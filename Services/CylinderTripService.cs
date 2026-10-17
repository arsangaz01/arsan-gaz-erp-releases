using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

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
    int PickedUpTotal = 0,
    bool RequireReasons = false);

// Refused: cylinders the rules turned away (e.g. an empty cylinder scanned for loading), also listed in Messages.
public sealed record TripLoadResult(int Added, int Skipped, IReadOnlyList<string> Messages, IReadOnlyList<string>? Refused = null)
{
    public IReadOnlyList<string> RefusedCodes => Refused ?? [];
}

public sealed record TripReturnResult(TripReconcileResult Reconcile, int Full, int Empty, int Other, IReadOnlyList<string> Failures)
{
    public string Summary
    {
        get
        {
            List<string> parts = [];
            if (Full > 0)
            {
                parts.Add($"{Full} tüp DOLU olarak depo stoğuna girdi");
            }

            if (Empty > 0)
            {
                parts.Add($"{Empty} tüp BOŞ olarak depoya girdi");
            }

            if (Other > 0)
            {
                parts.Add($"{Other} tüp sefer dışı olarak depoya alındı");
            }

            string text = parts.Count == 0 ? "Depoya okutulan tüp yok." : string.Join(", ", parts) + ".";
            return Failures.Count == 0 ? text : text + Environment.NewLine + string.Join(Environment.NewLine, Failures);
        }
    }

    public string Message => Summary + Environment.NewLine + Reconcile.AlertText;
}

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
    IReadOnlyList<TripGasShortage> MissingByGas,
    int MissingUnexplained = 0,
    int MissingUnchecked = 0,
    int PickedUp = 0,
    bool RequireReasons = false,
    string? ClosedBy = null);

public sealed record TripCylinderView(
    int CylinderId, string SerialNumber, string GasType, string Plate, DateTime LoadedAt, DateTime? DeliveredAt, DateTime? ReturnedAt, bool Missing,
    string? Reason = null, string? ReasonLabel = null, string? Note = null, DateTime? ReasonAt = null, string? ReasonBy = null,
    DateTime? CheckedAt = null, string? CheckedBy = null, bool IsPickup = false, string? PickupCustomer = null);

public sealed record TripReasonResult(int Updated, IReadOnlyList<string> Messages);

// One missing cylinder in the loss control list, with where the cylinder is now and whether it was scanned after the trip.
public sealed record TripMissingItem(
    int TripId, string TripTitle, bool TripActive, DateTime TripStartedAt, int CylinderId, string SerialNumber, string GasType, string Plate,
    DateTime LoadedAt, string? Reason, string? ReasonLabel, string? Note, DateTime? ReasonAt, string? ReasonBy, DateTime? CheckedAt, string? CheckedBy,
    string CurrentStatus, string? CurrentCustomer, string? CurrentPlate, DateTime? LastMovementAt, bool SeenAfterTrip, bool IsPickup = false);

public sealed record TripUnexpectedReturn(int CylinderId, string SerialNumber, string GasType, string? Plate, DateTime At);

public sealed record TripLoadView(int TripId, string Title, bool Active, string? Plate, int Total, int OnVehicle, int Missing, IReadOnlyList<GasTypeGroup> Groups);

public sealed record TripDetail(TripListItem Trip, IReadOnlyList<TripCylinderView> Cylinders, IReadOnlyList<TripUnexpectedReturn> UnexpectedReturns);

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

    public async Task<List<TripListItem>> TripsAsync(bool? active, int take = 100)
    {
        await using ArsanGazDbContext db = new();
        IQueryable<CylinderTrip> query = db.CylinderTrips.AsNoTracking().Include(x => x.Vehicles).Include(x => x.Cylinders);
        if (active is bool wanted)
        {
            CylinderTripStatus status = wanted ? CylinderTripStatus.Active : CylinderTripStatus.Closed;
            query = query.Where(x => x.Status == status);
        }

        List<CylinderTrip> trips = await query.OrderByDescending(x => x.StartedAt).ThenByDescending(x => x.Id).Take(take).ToListAsync();
        return trips.Select(TripView).ToList();
    }

    // Closed trips for the phone "Tamamlanmış seferler" list.
    // seeAll: Yönetici/Operatör. Otherwise only trips this account closed (legacy: own plate / started).
    public async Task<List<TripListItem>> ClosedTripsAsync(string? plate, int take = 40, bool seeAll = false, string? userName = null)
    {
        string? normalized = CylinderTracking.NormalizePlate(plate);
        string? who = string.IsNullOrWhiteSpace(userName) ? null : userName.Trim();
        take = Math.Clamp(take, 1, 100);
        await using ArsanGazDbContext db = new();
        IQueryable<CylinderTrip> query = db.CylinderTrips.AsNoTracking()
            .Where(x => x.Status == CylinderTripStatus.Closed)
            .Include(x => x.Vehicles)
            .Include(x => x.Cylinders);
        if (!seeAll)
        {
            if (who is not null)
            {
                string whoLower = who.ToLowerInvariant();
                // Prefer ClosedBy; older rows without it fall back to fixed plate or CreatedBy.
                // No CreatedBy.Contains: CreatedBy is often "Mobil (cihaz)" and substring match leaks other users' trips.
                query = query.Where(x =>
                    (x.ClosedBy != null && x.ClosedBy.ToLower() == whoLower)
                    || (x.ClosedBy == null && normalized != null
                        && (x.Vehicles.Any(v => v.Plate == normalized) || x.Cylinders.Any(c => c.VehiclePlate == normalized))));
            }
            else if (normalized is not null)
            {
                query = query.Where(x => x.Vehicles.Any(v => v.Plate == normalized) || x.Cylinders.Any(c => c.VehiclePlate == normalized));
            }
            else
            {
                // Depot account without a name filter and without a plate: show nothing rather than everyone's history.
                return [];
            }
        }

        List<CylinderTrip> trips = await query
            .OrderByDescending(x => x.ClosedAt ?? x.StartedAt)
            .ThenByDescending(x => x.Id)
            .Take(take)
            .ToListAsync();
        return trips.Select(TripView).ToList();
    }

    public async Task<TripDetail?> TripDetailAsync(int tripId)
    {
        await using ArsanGazDbContext db = new();
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
                x.MissingNote, x.ReasonAt, x.ReasonBy, x.CheckedAt, x.CheckedBy, x.IsPickup, x.PickupCustomer))
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
        await using ArsanGazDbContext db = new();
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
            return new TripVehicleView(plate, !returnedPlates.Contains(plate), vehicle?.ReturnedAt, rows.Count(x => !x.IsPickup),
                rows.Count(x => x.DeliveredAt is not null), rows.Count(x => x.ReturnedAt is not null),
                rows.Count(IsOpen) - missing, missing);
        }).ToList();

        List<TripGasShortage> missingByGas = trip.Cylinders.Where(x => IsMissing(x, returnedPlates))
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GasType) ? "Belirsiz" : x.GasType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TripGasShortage(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ToList();

        List<CylinderTripCylinder> missingRows = trip.Cylinders.Where(x => IsMissing(x, returnedPlates)).ToList();
        return new TripListItem(trip.Id, trip.Title, trip.Status == CylinderTripStatus.Active, trip.StartedAt, trip.ClosedAt, trip.Note, trip.CreatedBy,
            vehicles, trip.Cylinders.Count(x => !x.IsPickup), trip.Cylinders.Count(x => x.DeliveredAt is not null), trip.Cylinders.Count(x => x.ReturnedAt is not null),
            vehicles.Sum(x => x.OnVehicle), vehicles.Sum(x => x.Missing), missingByGas,
            missingRows.Count(x => x.MissingReason is null), missingRows.Count(x => x.MissingReason is not null && x.CheckedAt is null),
            trip.Cylinders.Count(x => x.IsPickup), trip.RequireReasons, trip.ClosedBy);
    }

    // Writes where / why loaded (or picked up) cylinders were neither delivered nor scanned back at the depot. Works on
    // active and closed trips; changing the reason clears an earlier check so the new explanation is reviewed again.
    public async Task<TripReasonResult> ExplainMissingAsync(
        int tripId, IEnumerable<int> cylinderIds, string? reasonKey, string? note, string? performedBy, int? customerId = null)
    {
        string? customerName = customerId is int pickedCustomer && pickedCustomer != 0 ? await CustomerNameAsync(pickedCustomer) : null;
        (TripMissingReason reason, string? cleanNote) = TripMissingReasons.Validate(reasonKey, note, customerName);
        HashSet<int> ids = cylinderIds.ToHashSet();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Gerekçe yazılacak tüp seçin.");
        }

        await using ArsanGazDbContext db = new();
        bool exists = await db.CylinderTrips.AnyAsync(x => x.Id == tripId);
        if (!exists)
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

    // A manager confirms (or withdraws the confirmation of) the written reasons. Only explained, still open rows can be checked.
    public async Task<TripReasonResult> CheckMissingAsync(int tripId, IEnumerable<int> cylinderIds, string? checkedBy, bool undo = false)
    {
        HashSet<int> ids = cylinderIds.ToHashSet();
        if (ids.Count == 0)
        {
            throw new ArgumentException("Kontrol edilecek tüp seçin.");
        }

        await using ArsanGazDbContext db = new();
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
        await using ArsanGazDbContext db = new();
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
                    cylinder?.VehiclePlate, last, last is DateTime l && l > x.Row.LoadedAt, x.Row.IsPickup);
            })
            .OrderBy(x => x.Reason is null ? 0 : x.CheckedAt is null ? 1 : 2)
            .ThenByDescending(x => x.TripStartedAt)
            .ThenBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<CylinderTrip> StartTripAsync(IEnumerable<string?>? plates, string? note, string? createdBy)
    {
        await using ArsanGazDbContext db = new();
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

        Dictionary<int, CylinderTripCylinder> onTrip = trip.Cylinders.ToDictionary(x => x.CylinderId);
        HashSet<int> seen = [];
        int added = 0;
        int skipped = 0;
        List<string> messages = [];
        List<string> refused = [];
        int unknownFullness = 0;
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
                refused.Add(cylinder.SerialNumber);
                skipped++;
                continue;
            }

            if (CylinderTripTracking.LoadRefusal((int)cylinder.Status, cylinder.IsFull) is string refusal)
            {
                messages.Add($"{cylinder.SerialNumber}: {refusal}");
                refused.Add(cylinder.SerialNumber);
                skipped++;
                continue;
            }

            if (cylinder.IsFull is null && cylinder.Status != CylinderStatus.Filling)
            {
                unknownFullness++;
            }

            candidates.Add((code, cylinder));
        }

        if (candidates.Count == 0)
        {
            return new TripLoadResult(added, skipped, messages, refused);
        }

        if (unknownFullness > 0)
        {
            messages.Add($"{unknownFullness} tüpün doluluk bilgisi yoktu; sefere yüklendiği için DOLU kaydedildi.");
        }

        // The cylinder must really be moved onto the vehicle before it counts as loaded; a refused move would otherwise
        // leave a trip row that later shows up as missing. Everything loaded for a trip is full.
        string movementNote = $"Sefer #{tripId} · {normalized} · yükleme" + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
        List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(
            candidates.Select(x => x.Code), CylinderStatus.Vehicle, null, movementNote, "Sefer yüklemesi", normalized, isFull: true);
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
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                // Another user loaded some of these cylinders onto the same trip at the same moment (unique trip/cylinder
                // index): save the rows one by one and report the ones already there.
                List<CylinderTripCylinder> rows = db.ChangeTracker.Entries<CylinderTripCylinder>()
                    .Where(e => e.State == EntityState.Added).Select(e => e.Entity).ToList();
                db.ChangeTracker.Clear();
                foreach (CylinderTripCylinder row in rows)
                {
                    db.CylinderTripCylinders.Add(new CylinderTripCylinder
                    {
                        TripId = row.TripId, CylinderId = row.CylinderId, VehiclePlate = row.VehiclePlate,
                        SerialNumber = row.SerialNumber, GasType = row.GasType, LoadedAt = row.LoadedAt,
                        IsPickup = row.IsPickup, PickupCustomer = row.PickupCustomer
                    });
                    try
                    {
                        await db.SaveChangesAsync();
                    }
                    catch (DbUpdateException)
                    {
                        db.ChangeTracker.Clear();
                        added--;
                        skipped++;
                        messages.Add($"{row.SerialNumber}: aynı anda başka bir kullanıcı tarafından sefere yüklendi.");
                    }
                }
            }
        }

        return new TripLoadResult(added, skipped, messages, refused);
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

        await using ArsanGazDbContext db = new();
        await EnsureActiveTripAsync(db, tripId);
        string movementNote = $"Sefer #{tripId} · teslim" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
            + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
        List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(clean, CylinderStatus.Customer, customerId, movementNote, "Sefer teslimi", vehiclePlate: null, isFull: null);
        int? deliveredTo = await _crud.ResolveCustomerIdAsync(customerId);
        await MarkTripRowsAsync(db, tripId, null, moved, row =>
        {
            row.DeliveredAt ??= DateTime.Now;
            row.DeliveredCustomerId = deliveredTo;
        });
        return moved;
    }

    // Empties taken back from customers during the trip: Customer → this vehicle, empty. They join the trip list so the
    // depot return can check them; a cylinder delivered earlier on this trip and taken back simply becomes a pickup.
    public async Task<TripLoadResult> PickupEmptiesAsync(int tripId, string? plate, IEnumerable<string?> codes, string? performedBy)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Müşteriden boş alma için plaka zorunludur.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);
        if (clean.Count == 0)
        {
            throw new ArgumentException("Okutulmuş tüp yok.");
        }

        await using ArsanGazDbContext db = new();
        CylinderTrip trip = await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        Dictionary<int, CylinderTripCylinder> rows = trip.Cylinders.ToDictionary(x => x.CylinderId);
        int skipped = 0;
        List<string> messages = [];
        List<(string Code, Cylinder Cylinder)> candidates = [];
        HashSet<int> seen = [];
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

            if (rows.TryGetValue(cylinder.Id, out CylinderTripCylinder? existing) && existing.DeliveredAt is null)
            {
                messages.Add(existing.ReturnedAt is null
                    ? $"{cylinder.SerialNumber}: zaten araçta (bu seferde teslim edilmemiş)."
                    : $"{cylinder.SerialNumber}: bu seferde depoya dönüşü okutulmuş.");
                skipped++;
                continue;
            }

            if (cylinder.Status != CylinderStatus.Customer)
            {
                messages.Add($"{cylinder.SerialNumber}: sistemde müşteride değil ({CylinderTracking.StatusName((int)cylinder.Status)}) görünüyordu; yine de araca boş alındı.");
            }

            candidates.Add((code, cylinder));
        }

        if (candidates.Count == 0)
        {
            return new TripLoadResult(0, skipped, messages);
        }

        string movementNote = $"Sefer #{tripId} · {normalized} · müşteriden boş alındı" + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
        List<CylinderBulkResult> moved = await _crud.MoveCylindersAsync(
            candidates.Select(x => x.Code), CylinderStatus.Vehicle, null, movementNote, "Sefer boş alma", normalized, isFull: false);
        Dictionary<string, CylinderBulkResult> byCode = moved.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        DateTime now = DateTime.Now;
        int added = 0;
        foreach ((string code, Cylinder cylinder) in candidates)
        {
            if (byCode.GetValueOrDefault(code) is not { Ok: true })
            {
                messages.Add($"{cylinder.SerialNumber}: araca alınamadı ({byCode.GetValueOrDefault(code)?.Message ?? "bilinmeyen hata"}).");
                skipped++;
                continue;
            }

            string? customer = cylinder.Status == CylinderStatus.Customer ? cylinder.CurrentCustomer?.CompanyName : null;
            int? pickupCustomerId = cylinder.Status == CylinderStatus.Customer ? cylinder.CurrentCustomerId : null;
            if (rows.TryGetValue(cylinder.Id, out CylinderTripCylinder? row))
            {
                row.DeliveredAt = null;
                row.DeliveredCustomerId = null;
                row.ReturnedAt = null;
                row.MissingReason = null;
                row.MissingNote = null;
                row.ReasonAt = null;
                row.ReasonBy = null;
                row.CheckedAt = null;
                row.CheckedBy = null;
                row.IsPickup = true;
                row.PickupCustomer = customer;
                row.PickupCustomerId = pickupCustomerId;
                row.VehiclePlate = normalized;
                row.LoadedAt = now;
                messages.Add($"{cylinder.SerialNumber}: bu seferde teslim edilmişti; müşteriden geri alındı.");
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
                    IsPickup = true,
                    PickupCustomer = customer,
                    PickupCustomerId = pickupCustomerId
                });
            }

            added++;
        }

        await db.SaveChangesAsync();
        return new TripLoadResult(added, skipped, messages);
    }

    // Depot return scan: a loaded full that was not delivered goes back into stock as full, an empty taken from a
    // customer goes in as empty; cylinders that were not on this trip keep the default fullness rule.
    public async Task<TripReturnResult> ReturnCylindersAsync(int tripId, string? plate, IEnumerable<string?> codes, string? note, string? performedBy)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Dönüş okutması için plaka zorunludur.");
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);

        await using ArsanGazDbContext db = new();
        CylinderTrip trip = await EnsureActiveTripAsync(db, tripId);
        await EnsureVehicleOnTripAsync(db, tripId, normalized);

        int full = 0, empty = 0, other = 0;
        List<string> failures = [];
        if (clean.Count > 0)
        {
            Dictionary<string, int> ids = await CodeMapAsync(clean);
            Dictionary<int, CylinderTripCylinder> rows = trip.Cylinders.ToDictionary(x => x.CylinderId);
            string movementNote = $"Sefer #{tripId} · {normalized} · depo dönüşü" + (string.IsNullOrWhiteSpace(note) ? string.Empty : " · " + note.Trim())
                + (string.IsNullOrWhiteSpace(performedBy) ? string.Empty : $" · {performedBy}");
            List<CylinderBulkResult> moved = [];
            foreach (IGrouping<bool?, string> group in clean.GroupBy(code =>
                     {
                         CylinderTripCylinder? row = ids.TryGetValue(code, out int id) ? rows.GetValueOrDefault(id) : null;
                         return CylinderTripTracking.ReturnFullness(row is not null && row.ReturnedAt is null, row?.IsPickup == true, row?.DeliveredAt is not null);
                     }))
            {
                List<CylinderBulkResult> results = await _crud.MoveCylindersAsync(group, CylinderStatus.Warehouse, null, movementNote, "Sefer dönüşü", vehiclePlate: null, isFull: group.Key);
                int ok = results.Count(x => x.Ok);
                if (group.Key == true)
                {
                    full += ok;
                }
                else if (group.Key == false)
                {
                    empty += ok;
                }
                else
                {
                    other += ok;
                }

                failures.AddRange(results.Where(x => !x.Ok).Select(x => $"⚠ {x.Code}: {x.Message}"));
                moved.AddRange(results);
            }

            db.ChangeTracker.Clear();
            await MarkTripRowsAsync(db, tripId, normalized, moved, row => row.ReturnedAt ??= DateTime.Now);

            CylinderTripVehicle? vehicle = await db.CylinderTripVehicles.FirstOrDefaultAsync(x => x.TripId == tripId && x.Plate == normalized);
            if (vehicle is not null && vehicle.ReturnedAt is null)
            {
                vehicle.ReturnedAt = DateTime.Now;
                await db.SaveChangesAsync();
            }
        }

        return new TripReturnResult(await ReconcileTripAsync(tripId, normalized, clean), full, empty, other, failures);
    }

    // Only cylinders whose movement was actually saved are marked delivered/returned on the trip.
    private async Task MarkTripRowsAsync(ArsanGazDbContext db, int tripId, string? plate, IEnumerable<CylinderBulkResult> moved, Action<CylinderTripCylinder> mark)
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
        await using ArsanGazDbContext db = new();
        List<CylinderTripCylinder> rows = await db.CylinderTripCylinders.AsNoTracking()
            .Where(x => x.TripId == tripId)
            .ToListAsync();

        List<TripCylinderSnapshot> snapshots = rows.Select(x => new TripCylinderSnapshot(
            x.CylinderId, x.SerialNumber, x.GasType, x.VehiclePlate, x.DeliveredAt, x.ReturnedAt, x.MissingReason, x.IsPickup)).ToList();

        Dictionary<string, int> codeMap = await AllCodeMapAsync(db);
        return CylinderTripTracking.Reconcile(snapshots, returnScanCodes ?? [], codeMap, plate);
    }

    public async Task<TripCloseResult> CloseTripAsync(int tripId, string? closedBy = null)
    {
        await using ArsanGazDbContext db = new();
        CylinderTrip trip = await db.CylinderTrips.Include(x => x.Vehicles).Include(x => x.Cylinders).FirstOrDefaultAsync(x => x.Id == tripId)
            ?? throw new InvalidOperationException($"Sefer bulunamadı: {tripId}.");
        if (trip.Status == CylinderTripStatus.Closed)
        {
            throw new InvalidOperationException("Sefer zaten kapatılmış.");
        }

        // Every cylinder that was neither delivered nor scanned back needs a "where / why" reason before the trip ends.
        List<CylinderTripCylinder> open = trip.Cylinders.Where(IsOpen).OrderBy(x => x.VehiclePlate).ThenBy(x => x.SerialNumber).ToList();
        List<string> unexplained = open.Where(x => string.IsNullOrWhiteSpace(x.MissingReason)).Select(x => x.SerialNumber).ToList();
        if (trip.RequireReasons && unexplained.Count > 0)
        {
            throw new TripReasonsRequiredException(unexplained);
        }

        string? who = string.IsNullOrWhiteSpace(closedBy) ? null
            : closedBy.Trim().Length > 80 ? closedBy.Trim()[..80] : closedBy.Trim();
        DateTime closedAt = DateTime.Now;
        // Atomic claim so two concurrent closes cannot both send manager notices / overwrite ClosedBy.
        int claimed = await db.CylinderTrips
            .Where(t => t.Id == tripId && t.Status == CylinderTripStatus.Active)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.Status, CylinderTripStatus.Closed)
                .SetProperty(t => t.ClosedAt, closedAt)
                .SetProperty(t => t.ClosedBy, who));
        if (claimed == 0)
        {
            throw new InvalidOperationException("Sefer zaten kapatılmış.");
        }

        trip.Status = CylinderTripStatus.Closed;
        trip.ClosedAt = closedAt;
        trip.ClosedBy = who;
        foreach (CylinderTripVehicle vehicle in trip.Vehicles.Where(x => x.ReturnedAt is null))
        {
            vehicle.ReturnedAt = closedAt;
        }

        await db.SaveChangesAsync();

        List<string> plates = open.Select(x => x.VehiclePlate).Distinct().ToList();
        Dictionary<string, string?> drivers = (await db.Vehicles.AsNoTracking().Where(x => plates.Contains(x.Plate)).ToListAsync())
            .GroupBy(x => x.Plate, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().DriverName, StringComparer.OrdinalIgnoreCase);
        List<TripMissingNotice> missing = open.Select(x => new TripMissingNotice(
            x.SerialNumber, x.GasType, x.VehiclePlate, drivers.GetValueOrDefault(x.VehiclePlate), x.IsPickup, x.PickupCustomer,
            x.MissingReason, x.MissingReason is null ? null : TripMissingReasons.Label(x.MissingReason), x.MissingNote, x.ReasonBy)).ToList();

        TripReconcileResult reconcile = await ReconcileTripAsync(tripId);
        string message = reconcile.HasAlerts
            ? "Sefer kapatıldı. " + reconcile.AlertText
            : "Sefer kapatıldı; eksik okutma uyarısı yok.";
        if (missing.Count > 0)
        {
            message += Environment.NewLine + "Okutulmayan tüplerin gerekçeleri yöneticilere bildirildi.";
        }

        return new TripCloseResult(tripId, reconcile, message, trip.Title, who, missing);
    }

    private static async Task<string?> CustomerNameAsync(int customerId)
    {
        if (customerId > 0)
        {
            await using ArsanGazDbContext db = new();
            return await db.Customers.AsNoTracking().Where(x => x.Id == customerId).Select(x => x.CompanyName).FirstOrDefaultAsync()
                ?? throw new ArgumentException($"Müşteri bulunamadı: {customerId}.");
        }

        // Negative ids are CRM companies in the customer picker; only the name is needed, no ERP customer is created.
        await using CrmDbContext crm = new();
        return await crm.CrmCompanies.AsNoTracking().Where(x => x.Id == -customerId).Select(x => x.CompanyName).FirstOrDefaultAsync()
            ?? throw new ArgumentException($"CRM firması bulunamadı: {-customerId}.");
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
            trip.Cylinders.Count(x => !x.IsPickup),
            trip.Cylinders.Count(x => x.DeliveredAt is not null),
            trip.Cylinders.Count(x => x.ReturnedAt is not null),
            trip.Cylinders.Count(x => x.IsPickup),
            trip.RequireReasons);
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
