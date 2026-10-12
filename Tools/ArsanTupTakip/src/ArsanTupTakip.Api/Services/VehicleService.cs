using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArsanTupTakip.Api;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

public sealed record VehicleListItem(
    int Id,
    string Plate,
    string? Name,
    string? VehicleType,
    string? DriverName,
    int? CylinderCapacity,
    string? Note,
    bool IsActive,
    int CylindersOnBoard,
    int? ActiveTripId,
    string? ActiveTripTitle,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    uint Version = 0);

public sealed record VehicleTripItem(int TripId, string Title, bool Active, DateTime StartedAt, DateTime? ClosedAt, DateTime? ReturnedAt,
    int Loaded, int Delivered, int Returned, int Missing);

public sealed record VehicleDetail(VehicleListItem Vehicle, IReadOnlyList<GasTypeGroup> OnBoard, string? CapacityWarning, IReadOnlyList<VehicleTripItem> Trips);

// Vehicle registry. Trips and cylinders keep referring to vehicles by plate text, so this only adds descriptive data,
// the active flag and per-vehicle views on top of the existing plate based tracking.
public sealed class VehicleService
{
    private const int TripHistoryCount = 30;
    private static readonly StringComparer TurkishOrder = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true);

    public async Task<List<VehicleListItem>> ListAsync(bool includeInactive)
    {
        await using TupTakipDbContext db = new();
        await RegisterLoosePlatesAsync(db);
        List<Vehicle> vehicles = await db.Vehicles.AsNoTracking().Where(x => includeInactive || x.IsActive).ToListAsync();
        Dictionary<string, int> onBoard = await OnBoardCountsAsync(db);
        Dictionary<string, (int Id, string Title)> activeTrips = await ActiveTripByPlateAsync(db);
        return vehicles
            .OrderByDescending(x => x.IsActive)
            .ThenBy(x => x.Plate, TurkishOrder)
            .Select(x => ListItem(x, onBoard, activeTrips))
            .ToList();
    }

    public async Task<VehicleDetail?> DetailAsync(int id)
    {
        await using TupTakipDbContext db = new();
        Vehicle? vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (vehicle is null)
        {
            return null;
        }

        string plate = vehicle.Plate;
        List<Cylinder> cylinders = await db.Cylinders.AsNoTracking().Include(x => x.CurrentCustomer)
            .Where(x => x.IsActive && x.Status == CylinderStatus.Vehicle && x.VehiclePlate == plate)
            .ToListAsync();

        List<int> tripIds = await db.CylinderTripVehicles.AsNoTracking().Where(x => x.Plate == plate).Select(x => x.TripId)
            .Union(db.CylinderTripCylinders.AsNoTracking().Where(x => x.VehiclePlate == plate).Select(x => x.TripId))
            .OrderByDescending(x => x)
            .Take(TripHistoryCount)
            .ToListAsync();
        List<CylinderTrip> trips = await db.CylinderTrips.AsNoTracking().Include(x => x.Vehicles).Include(x => x.Cylinders)
            .Where(x => tripIds.Contains(x.Id))
            .ToListAsync();

        Dictionary<int, DateTime> loadedAt = trips
            .Where(x => x.Status == CylinderTripStatus.Active)
            .SelectMany(x => x.Cylinders)
            .Where(x => string.Equals(x.VehiclePlate, plate, StringComparison.OrdinalIgnoreCase) && x.DeliveredAt is null && x.ReturnedAt is null)
            .GroupBy(x => x.CylinderId)
            .ToDictionary(g => g.Key, g => g.Max(x => x.LoadedAt));

        List<LoadCylinderItem> items = cylinders.Select(c => Item(c, loadedAt.TryGetValue(c.Id, out DateTime at) ? at : null, "Araçta", false)).ToList();
        List<VehicleTripItem> history = trips
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.Id)
            .Select(trip => TripItem(trip, plate))
            .ToList();

        Dictionary<string, (int Id, string Title)> activeTrips = await ActiveTripByPlateAsync(db);
        VehicleListItem summary = ListItem(vehicle, new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [plate] = cylinders.Count }, activeTrips);
        return new VehicleDetail(summary, VehicleTracking.GroupByGas(items), VehicleTracking.CapacityWarning(cylinders.Count, vehicle.CylinderCapacity), history);
    }

    public async Task<Vehicle> CreateAsync(VehicleInput input)
    {
        string plate = CylinderTracking.NormalizePlate(input.Plate) ?? throw new ArgumentException("Plaka zorunludur.");
        if (VehicleTracking.InputError(input) is string error)
        {
            throw new ArgumentException(error);
        }

        await using TupTakipDbContext db = new();
        Vehicle? existing = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.Plate == plate);
        if (existing is not null)
        {
            throw new InvalidOperationException(existing.IsActive
                ? $"{plate} plakalı araç zaten kayıtlı."
                : $"{plate} plakalı araç pasif olarak kayıtlı; listede \"Pasifleri göster\" ile bulup aktif edin.");
        }

        Vehicle vehicle = new()
        {
            Plate = plate,
            Name = VehicleTracking.Clean(input.Name),
            VehicleType = VehicleTracking.Clean(input.VehicleType),
            DriverName = VehicleTracking.Clean(input.DriverName),
            CylinderCapacity = input.CylinderCapacity is > 0 ? input.CylinderCapacity : null,
            Note = VehicleTracking.Clean(input.Note),
            IsActive = true,
            CreatedAt = DateTime.Now
        };
        db.Vehicles.Add(vehicle);
        await db.SaveChangesAsync();
        return vehicle;
    }

    public async Task<Vehicle> UpdateAsync(int id, VehicleInput input, uint? expectedVersion = null)
    {
        if (VehicleTracking.InputError(input) is string error)
        {
            throw new ArgumentException(error);
        }

        await using TupTakipDbContext db = new();
        Vehicle vehicle = await db.Vehicles.FirstOrDefaultAsync(x => x.Id == id) ?? throw new InvalidOperationException("Araç bulunamadı.");
        if (expectedVersion is uint seen && seen != vehicle.Version)
        {
            throw new RecordChangedException();
        }
        if (!string.IsNullOrWhiteSpace(input.Plate) && CylinderTracking.NormalizePlate(input.Plate) != vehicle.Plate)
        {
            throw new ArgumentException("Kayıtlı aracın plakası değiştirilemez (seferler ve tüp hareketleri plakaya bağlı). Yeni plakayı ayrı araç olarak ekleyip eskisini pasife alın.");
        }

        if (input.IsActive == false && vehicle.IsActive)
        {
            string plate = vehicle.Plate;
            bool onTrip = await db.CylinderTripVehicles.AnyAsync(x => x.Plate == plate && x.ReturnedAt == null && x.Trip.Status == CylinderTripStatus.Active);
            if (onTrip)
            {
                throw new InvalidOperationException($"{plate} aktif bir seferde; önce depo dönüşünü okutun veya seferi kapatın, sonra pasife alın.");
            }
        }

        vehicle.Name = VehicleTracking.Clean(input.Name);
        vehicle.VehicleType = VehicleTracking.Clean(input.VehicleType);
        vehicle.DriverName = VehicleTracking.Clean(input.DriverName);
        vehicle.CylinderCapacity = input.CylinderCapacity is > 0 ? input.CylinderCapacity : null;
        vehicle.Note = VehicleTracking.Clean(input.Note);
        if (input.IsActive is bool active)
        {
            vehicle.IsActive = active;
        }

        vehicle.UpdatedAt = DateTime.Now;
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new RecordChangedException();
        }

        return vehicle;
    }

    // Trips may still receive plates that are not in the registry (older app versions); they are registered on the fly.
    // A plate registered as inactive cannot be sent on a new trip.
    internal static async Task EnsureUsableAsync(TupTakipDbContext db, string plate)
    {
        Vehicle? vehicle = await db.Vehicles.FirstOrDefaultAsync(x => x.Plate == plate);
        if (vehicle is null)
        {
            db.Vehicles.Add(new Vehicle { Plate = plate, IsActive = true, CreatedAt = DateTime.Now });
            await db.SaveChangesAsync();
            return;
        }

        if (!vehicle.IsActive)
        {
            throw new InvalidOperationException($"{plate} pasif araç; sefere eklemek için Araç İşlemleri'nden aktif edin.");
        }
    }

    internal static LoadCylinderItem Item(Cylinder c, DateTime? loadedAt, string state, bool alarm) => new(
        c.Id,
        c.SerialNumber,
        string.IsNullOrWhiteSpace(c.Barcode) ? c.SerialNumber : c.Barcode,
        c.GasType,
        $"{c.Capacity.ToString("0.##", CultureInfo.GetCultureInfo("tr-TR"))} {c.CapacityUnit}",
        c.IsFull,
        CylinderTracking.FullnessName(c.IsFull),
        CylinderTracking.StatusName((int)c.Status),
        c.CurrentCustomer?.CompanyName,
        c.VehiclePlate,
        c.LastMovementAt,
        c.TestDueDate,
        loadedAt,
        state,
        alarm);

    private static VehicleTripItem TripItem(CylinderTrip trip, string plate)
    {
        HashSet<string> returnedPlates = CylinderTripService.ReturnedPlates(trip);
        List<CylinderTripCylinder> rows = trip.Cylinders.Where(x => string.Equals(x.VehiclePlate, plate, StringComparison.OrdinalIgnoreCase)).ToList();
        CylinderTripVehicle? vehicle = trip.Vehicles.FirstOrDefault(x => string.Equals(x.Plate, plate, StringComparison.OrdinalIgnoreCase));
        return new VehicleTripItem(trip.Id, trip.Title, trip.Status == CylinderTripStatus.Active, trip.StartedAt, trip.ClosedAt, vehicle?.ReturnedAt,
            rows.Count, rows.Count(x => x.DeliveredAt is not null), rows.Count(x => x.ReturnedAt is not null),
            rows.Count(x => CylinderTripService.IsMissing(x, returnedPlates)));
    }

    private static VehicleListItem ListItem(Vehicle x, IReadOnlyDictionary<string, int> onBoard, IReadOnlyDictionary<string, (int Id, string Title)> activeTrips)
    {
        (int Id, string Title)? trip = activeTrips.TryGetValue(x.Plate, out (int Id, string Title) found) ? found : null;
        return new VehicleListItem(x.Id, x.Plate, x.Name, x.VehicleType, x.DriverName, x.CylinderCapacity, x.Note, x.IsActive,
            onBoard.GetValueOrDefault(x.Plate), trip?.Id, trip?.Title, x.CreatedAt, x.UpdatedAt, x.Version);
    }

    private static async Task<Dictionary<string, int>> OnBoardCountsAsync(TupTakipDbContext db)
    {
        var rows = await db.Cylinders.AsNoTracking()
            .Where(x => x.IsActive && x.Status == CylinderStatus.Vehicle && x.VehiclePlate != null)
            .GroupBy(x => x.VehiclePlate!)
            .Select(g => new { Plate = g.Key, Count = g.Count() })
            .ToListAsync();
        Dictionary<string, int> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            result[row.Plate.Trim()] = result.GetValueOrDefault(row.Plate.Trim()) + row.Count;
        }

        return result;
    }

    private static async Task<Dictionary<string, (int Id, string Title)>> ActiveTripByPlateAsync(TupTakipDbContext db)
    {
        var rows = await db.CylinderTripVehicles.AsNoTracking()
            .Where(x => x.ReturnedAt == null && x.Trip.Status == CylinderTripStatus.Active)
            .Select(x => new { x.Plate, x.TripId, x.Trip.Title, x.Trip.StartedAt })
            .ToListAsync();
        Dictionary<string, (int Id, string Title)> result = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows.OrderBy(x => x.StartedAt))
        {
            result[row.Plate] = (row.TripId, row.Title);
        }

        return result;
    }

    // Cylinders moved onto a typed plate (desktop ERP, panel move) after the startup migration still get a registry row.
    private static async Task RegisterLoosePlatesAsync(TupTakipDbContext db)
    {
        List<string> plates = await db.Cylinders.AsNoTracking()
            .Where(x => x.Status == CylinderStatus.Vehicle && x.VehiclePlate != null && x.VehiclePlate != "")
            .Select(x => x.VehiclePlate!)
            .Distinct()
            .ToListAsync();
        if (plates.Count == 0)
        {
            return;
        }

        HashSet<string> known = (await db.Vehicles.AsNoTracking().Select(x => x.Plate).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);
        List<string> missing = plates.Select(x => x.Trim()).Where(x => x.Length > 0 && !known.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (missing.Count == 0)
        {
            return;
        }

        foreach (string plate in missing)
        {
            db.Vehicles.Add(new Vehicle { Plate = plate, IsActive = true, CreatedAt = DateTime.Now });
        }

        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another process registered the same plate at the same moment; the next listing shows it.
            db.ChangeTracker.Clear();
        }
    }
}
