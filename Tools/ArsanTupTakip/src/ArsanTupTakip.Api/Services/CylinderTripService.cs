using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

// OnVehicle = cylinders of this trip still on the vehicle (neither delivered nor returned).
public sealed record TripVehicleSummary(string Plate, int LoadedCount, bool OnTrip, DateTime? ReturnedAt, int OnVehicle = 0);

public sealed record ActiveTripSummary(
    int Id,
    string Title,
    DateTime StartedAt,
    string? Note,
    IReadOnlyList<TripVehicleSummary> Vehicles,
    int LoadedTotal,
    int DeliveredTotal,
    int ReturnedTotal,
    bool RequireReasons = false,
    int PickedUpTotal = 0);

public sealed record TripLoadResult(int Added, int Skipped, IReadOnlyList<string> Messages);

// Web panel views. Missing = neither delivered nor returned although its vehicle came back (or the trip was closed);
// OnVehicle = the same while the vehicle is still out, which is not an error yet.
public sealed record TripVehicleView(string Plate, bool OnTrip, DateTime? ReturnedAt, int Loaded, int Delivered, int Returned, int OnVehicle, int Missing, int PickedUp = 0);

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
    IReadOnlyList<TripGasShortage> MissingByGas,
    int MissingUnexplained = 0,
    int MissingUnchecked = 0,
    bool RequireReasons = false,
    int PickedUp = 0);

public sealed record TripCylinderView(
    int CylinderId, string SerialNumber, string GasType, string Plate, DateTime LoadedAt, DateTime? DeliveredAt, DateTime? ReturnedAt, bool Missing,
    string? Reason = null, string? ReasonLabel = null, string? Note = null, DateTime? ReasonAt = null, string? ReasonBy = null,
    DateTime? CheckedAt = null, string? CheckedBy = null, bool Pickup = false);

public sealed record TripReasonResult(int Updated, IReadOnlyList<string> Messages);

// One missing cylinder in the loss control list, with where the cylinder is now and whether it was scanned after the trip.
public sealed record TripMissingItem(
    int TripId, string TripTitle, bool TripActive, DateTime TripStartedAt, int CylinderId, string SerialNumber, string GasType, string Plate,
    DateTime LoadedAt, string? Reason, string? ReasonLabel, string? Note, DateTime? ReasonAt, string? ReasonBy, DateTime? CheckedAt, string? CheckedBy,
    string CurrentStatus, string? CurrentCustomer, string? CurrentPlate, DateTime? LastMovementAt, bool SeenAfterTrip);

public sealed record TripUnexpectedReturn(int CylinderId, string SerialNumber, string GasType, string? Plate, DateTime At);

public sealed record TripLoadView(int TripId, string Title, bool Active, string? Plate, int Total, int OnVehicle, int Missing, IReadOnlyList<GasTypeGroup> Groups);

public sealed record TripDetail(TripListItem Trip, IReadOnlyList<TripCylinderView> Cylinders, IReadOnlyList<TripUnexpectedReturn> UnexpectedReturns,
    IReadOnlyList<CustomerTripBalance>? Customers = null);

// Delivered / Collected: on this trip. Holds*: what the customer has now according to the scans.
public sealed record CustomerTripBalance(int CustomerId, string CustomerName, int Delivered, int Collected, int Holds, int HoldsFull, int HoldsEmpty)
{
    public int Net => Delivered - Collected;

    public string Text =>
        $"{CustomerName}: bu seferde verilen {Delivered}, alınan {Collected}"
        + (Net == 0 ? " (denk)" : Net > 0 ? $" (müşteride +{Net})" : $" (müşteriden {-Net} fazla alındı)")
        + $" · müşteride şimdi {Holds} tüp ({HoldsFull} dolu, {HoldsEmpty} boş"
        + (Holds - HoldsFull - HoldsEmpty > 0 ? $", {Holds - HoldsFull - HoldsEmpty} belirsiz" : string.Empty) + ")";
}

public sealed record TripPickupResult(IReadOnlyList<CylinderBulkResult> Results, CustomerTripBalance Balance);

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
                IsMissing(x, returnedPlates), x.MissingReason, x.MissingReason is null ? null : TripMissingReasons.Label(x.MissingReason),
                x.MissingNote, x.ReasonAt, x.ReasonBy, x.CheckedAt, x.CheckedBy, x.PickupCustomerId is not null))
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

        return new TripDetail(TripView(trip), cylinders, unexpected, await CustomerBalancesAsync(db, trip));
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
            return new TripVehicleView(plate, !returnedPlates.Contains(plate), vehicle?.ReturnedAt, rows.Count(x => x.PickupCustomerId is null),
                rows.Count(x => x.DeliveredAt is not null), rows.Count(x => x.ReturnedAt is not null),
                rows.Count(IsOpen) - missing, missing, rows.Count(x => x.PickupCustomerId is not null));
        }).ToList();

        List<TripGasShortage> missingByGas = trip.Cylinders.Where(x => IsMissing(x, returnedPlates))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GasType) ? "Belirsiz" : x.GasType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TripGasShortage(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        List<CylinderTripCylinder> missingRows = trip.Cylinders.Where(x => IsMissing(x, returnedPlates)).ToList();
        return new TripListItem(trip.Id, trip.Title, trip.Status == CylinderTripStatus.Active, trip.StartedAt, trip.ClosedAt, trip.Note, trip.CreatedBy,
            vehicles, trip.Cylinders.Count(x => x.PickupCustomerId is null), trip.Cylinders.Count(x => x.DeliveredAt is not null), trip.Cylinders.Count(x => x.ReturnedAt is not null),
            vehicles.Sum(x => x.OnVehicle), vehicles.Sum(x => x.Missing), missingByGas,
            missingRows.Count(x => x.MissingReason is null), missingRows.Count(x => x.MissingReason is not null && x.CheckedAt is null),
            trip.RequireReasons, trip.Cylinders.Count(x => x.PickupCustomerId is not null));
    }

    // Writes where / why loaded cylinders were neither delivered nor scanned back at the depot. Works on active and
    // closed trips; changing the reason clears an earlier check so the new explanation is reviewed again.
    public async Task<TripReasonResult> ExplainMissingAsync(
        int tripId, IEnumerable<int> cylinderIds, string? reasonKey, string? note, string? performedBy, int? customerId = null)
    {
        string? customerName = null;
        await using TupTakipDbContext db = new();
        if (customerId is int pickedCustomer && pickedCustomer > 0)
        {
            customerName = await db.Customers.AsNoTracking().Where(x => x.Id == pickedCustomer).Select(x => x.CompanyName).FirstOrDefaultAsync()
                ?? throw new ArgumentException($"Müşteri bulunamadı: {pickedCustomer}.");
        }

        (TripMissingReason reason, string? cleanNote) = TripMissingReasons.Validate(reasonKey, note, customerName);
        HashSet<int> ids = cylinderIds.ToHashSet();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Gerekçe yazılacak tüp seçin.");
        }

        if (!await db.CylinderTrips.AnyAsync(x => x.Id == tripId))
        {
            throw new InvalidOperationException($"Sefer bulunamadı: {tripId}.");
        }

        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders.Where(x => x.TripId == tripId && ids.Contains(x.CylinderId)).ToListAsync();
        List<string> messages = [];
        int updated = 0;
        DateTime now = DateTime.Now;
        foreach (int id in ids.Where(id => rows.All(r => r.CylinderId != id)))
        {
            messages.Add($"Tüp #{id}: bu sefere yüklenmemiş.");
        }

        foreach (CylinderTripCylinder row in rows)
        {
            if (!IsOpen(row))
            {
                messages.Add($"{row.SerialNumber}: teslim veya dönüş okutması var; gerekçe gerekmez.");
                continue;
            }

            bool changed = row.MissingReason != reason.Key || row.MissingNote != cleanNote;
            row.MissingReason = reason.Key;
            row.MissingNote = cleanNote;
            row.ReasonAt = now;
            row.ReasonBy = performedBy;
            if (changed)
            {
                row.CheckedAt = null;
                row.CheckedBy = null;
            }

            updated++;
        }

        await db.SaveChangesAsync();
        return new TripReasonResult(updated, messages);
    }

    // A manager confirms (or withdraws the confirmation of) the written reasons. Only explained rows can be checked.
    public async Task<TripReasonResult> CheckMissingAsync(int tripId, IEnumerable<int> cylinderIds, string? checkedBy, bool undo = false)
    {
        HashSet<int> ids = cylinderIds.ToHashSet();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Kontrol edilecek tüp seçin.");
        }

        await using TupTakipDbContext db = new();
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders.Where(x => x.TripId == tripId && ids.Contains(x.CylinderId)).ToListAsync();
        List<string> messages = [];
        int updated = 0;
        DateTime now = DateTime.Now;
        foreach (CylinderTripCylinder row in rows)
        {
            if (undo)
            {
                if (row.CheckedAt is not null)
                {
                    row.CheckedAt = null;
                    row.CheckedBy = null;
                    updated++;
                }

                continue;
            }

            if (row.MissingReason is null)
            {
                messages.Add($"{row.SerialNumber}: önce gerekçe yazılmalı.");
                continue;
            }

            if (row.CheckedAt is null)
            {
                row.CheckedAt = now;
                row.CheckedBy = checkedBy;
                updated++;
            }
        }

        await db.SaveChangesAsync();
        return new TripReasonResult(updated, messages);
    }

    // Loss control list across trips: every cylinder that came back unscanned in the last <days> days, unchecked first.
    public async Task<List<TripMissingItem>> MissingReportAsync(int days = 60, bool includeChecked = false)
    {
        DateTime since = DateTime.Today.AddDays(-Math.Clamp(days, 1, 3650));
        await using TupTakipDbContext db = new();
        List<CylinderTrip> trips = await db.CylinderTrips.AsNoTracking().Include(x => x.Vehicles).Include(x => x.Cylinders)
            .Where(x => x.StartedAt >= since)
            .ToListAsync();

        List<(CylinderTrip Trip, CylinderTripCylinder Row)> missing = trips
            .SelectMany(t =>
            {
                HashSet<string> returnedPlates = ReturnedPlates(t);
                return t.Cylinders.Where(r => IsMissing(r, returnedPlates)).Select(r => (t, r));
            })
            .Where(x => includeChecked || x.r.CheckedAt is null)
            .ToList();

        List<int> ids = missing.Select(x => x.Row.CylinderId).Distinct().ToList();
        Dictionary<int, Cylinder> cylinders = await db.Cylinders.AsNoTracking().Include(x => x.CurrentCustomer)
            .Where(x => ids.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id);
        Dictionary<int, DateTime> lastMovement = (await db.CylinderMovements.AsNoTracking()
                .Where(x => ids.Contains(x.CylinderId))
                .GroupBy(x => x.CylinderId)
                .Select(g => new { Id = g.Key, At = g.Max(x => x.MovementDate) })
                .ToListAsync())
            .ToDictionary(x => x.Id, x => x.At);

        return missing
            .Select(x =>
            {
                Cylinder? cylinder = cylinders.GetValueOrDefault(x.Row.CylinderId);
                DateTime? last = lastMovement.TryGetValue(x.Row.CylinderId, out DateTime at) ? at : null;
                return new TripMissingItem(x.Trip.Id, x.Trip.Title, x.Trip.Status == CylinderTripStatus.Active, x.Trip.StartedAt,
                    x.Row.CylinderId, x.Row.SerialNumber, x.Row.GasType, x.Row.VehiclePlate, x.Row.LoadedAt,
                    x.Row.MissingReason, x.Row.MissingReason is null ? null : TripMissingReasons.Label(x.Row.MissingReason), x.Row.MissingNote,
                    x.Row.ReasonAt, x.Row.ReasonBy, x.Row.CheckedAt, x.Row.CheckedBy,
                    cylinder is null ? "-" : CylinderTracking.StatusName((int)cylinder.Status), cylinder?.CurrentCustomer?.CompanyName,
                    cylinder?.VehiclePlate, last, last is DateTime l && l > x.Row.LoadedAt);
            })
            .OrderBy(x => x.Reason is null ? 0 : x.CheckedAt is null ? 1 : 2)
            .ThenByDescending(x => x.TripStartedAt)
            .ThenBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
            CreatedBy = createdBy,
            RequireReasons = true
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

        Dictionary<int, CylinderTripCylinder> onTrip = trip.Cylinders.ToDictionary(x => x.CylinderId);
        HashSet<int> seen = [];
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

            if (!seen.Add(cylinder.Id))
            {
                continue;
            }

            // Delivered / returned rows stay for history but no longer count as "on the trip" for loading.
            if (onTrip.TryGetValue(cylinder.Id, out CylinderTripCylinder? row))
            {
                messages.Add(CylinderTripTracking.LoadDuplicateMessage(cylinder.SerialNumber, row.DeliveredAt, row.ReturnedAt));
                skipped++;
                continue;
            }

            if (cylinder.Status == CylinderStatus.Customer)
            {
                messages.Add($"{cylinder.SerialNumber}: sistemde müşteride görünüyor; araca almak için 'Müşteriden boş alım' kullanın.");
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
        await MarkTripRowsAsync(db, tripId, null, moved, row =>
        {
            row.DeliveredAt ??= DateTime.Now;
            row.DeliveredCustomerId = customerId;
        });
        return moved;
    }

    // Empties (or returned cylinders) collected at a customer go onto the vehicle and onto the trip, so the depot return
    // scan accounts for them and an unscanned one needs a reason like any loaded cylinder.
    public async Task<TripPickupResult> PickupCylindersAsync(int tripId, string? plate, int? customerId, IEnumerable<string?> codes, string? note, string? performedBy, bool isFull = false)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Boş alımı için araç seçin.");
        int customer = customerId is int id && id > 0 ? id : throw new ArgumentException("Boş alınan müşteriyi seçin.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);
        if (clean.Count == 0)
        {
            throw new ArgumentException("Okutulmuş tüp yok.");
        }

        await using TupTakipDbContext db = new();
        string customerName = await db.Customers.AsNoTracking().Where(x => x.Id == customer).Select(x => x.CompanyName).FirstOrDefaultAsync()
            ?? throw new ArgumentException($"Müşteri bulunamadı: {customer}.");
        await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        List<CylinderBulkResult> results = [];
        List<(string Code, Cylinder Cylinder, string? Warning)> candidates = [];
        foreach (string code in clean)
        {
            Cylinder? cylinder = await _crud.FindCylinderByCodeAsync(code);
            if (cylinder is null)
            {
                results.Add(new CylinderBulkResult(code, false, "Tüp bulunamadı."));
                continue;
            }

            if (!cylinder.IsActive || cylinder.Status == CylinderStatus.Scrap)
            {
                results.Add(new CylinderBulkResult(code, false, $"{cylinder.SerialNumber}: pasif veya hurda tüp alınamaz."));
                continue;
            }

            string? warning = cylinder.Status == CylinderStatus.Customer && cylinder.CurrentCustomerId == customer
                ? null
                : await PickupWarningAsync(db, cylinder);
            candidates.Add((code, cylinder, warning));
        }

        if (candidates.Count > 0)
        {
            string movementNote = $"Sefer #{tripId} · {normalized} · {(isFull ? "dolu iade" : "boş alım")} · {customerName}" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
                + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
            List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(
                candidates.Select(x => x.Code), CylinderStatus.Vehicle, null, movementNote, isFull ? "Sefer dolu iadesi" : "Sefer boş alımı", normalized, isFull, confirmedCustomerChange: true);
            Dictionary<string, CylinderBulkResult> byCode = moved.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
            HashSet<int> ids = candidates.Select(x => x.Cylinder.Id).ToHashSet();
            Dictionary<int, CylinderTripCylinder> rows = await db.CylinderTripCylinders
                .Where(x => x.TripId == tripId && ids.Contains(x.CylinderId))
                .ToDictionaryAsync(x => x.CylinderId);
            DateTime now = DateTime.Now;
            foreach ((string code, Cylinder cylinder, string? found) in candidates)
            {
                string? warning = found;
                CylinderBulkResult result = byCode.GetValueOrDefault(code) ?? new CylinderBulkResult(code, false, "bilinmeyen hata");
                if (!result.Ok)
                {
                    results.Add(result);
                    continue;
                }

                if (rows.TryGetValue(cylinder.Id, out CylinderTripCylinder? row))
                {
                    if (row.DeliveredAt is null)
                    {
                        row.ReturnedAt = null;
                        row.VehiclePlate = normalized;
                        results.Add(new CylinderBulkResult(code, true, $"{cylinder.SerialNumber}: zaten bu seferin listesinde, araçta sayıldı"));
                        continue;
                    }

                    row.DeliveredAt = null;
                    row.DeliveredCustomerId = null;
                    row.ReturnedAt = null;
                    row.VehiclePlate = normalized;
                    warning = "bu seferde teslim edilmişti, teslim geri alındı" + (warning is null ? string.Empty : "; " + warning);
                }
                else
                {
                    db.CylinderTripCylinders.Add(new CylinderTripCylinder
                    {
                        TripId = tripId,
                        CylinderId = cylinder.Id,
                        VehiclePlate = normalized,
                        SerialNumber = cylinder.SerialNumber,
                        GasType = cylinder.GasType,
                        LoadedAt = now,
                        PickupCustomerId = customer
                    });
                }

                results.Add(new CylinderBulkResult(code, true, $"{cylinder.SerialNumber}: araca alındı" + (warning is null ? string.Empty : $" (⚠ {warning})")));
            }

            await db.SaveChangesAsync();
        }

        return new TripPickupResult(results, await CustomerBalanceAsync(tripId, customer));
    }

    private static async Task<string> PickupWarningAsync(TupTakipDbContext db, Cylinder cylinder)
    {
        if (cylinder.Status == CylinderStatus.Customer && cylinder.CurrentCustomerId is int other)
        {
            string? name = await db.Customers.AsNoTracking().Where(x => x.Id == other).Select(x => x.CompanyName).FirstOrDefaultAsync();
            return $"kayıtta başka müşterideydi: {name ?? "#" + other}";
        }

        return $"kayıtta müşteride değil, {CylinderTracking.StatusName((int)cylinder.Status)} görünüyordu"
            + (string.IsNullOrWhiteSpace(cylinder.VehiclePlate) ? string.Empty : $" ({cylinder.VehiclePlate})");
    }

    // What this trip gave to and took from one customer, and what the customer holds now.
    public async Task<CustomerTripBalance> CustomerBalanceAsync(int tripId, int customerId)
    {
        await using TupTakipDbContext db = new();
        string name = await db.Customers.AsNoTracking().Where(x => x.Id == customerId).Select(x => x.CompanyName).FirstOrDefaultAsync() ?? $"#{customerId}";
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders.AsNoTracking()
            .Where(x => x.TripId == tripId && (x.DeliveredCustomerId == customerId || x.PickupCustomerId == customerId))
            .ToListAsync();
        List<bool?> held = await db.Cylinders.AsNoTracking()
            .Where(x => x.IsActive && x.Status == CylinderStatus.Customer && x.CurrentCustomerId == customerId)
            .Select(x => x.IsFull)
            .ToListAsync();
        return new CustomerTripBalance(customerId, name,
            rows.Count(x => x.DeliveredCustomerId == customerId && x.DeliveredAt is not null),
            rows.Count(x => x.PickupCustomerId == customerId),
            held.Count, held.Count(x => x == true), held.Count(x => x == false));
    }

    private static async Task<List<CustomerTripBalance>> CustomerBalancesAsync(TupTakipDbContext db, CylinderTrip trip)
    {
        List<int> ids = trip.Cylinders.Select(x => x.DeliveredCustomerId).Concat(trip.Cylinders.Select(x => x.PickupCustomerId))
            .OfType<int>().Distinct().ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        Dictionary<int, string> names = await db.Customers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.CompanyName);
        var held = (await db.Cylinders.AsNoTracking()
                .Where(x => x.IsActive && x.Status == CylinderStatus.Customer && x.CurrentCustomerId != null && ids.Contains(x.CurrentCustomerId.Value))
                .GroupBy(x => x.CurrentCustomerId!.Value)
                .Select(g => new { Id = g.Key, Total = g.Count(), Full = g.Count(x => x.IsFull == true), Empty = g.Count(x => x.IsFull == false) })
                .ToListAsync())
            .ToDictionary(x => x.Id);
        return ids.Select(id => new CustomerTripBalance(id, names.GetValueOrDefault(id) ?? $"#{id}",
                trip.Cylinders.Count(x => x.DeliveredCustomerId == id && x.DeliveredAt is not null),
                trip.Cylinders.Count(x => x.PickupCustomerId == id),
                held.GetValueOrDefault(id)?.Total ?? 0, held.GetValueOrDefault(id)?.Full ?? 0, held.GetValueOrDefault(id)?.Empty ?? 0))
            .OrderBy(x => x.CustomerName, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .ToList();
    }

    // isFull = true: the scanned cylinders came back full from a customer (return); otherwise their full/empty state is kept,
    // and one taken back from a customer is empty (CylinderTracking.NextFullness) until it is filled.
    public async Task<TripReconcileResult> ReturnCylindersAsync(int tripId, string? plate, IEnumerable<string?> codes, string? note, string? performedBy, bool? isFull = null)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Dönüş okutması için plaka zorunludur.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);

        await using TupTakipDbContext db = new();
        await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        if (clean.Count > 0)
        {
            bool full = isFull == true;
            string movementNote = $"Sefer #{tripId} · {normalized} · depo dönüşü" + (full ? " · dolu iade" : string.Empty) + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
                + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
            List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(clean, CylinderStatus.Warehouse, null, movementNote, "Sefer dönüşü",
                vehiclePlate: null, isFull: full ? true : null);
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

        Dictionary<string, int> codeMap = await AllCodeMapAsync(db);
        return CylinderTripTracking.Reconcile(rows.Select(Snapshot), returnScanCodes ?? [], codeMap, plate);
    }

    private static TripCylinderSnapshot Snapshot(CylinderTripCylinder x) =>
        new(x.CylinderId, x.SerialNumber, x.GasType, x.VehiclePlate, x.DeliveredAt, x.ReturnedAt, x.MissingReason);

    public async Task<TripCloseResult> CloseTripAsync(int tripId, string? closedBy = null)
    {
        await using TupTakipDbContext db = new();
        CylinderTrip trip = await db.CylinderTrips.Include(x => x.Vehicles).Include(x => x.Cylinders).FirstOrDefaultAsync(x => x.Id == tripId)
            ?? throw new InvalidOperationException($"Sefer bulunamadı: {tripId}.");
        if (trip.Status == CylinderTripStatus.Closed)
        {
            throw new InvalidOperationException("Sefer zaten kapatılmış.");
        }

        CylinderTripTracking.EnsureCanClose(trip.RequireReasons, trip.Cylinders.Select(Snapshot));
        List<CylinderTripCylinder> open = trip.Cylinders.Where(IsOpen)
            .OrderBy(x => x.VehiclePlate, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase).ToList();

        trip.Status = CylinderTripStatus.Closed;
        trip.ClosedAt = DateTime.Now;
        foreach (CylinderTripVehicle vehicle in trip.Vehicles.Where(x => x.ReturnedAt is null))
        {
            vehicle.ReturnedAt = trip.ClosedAt;
        }

        await db.SaveChangesAsync();

        List<string> plates = open.Select(x => x.VehiclePlate).Distinct().ToList();
        Dictionary<string, string?> drivers = (await db.Vehicles.AsNoTracking().Where(x => plates.Contains(x.Plate)).ToListAsync())
            .GroupBy(x => x.Plate, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DriverName, StringComparer.OrdinalIgnoreCase);
        List<TripMissingNotice> missing = open.Select(x => new TripMissingNotice(
            x.SerialNumber, x.GasType, x.VehiclePlate, drivers.GetValueOrDefault(x.VehiclePlate),
            x.MissingReason, x.MissingReason is null ? null : TripMissingReasons.Label(x.MissingReason), x.MissingNote, x.ReasonBy)).ToList();

        TripReconcileResult reconcile = await ReconcileTripAsync(tripId);
        string message = reconcile.HasAlerts
            ? "Sefer kapatıldı. " + reconcile.AlertText
            : "Sefer kapatıldı; eksik okutma uyarısı yok.";
        if (missing.Count > 0)
        {
            message += Environment.NewLine + "Okutulmayan tüpler ve gerekçeleri yöneticiye bildirildi.";
        }

        return new TripCloseResult(tripId, reconcile, message, trip.Title, closedBy, missing);
    }

    private static ActiveTripSummary TripSummary(CylinderTrip trip)
    {
        Dictionary<string, int> loadedByPlate = trip.Cylinders
            .GroupBy(x => x.VehiclePlate, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        Dictionary<string, int> openByPlate = trip.Cylinders
            .Where(IsOpen)
            .GroupBy(x => x.VehiclePlate, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);

        List<TripVehicleSummary> vehicles = trip.Vehicles
            .OrderBy(x => x.Plate, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .Select(v => new TripVehicleSummary(
                v.Plate,
                loadedByPlate.GetValueOrDefault(v.Plate),
                v.ReturnedAt is null,
                v.ReturnedAt,
                openByPlate.GetValueOrDefault(v.Plate)))
            .ToList();

        foreach (KeyValuePair<string, int> extra in loadedByPlate.Where(x => vehicles.All(v => !string.Equals(v.Plate, x.Key, StringComparison.OrdinalIgnoreCase))))
        {
            vehicles.Add(new TripVehicleSummary(extra.Key, extra.Value, true, null, openByPlate.GetValueOrDefault(extra.Key)));
        }

        return new ActiveTripSummary(
            trip.Id,
            trip.Title,
            trip.StartedAt,
            trip.Note,
            vehicles,
            trip.Cylinders.Count(x => x.PickupCustomerId is null),
            trip.Cylinders.Count(x => x.DeliveredAt is not null),
            trip.Cylinders.Count(x => x.ReturnedAt is not null),
            trip.RequireReasons,
            trip.Cylinders.Count(x => x.PickupCustomerId is not null));
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
