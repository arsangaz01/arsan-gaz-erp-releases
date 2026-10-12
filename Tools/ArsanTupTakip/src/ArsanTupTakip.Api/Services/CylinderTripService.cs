using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

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

// Web panel views. Missing = neither delivered nor returned although its vehicle came back (or the trip was closed);
// OnVehicle = the same while the vehicle is still out, which is not an error yet.
public sealed record TripVehicleView(string Plate, bool OnTrip, DateTime? ReturnedAt, int Loaded, int Delivered, int Returned, int OnVehicle, int Missing);

public sealed record TripListItem(
    int Id,
    string Title,
    bool Active,
    DateTime StartedAt,
    DateTime? ClosedAt,
    string? Note,
    string? CreatedBy,
    IReadOnlyList<TripVehicleView> Vehicles,
    int Loaded,
    int Delivered,
    int Returned,
    int OnVehicle,
    int Missing,
    IReadOnlyList<TripGasShortage> MissingByGas);

public sealed record TripCylinderView(
    int CylinderId, string SerialNumber, string GasType, string Plate, DateTime LoadedAt, DateTime? DeliveredAt, DateTime? ReturnedAt, bool Missing);

public sealed record TripUnexpectedReturn(int CylinderId, string SerialNumber, string GasType, string? Plate, DateTime At);

public sealed record TripLoadView(int TripId, string Title, bool Active, string? Plate, int Total, int OnVehicle, int Missing, IReadOnlyList<GasTypeGroup> Groups);

public sealed record TripDetail(TripListItem Trip, IReadOnlyList<TripCylinderView> Cylinders, IReadOnlyList<TripUnexpectedReturn> UnexpectedReturns);

public sealed class CylinderTripService
{
    private readonly CrudService _crud = new();

    public async Task<List<ActiveTripSummary>> ActiveTripsAsync()
    {
        await using TupTakipDbContext db = new();
        List<CylinderTrip> trips = await db.CylinderTrips.AsNoTracking()
            .Where(x => x.Status == CylinderTripStatus.Active)
            .OrderByDescending(x => x.StartedAt)
            .Include(x => x.Vehicles)
            .Include(x => x.Cylinders)
            .ToListAsync();

        return trips.Select(TripSummary).ToList();
    }

    public async Task<List<TripListItem>> TripsAsync(bool? active, int take = 100)
    {
        await using TupTakipDbContext db = new();
        IQueryable<CylinderTrip> query = db.CylinderTrips.AsNoTracking().Include(x => x.Vehicles).Include(x => x.Cylinders);
        if (active is bool wanted)
        {
            CylinderTripStatus status = wanted ? CylinderTripStatus.Active : CylinderTripStatus.Closed;
            query = query.Where(x => x.Status == status);
        }

        List<CylinderTrip> trips = await query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Take(take).ToListAsync();
        return trips.Select(TripView).ToList();
    }

    public async Task<TripDetail?> TripDetailAsync(int tripId)
    {
        await using TupTakipDbContext db = new();
        CylinderTrip? trip = await db.CylinderTrips.AsNoTracking().Include(x => x.Vehicles).Include(x => x.Cylinders)
            .FirstOrDefaultAsync(x => x.Id == tripId);
        if (trip is null)
        {
            return null;
        }

        HashSet<string> returnedPlates = ReturnedPlates(trip);
        List<TripCylinderView> cylinders = trip.Cylinders
            .OrderBy(x => x.VehiclePlate, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.GasType, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .Select(x => new TripCylinderView(x.CylinderId, x.SerialNumber, x.GasType, x.VehiclePlate, x.LoadedAt, x.DeliveredAt, x.ReturnedAt,
                IsMissing(x, returnedPlates)))
            .ToList();

        // Return scans of cylinders that were never loaded on this trip are only kept as movements ("Sefer #id · plate · depo dönüşü").
        string prefix = $"Sefer #{tripId} · ";
        HashSet<int> loadedIds = trip.Cylinders.Select(x => x.CylinderId).ToHashSet();
        List<CylinderMovement> returns = await db.CylinderMovements.AsNoTracking().Include(x => x.Cylinder)
            .Where(x => x.MovementType == "Sefer dönüşü" && x.Description != null && x.Description.StartsWith(prefix))
            .OrderBy(x => x.MovementDate)
            .ToListAsync();
        List<TripUnexpectedReturn> unexpected = returns
            .Where(x => !loadedIds.Contains(x.CylinderId))
            .GroupBy(x => x.CylinderId)
            .Select(g => g.First())
            .Select(x => new TripUnexpectedReturn(x.CylinderId, x.Cylinder.SerialNumber, x.Cylinder.GasType,
                x.Description!.Substring(prefix.Length).Split(" · ")[0], x.MovementDate))
            .ToList();

        return new TripDetail(TripView(trip), cylinders, unexpected);
    }

    // Load list of a trip (optionally one vehicle) grouped by gas type, with the cylinder details behind each group.
    public async Task<TripLoadView?> TripLoadAsync(int tripId, string? plate)
    {
        string? normalized = CylinderTracking.NormalizePlate(plate);
        await using TupTakipDbContext db = new();
        CylinderTrip? trip = await db.CylinderTrips.AsNoTracking().Include(x => x.Vehicles).Include(x => x.Cylinders)
            .FirstOrDefaultAsync(x => x.Id == tripId);
        if (trip is null)
        {
            return null;
        }

        HashSet<string> returnedPlates = ReturnedPlates(trip);
        List<CylinderTripCylinder> rows = trip.Cylinders
            .Where(x => normalized is null || string.Equals(x.VehiclePlate, normalized, StringComparison.OrdinalIgnoreCase))
            .ToList();
        List<int> ids = rows.Select(x => x.CylinderId).Distinct().ToList();
        Dictionary<int, Cylinder> cylinders = await db.Cylinders.AsNoTracking().Include(x => x.CurrentCustomer)
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);

        List<LoadCylinderItem> items = rows.Select(row =>
        {
            bool missing = IsMissing(row, returnedPlates);
            string state = VehicleTracking.TripState(row.DeliveredAt, row.ReturnedAt, missing);
            return cylinders.TryGetValue(row.CylinderId, out Cylinder? cylinder)
                ? VehicleService.Item(cylinder, row.LoadedAt, state, missing) with { Plate = row.VehiclePlate, GasType = row.GasType is { Length: > 0 } ? row.GasType : cylinder.GasType }
                : new LoadCylinderItem(row.CylinderId, row.SerialNumber, row.SerialNumber, row.GasType, "-", null, "-", "-", null, row.VehiclePlate,
                    null, null, row.LoadedAt, state, missing);
        }).ToList();

        int onVehicle = rows.Count(x => IsOpen(x) && !IsMissing(x, returnedPlates));
        return new TripLoadView(trip.Id, trip.Title, trip.Status == CylinderTripStatus.Active, normalized, items.Count, onVehicle,
            items.Count(x => x.Alarm), VehicleTracking.GroupByGas(items));
    }

    internal static HashSet<string> ReturnedPlates(CylinderTrip trip) =>
        trip.Status == CylinderTripStatus.Closed
            ? trip.Cylinders.Select(x => x.VehiclePlate).Concat(trip.Vehicles.Select(x => x.Plate)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : trip.Vehicles.Where(x => x.ReturnedAt is not null).Select(x => x.Plate).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsOpen(CylinderTripCylinder row) => row.DeliveredAt is null && row.ReturnedAt is null;

    internal static bool IsMissing(CylinderTripCylinder row, HashSet<string> returnedPlates) => IsOpen(row) && returnedPlates.Contains(row.VehiclePlate);

    private static TripListItem TripView(CylinderTrip trip)
    {
        HashSet<string> returnedPlates = ReturnedPlates(trip);
        List<string> plates = trip.Vehicles.Select(x => x.Plate)
            .Concat(trip.Cylinders.Select(x => x.VehiclePlate))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .ToList();
        List<TripVehicleView> vehicles = plates.Select(plate =>
        {
            List<CylinderTripCylinder> rows = trip.Cylinders.Where(x => string.Equals(x.VehiclePlate, plate, StringComparison.OrdinalIgnoreCase)).ToList();
            CylinderTripVehicle? vehicle = trip.Vehicles.FirstOrDefault(x => string.Equals(x.Plate, plate, StringComparison.OrdinalIgnoreCase));
            int missing = rows.Count(x => IsMissing(x, returnedPlates));
            return new TripVehicleView(plate, !returnedPlates.Contains(plate), vehicle?.ReturnedAt, rows.Count,
                rows.Count(x => x.DeliveredAt is not null), rows.Count(x => x.ReturnedAt is not null),
                rows.Count(IsOpen) - missing, missing);
        }).ToList();

        List<TripGasShortage> missingByGas = trip.Cylinders.Where(x => IsMissing(x, returnedPlates))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GasType) ? "Belirsiz" : x.GasType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TripGasShortage(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        return new TripListItem(trip.Id, trip.Title, trip.Status == CylinderTripStatus.Active, trip.StartedAt, trip.ClosedAt, trip.Note, trip.CreatedBy,
            vehicles, trip.Cylinders.Count, trip.Cylinders.Count(x => x.DeliveredAt is not null), trip.Cylinders.Count(x => x.ReturnedAt is not null),
            vehicles.Sum(x => x.OnVehicle), vehicles.Sum(x => x.Missing), missingByGas);
    }

    public async Task<CylinderTrip> StartTripAsync(IEnumerable<string?>? plates, string? note, string? createdBy)
    {
        await using TupTakipDbContext db = new();
        List<string> normalized = NormalizePlates(plates).ToList();
        foreach (string plate in normalized)
        {
            await VehicleService.EnsureUsableAsync(db, plate);
        }

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

        foreach (string plate in normalized)
        {
            await AddVehicleInternalAsync(db, trip.Id, plate);
        }

        return trip;
    }

    public async Task AddVehicleAsync(int tripId, string? plate)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Geçerli bir plaka girin.");
        await using TupTakipDbContext db = new();
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

        await using TupTakipDbContext db = new();
        CylinderTrip trip = await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        HashSet<int> already = trip.Cylinders.Select(x => x.CylinderId).ToHashSet();
        int added = 0;
        int skipped = 0;
        List<string> messages = [];
        List<(string Code, Cylinder Cylinder)> candidates = [];
        foreach (string code in clean)
        {
            Cylinder? cylinder = await _crud.FindCylinderByCodeAsync(code);
            if (cylinder is null)
            {
                messages.Add($"{code}: tüp bulunamadı.");
                skipped++;
                continue;
            }

            if (!already.Add(cylinder.Id))
            {
                messages.Add($"{cylinder.SerialNumber}: zaten sefer listesinde.");
                skipped++;
                continue;
            }

            candidates.Add((code, cylinder));
        }

        if (candidates.Count == 0)
        {
            return new TripLoadResult(added, skipped, messages);
        }

        // The cylinder must really be moved onto the vehicle before it counts as loaded; a refused move (e.g. it is
        // registered to a customer) would otherwise leave a trip row that later shows up as missing.
        string movementNote = $"Sefer #{tripId} · {normalized} · yükleme" + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
        List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(
            candidates.Select(x => x.Code), CylinderStatus.Vehicle, null, movementNote, "Sefer yüklemesi", normalized, isFull: null);
        Dictionary<string, CylinderBulkResult> byCode = moved.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        DateTime now = DateTime.Now;
        foreach ((string code, Cylinder cylinder) in candidates)
        {
            if (byCode.GetValueOrDefault(code) is not { Ok: true })
            {
                messages.Add($"{cylinder.SerialNumber}: araca alınamadı ({byCode.GetValueOrDefault(code)?.Message ?? "bilinmeyen hata"}).");
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
            added++;
        }

        if (added > 0)
        {
            await db.SaveChangesAsync();
        }

        return new TripLoadResult(added, skipped, messages);
    }

    public async Task<List<CylinderBulkResult>> DeliverCylindersAsync(int tripId, IEnumerable<string?> codes, int? customerId, string? note, string? performedBy)
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

        await using TupTakipDbContext db = new();
        await EnsureActiveTripAsync(db, tripId);
        string movementNote = $"Sefer #{tripId} · teslim" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
            + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
        List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(clean, CylinderStatus.Customer, customerId, movementNote, "Sefer teslimi", vehiclePlate: null, isFull: null);
        await MarkTripRowsAsync(db, tripId, null, moved, row => row.DeliveredAt ??= DateTime.Now);
        return moved;
    }

    public async Task<TripReconcileResult> ReturnCylindersAsync(int tripId, string? plate, IEnumerable<string?> codes, string? note, string? performedBy)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Dönüş okutması için plaka zorunludur.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);

        await using TupTakipDbContext db = new();
        await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        if (clean.Count > 0)
        {
            string movementNote = $"Sefer #{tripId} · {normalized} · depo dönüşü" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
                + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
            List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(clean, CylinderStatus.Warehouse, null, movementNote, "Sefer dönüşü", vehiclePlate: null, isFull: null);
            await MarkTripRowsAsync(db, tripId, normalized, moved, row => row.ReturnedAt ??= DateTime.Now);

            CylinderTripVehicle? vehicle = await db.CylinderTripVehicles.FirstOrDefaultAsync(x => x.TripId == tripId && x.Plate == normalized);
            if (vehicle is not null && vehicle.ReturnedAt is null)
            {
                vehicle.ReturnedAt = DateTime.Now;
                await db.SaveChangesAsync();
            }
        }

        return await ReconcileTripAsync(tripId, normalized, clean);
    }

    // Only cylinders whose movement was actually saved are marked delivered/returned on the trip.
    private async Task MarkTripRowsAsync(TupTakipDbContext db, int tripId, string? plate, IEnumerable<CylinderBulkResult> moved, Action<CylinderTripCylinder> mark)
    {
        List<string> okCodes = moved.Where(x => x.Ok).Select(x => x.Code).ToList();
        if (okCodes.Count == 0)
        {
            return;
        }

        HashSet<int> ids = (await CodeMapAsync(okCodes)).Values.ToHashSet();
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders
            .Where(x => x.TripId == tripId && ids.Contains(x.CylinderId) && (plate == null || x.VehiclePlate == plate))
            .ToListAsync();
        foreach (CylinderTripCylinder row in rows)
        {
            mark(row);
        }

        await db.SaveChangesAsync();
    }

    public async Task<TripReconcileResult> ReconcileTripAsync(int tripId, string? plate = null, IEnumerable<string>? returnScanCodes = null)
    {
        await using TupTakipDbContext db = new();
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
        await using TupTakipDbContext db = new();
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

    private static async Task<CylinderTrip> EnsureActiveTripAsync(TupTakipDbContext db, int tripId)
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

    private static async Task EnsureVehicleOnTripAsync(TupTakipDbContext db, int tripId, string plate)
    {
        bool exists = await db.CylinderTripVehicles.AnyAsync(x => x.TripId == tripId && x.Plate == plate);
        if (!exists)
        {
            await AddVehicleInternalAsync(db, tripId, plate);
        }
    }

    private static async Task AddVehicleInternalAsync(TupTakipDbContext db, int tripId, string plate)
    {
        bool exists = await db.CylinderTripVehicles.AnyAsync(x => x.TripId == tripId && x.Plate == plate);
        if (exists)
        {
            return;
        }

        await VehicleService.EnsureUsableAsync(db, plate);
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

    private static async Task<Dictionary<string, int>> AllCodeMapAsync(TupTakipDbContext db)
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
