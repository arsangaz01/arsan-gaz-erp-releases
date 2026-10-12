using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record TripVehicleSummary(string Plate, int LoadedCount, bool OnTrip, DateTime? ReturnedAt);

public sealed record ActiveTripSummary(
    int Id,
    string Title,
    DateTime StartedAt,
    string? Note,
    IReadOnlyList<TripVehicleSummary> Vehicles,
    int LoadedTotal,
    int DeliveredTotal,
    int ReturnedTotal);

public sealed record TripLoadResult(int Added, int Skipped, IReadOnlyList<string> Messages);

public sealed record TripCloseResult(int TripId, TripReconcileResult Reconcile, string Message);

public sealed class CylinderTripService
{
    private readonly CrudService _crud = new();

    public async Task<List<ActiveTripSummary>> ActiveTripsAsync()
    {
        await using ArsanGazDbContext db = new();
        List<CylinderTrip> trips = await db.CylinderTrips.AsNoTracking()
            .Where(x => x.Status == CylinderTripStatus.Active)
            .OrderByDescending(x => x.StartedAt)
            .Include(x => x.Vehicles)
            .Include(x => x.Cylinders)
            .ToListAsync();

        return trips.Select(TripSummary).ToList();
    }

    public async Task<CylinderTrip> StartTripAsync(IEnumerable<string?>? plates, string? note, string? createdBy)
    {
        await using ArsanGazDbContext db = new();
        DateTime now = DateTime.Now;
        CylinderTrip trip = new()
        {
            Title = $"Sefer {now:dd.MM.yyyy HH:mm}",
            StartedAt = now,
            Status = CylinderTripStatus.Active,
            Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
            CreatedBy = createdBy
        };
        db.CylinderTrips.Add(trip);
        await db.SaveChangesAsync();

        foreach (string plate in NormalizePlates(plates))
        {
            await AddVehicleInternalAsync(db, trip.Id, plate);
        }

        return trip;
    }

    public async Task AddVehicleAsync(int tripId, string? plate)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Geçerli bir plaka girin.");
        await using ArsanGazDbContext db = new();
        await EnsureActiveTripAsync(db, tripId);
        await AddVehicleInternalAsync(db, tripId, normalized);
    }

    public async Task<TripLoadResult> LoadCylindersAsync(int tripId, string? plate, IEnumerable<string?> codes, string? performedBy)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Yükleme için plaka zorunludur.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);
        if (clean.Count == 0)
        {
            throw new ArgumentException("Okutulmuş tüp yok.");
        }

        await using ArsanGazDbContext db = new();
        CylinderTrip trip = await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        HashSet<int> already = trip.Cylinders.Select(x => x.CylinderId).ToHashSet();
        int added = 0;
        int skipped = 0;
        List<string> messages = [];
        List<string> movedCodes = [];
        DateTime now = DateTime.Now;
        foreach (string code in clean)
        {
            Cylinder? cylinder = await _crud.FindCylinderByCodeAsync(code);
            if (cylinder is null)
            {
                messages.Add($"{code}: tüp bulunamadı.");
                skipped++;
                continue;
            }

            if (already.Contains(cylinder.Id))
            {
                messages.Add($"{cylinder.SerialNumber}: zaten sefer listesinde.");
                skipped++;
                continue;
            }

            db.CylinderTripCylinders.Add(new CylinderTripCylinder
            {
                TripId = tripId,
                CylinderId = cylinder.Id,
                VehiclePlate = normalized,
                SerialNumber = cylinder.SerialNumber,
                GasType = cylinder.GasType,
                LoadedAt = now
            });
            already.Add(cylinder.Id);
            movedCodes.Add(code);
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();
            string movementNote = $"Sefer #{tripId} · {normalized} · yükleme" + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
            await _crud.MoveCylindersAsync(movedCodes, CylinderStatus.Vehicle, null, movementNote, "Sefer yüklemesi", normalized, isFull: null);
        }

        return new TripLoadResult(added, skipped, messages);
    }

    public async Task DeliverCylindersAsync(int tripId, IEnumerable<string?> codes, int? customerId, string? note, string? performedBy)
    {
        if (customerId is null)
        {
            throw new ArgumentException("Müşteriye teslim için müşteri seçilmelidir.");
        }

        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);
        if (clean.Count == 0)
        {
            throw new ArgumentException("Okutulmuş tüp yok.");
        }

        await using ArsanGazDbContext db = new();
        await EnsureActiveTripAsync(db, tripId);
        Dictionary<string, int> codeToId = await CodeMapAsync(clean);
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders
            .Where(x => x.TripId == tripId && codeToId.Values.Contains(x.CylinderId))
            .ToListAsync();

        DateTime now = DateTime.Now;
        foreach (CylinderTripCylinder row in rows)
        {
            row.DeliveredAt ??= now;
        }

        await db.SaveChangesAsync();
        string movementNote = $"Sefer #{tripId} · teslim" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
            + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
        await _crud.MoveCylindersAsync(clean, CylinderStatus.Customer, customerId, movementNote, "Sefer teslimi", vehiclePlate: null, isFull: null);
    }

    public async Task<TripReconcileResult> ReturnCylindersAsync(int tripId, string? plate, IEnumerable<string?> codes, string? note, string? performedBy)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Dönüş okutması için plaka zorunludur.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);

        await using ArsanGazDbContext db = new();
        CylinderTrip trip = await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        Dictionary<string, int> codeToId = await CodeMapAsync(clean);
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders
            .Where(x => x.TripId == tripId && x.VehiclePlate == normalized)
            .ToListAsync();

        DateTime now = DateTime.Now;
        foreach (CylinderTripCylinder row in rows.Where(x => codeToId.Values.Contains(x.CylinderId)))
        {
            row.ReturnedAt ??= now;
        }

        CylinderTripVehicle? vehicle = trip.Vehicles.FirstOrDefault(x => x.Plate == normalized);
        if (vehicle is not null && vehicle.ReturnedAt is null && clean.Count > 0)
        {
            vehicle.ReturnedAt = now;
        }

        await db.SaveChangesAsync();

        if (clean.Count > 0)
        {
            string movementNote = $"Sefer #{tripId} · {normalized} · depo dönüşü" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
                + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
            await _crud.MoveCylindersAsync(clean, CylinderStatus.Warehouse, null, movementNote, "Sefer dönüşü", vehiclePlate: null, isFull: null);
        }

        return await ReconcileTripAsync(tripId, normalized, clean);
    }

    public async Task<TripReconcileResult> ReconcileTripAsync(int tripId, string? plate = null, IEnumerable<string>? returnScanCodes = null)
    {
        await using ArsanGazDbContext db = new();
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders.AsNoTracking()
            .Where(x => x.TripId == tripId)
            .ToListAsync();

        List<TripCylinderSnapshot> snapshots = rows.Select(x => new TripCylinderSnapshot(
            x.CylinderId, x.SerialNumber, x.GasType, x.VehiclePlate, x.DeliveredAt, x.ReturnedAt)).ToList();

        Dictionary<string, int> codeMap = await AllCodeMapAsync(db);
        return CylinderTripTracking.Reconcile(snapshots, returnScanCodes ?? [], codeMap, plate);
    }

    public async Task<TripCloseResult> CloseTripAsync(int tripId)
    {
        await using ArsanGazDbContext db = new();
        CylinderTrip trip = await db.CylinderTrips.Include(x => x.Vehicles).FirstOrDefaultAsync(x => x.Id == tripId)
            ?? throw new InvalidOperationException($"Sefer bulunamadı: {tripId}.");
        if (trip.Status == CylinderTripStatus.Closed)
        {
            throw new InvalidOperationException("Sefer zaten kapatılmış.");
        }

        trip.Status = CylinderTripStatus.Closed;
        trip.ClosedAt = DateTime.Now;
        foreach (CylinderTripVehicle vehicle in trip.Vehicles.Where(x => x.ReturnedAt is null))
        {
            vehicle.ReturnedAt = trip.ClosedAt;
        }

        await db.SaveChangesAsync();
        TripReconcileResult reconcile = await ReconcileTripAsync(tripId);
        string message = reconcile.HasAlerts
            ? "Sefer kapatıldı. " + reconcile.AlertText
            : "Sefer kapatıldı; eksik okutma uyarısı yok.";
        return new TripCloseResult(tripId, reconcile, message);
    }

    private static ActiveTripSummary TripSummary(CylinderTrip trip)
    {
        Dictionary<string, int> loadedByPlate = trip.Cylinders
            .GroupBy(x => x.VehiclePlate, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        List<TripVehicleSummary> vehicles = trip.Vehicles
            .OrderBy(x => x.Plate, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .Select(v => new TripVehicleSummary(
                v.Plate,
                loadedByPlate.GetValueOrDefault(v.Plate),
                v.ReturnedAt is null,
                v.ReturnedAt))
            .ToList();

        foreach (KeyValuePair<string, int> extra in loadedByPlate.Where(x => vehicles.All(v => !string.Equals(v.Plate, x.Key, StringComparison.OrdinalIgnoreCase))))
        {
            vehicles.Add(new TripVehicleSummary(extra.Key, extra.Value, true, null));
        }

        return new ActiveTripSummary(
            trip.Id,
            trip.Title,
            trip.StartedAt,
            trip.Note,
            vehicles,
            trip.Cylinders.Count,
            trip.Cylinders.Count(x => x.DeliveredAt is not null),
            trip.Cylinders.Count(x => x.ReturnedAt is not null));
    }

    private static IEnumerable<string> NormalizePlates(IEnumerable<string?>? plates)
    {
        if (plates is null)
        {
            yield break;
        }

        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (string? raw in plates)
        {
            string? plate = CylinderTracking.NormalizePlate(raw);
            if (plate is not null && seen.Add(plate))
            {
                yield return plate;
            }
        }
    }

    private static async Task<CylinderTrip> EnsureActiveTripAsync(ArsanGazDbContext db, int tripId)
    {
        CylinderTrip trip = await db.CylinderTrips
            .Include(x => x.Vehicles)
            .Include(x => x.Cylinders)
            .FirstOrDefaultAsync(x => x.Id == tripId)
            ?? throw new InvalidOperationException($"Sefer bulunamadı: {tripId}.");
        if (trip.Status != CylinderTripStatus.Active)
        {
            throw new InvalidOperationException("Sefer aktif değil.");
        }

        return trip;
    }

    private static async Task EnsureVehicleOnTripAsync(ArsanGazDbContext db, int tripId, string plate)
    {
        bool exists = await db.CylinderTripVehicles.AnyAsync(x => x.TripId == tripId && x.Plate == plate);
        if (!exists)
        {
            await AddVehicleInternalAsync(db, tripId, plate);
        }
    }

    private static async Task AddVehicleInternalAsync(ArsanGazDbContext db, int tripId, string plate)
    {
        bool exists = await db.CylinderTripVehicles.AnyAsync(x => x.TripId == tripId && x.Plate == plate);
        if (exists)
        {
            return;
        }

        db.CylinderTripVehicles.Add(new CylinderTripVehicle { TripId = tripId, Plate = plate, AddedAt = DateTime.Now });
        await db.SaveChangesAsync();
    }

    private async Task<Dictionary<string, int>> CodeMapAsync(IReadOnlyList<string> codes)
    {
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (string code in codes)
        {
            Cylinder? cylinder = await _crud.FindCylinderByCodeAsync(code);
            if (cylinder is not null)
            {
                map[code] = cylinder.Id;
                if (!string.IsNullOrWhiteSpace(cylinder.Barcode))
                {
                    map[cylinder.Barcode] = cylinder.Id;
                }

                map[cylinder.SerialNumber] = cylinder.Id;
            }
        }

        return map;
    }

    private static async Task<Dictionary<string, int>> AllCodeMapAsync(ArsanGazDbContext db)
    {
        var rows = await db.Cylinders.AsNoTracking()
            .Where(x => x.IsActive)
            .Select(x => new { x.Id, x.SerialNumber, x.Barcode })
            .ToListAsync();
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            map[row.SerialNumber] = row.Id;
            if (!string.IsNullOrWhiteSpace(row.Barcode))
            {
                map[row.Barcode] = row.Id;
            }
        }

        return map;
    }
}
