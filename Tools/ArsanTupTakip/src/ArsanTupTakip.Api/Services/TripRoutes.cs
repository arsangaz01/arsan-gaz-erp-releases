using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

// Phone is only filled for users who may see customer phones (CustomerView).
public sealed record TripStopView(
    int Id, int Position, string Plate, int CustomerId, string CustomerName, string? Address, string? District, string? City, string? Phone,
    bool Done, DateTime? DoneAt, int Delivered, int Collected, string? Note, string? MapUrl);

public sealed record TripStopSuggestion(int CustomerId, string CustomerName, string? City, string Reason);

// The visiting order of a trip: the driver adds customers, moves them up or down, opens the address in the map app and
// sees which stops are already done (delivery or empty pickup scanned there).
public static class TripRoutes
{
    public const int MaxStops = 60;

    // Google Maps directions link built from the address text; null when there is nothing to search for.
    public static string? MapUrl(string? address, string? district, string? city)
    {
        string[] parts = new[] { address, district, city }
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x!.Trim())
            .ToArray();
        return parts.Length == 0
            ? null
            : "https://www.google.com/maps/dir/?api=1&destination=" + Uri.EscapeDataString(string.Join(", ", parts) + ", Türkiye");
    }

    // Moves the stop one step (-1 up, +1 down) and renumbers 1..n; returns the new order of ids.
    public static List<int> Move(IReadOnlyList<int> orderedIds, int stopId, int direction)
    {
        List<int> ids = [.. orderedIds];
        int index = ids.IndexOf(stopId);
        int target = index + Math.Sign(direction);
        if (index < 0 || direction == 0 || target < 0 || target >= ids.Count)
        {
            return ids;
        }

        (ids[index], ids[target]) = (ids[target], ids[index]);
        return ids;
    }

    public static async Task<List<TripStopView>> ListAsync(int tripId, string? plate, bool withPhones)
    {
        string? normalized = CylinderTracking.NormalizePlate(plate);
        await using TupTakipDbContext db = new();
        List<CylinderTripStop> stops = await db.CylinderTripStops.AsNoTracking()
            .Where(x => x.TripId == tripId && (normalized == null || x.Plate == normalized))
            .OrderBy(x => x.Plate).ThenBy(x => x.Position).ThenBy(x => x.Id)
            .ToListAsync();
        if (stops.Count == 0)
        {
            return [];
        }

        List<int> customerIds = stops.Select(x => x.CustomerId).Distinct().ToList();
        Dictionary<int, Customer> customers = await db.Customers.AsNoTracking().Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        var rows = await db.CylinderTripCylinders.AsNoTracking()
            .Where(x => x.TripId == tripId && ((x.DeliveredCustomerId != null && customerIds.Contains(x.DeliveredCustomerId.Value))
                || (x.PickupCustomerId != null && customerIds.Contains(x.PickupCustomerId.Value))))
            .Select(x => new { x.DeliveredCustomerId, x.DeliveredAt, x.PickupCustomerId })
            .ToListAsync();

        return stops.Select(stop =>
        {
            Customer? customer = customers.GetValueOrDefault(stop.CustomerId);
            int delivered = rows.Count(r => r.DeliveredCustomerId == stop.CustomerId && r.DeliveredAt != null);
            int collected = rows.Count(r => r.PickupCustomerId == stop.CustomerId);
            return new TripStopView(stop.Id, stop.Position, stop.Plate, stop.CustomerId, customer?.CompanyName ?? $"#{stop.CustomerId}",
                customer?.Address, customer?.District, customer?.City, withPhones ? customer?.Phone : null,
                stop.DoneAt is not null || delivered + collected > 0, stop.DoneAt, delivered, collected, stop.Note,
                MapUrl(customer?.Address, customer?.District, customer?.City));
        }).ToList();
    }

    public static async Task<string> AddAsync(int tripId, string? plate, int customerId, string addedBy)
    {
        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Rota için araç seçin.");
        await using TupTakipDbContext db = new();
        await EnsureOpenAsync(db, tripId, normalized);
        string name = await db.Customers.AsNoTracking().Where(x => x.Id == customerId && x.IsActive).Select(x => x.CompanyName).FirstOrDefaultAsync()
            ?? throw new ArgumentException("Müşteri bulunamadı veya pasif.");
        List<CylinderTripStop> stops = await db.CylinderTripStops.Where(x => x.TripId == tripId && x.Plate == normalized).ToListAsync();
        if (stops.Any(x => x.CustomerId == customerId))
        {
            return $"{name} zaten rotada.";
        }

        if (stops.Count >= MaxStops)
        {
            throw new ArgumentException($"Bir araca en fazla {MaxStops} durak eklenebilir.");
        }

        db.CylinderTripStops.Add(new CylinderTripStop
        {
            TripId = tripId,
            Plate = normalized,
            CustomerId = customerId,
            Position = stops.Count == 0 ? 1 : stops.Max(x => x.Position) + 1,
            AddedAt = DateTime.Now,
            AddedBy = addedBy
        });
        await db.SaveChangesAsync();
        return $"{name} rotaya eklendi ({stops.Count + 1}. durak).";
    }

    public static async Task MoveAsync(int tripId, int stopId, int direction, string? onlyPlate = null)
    {
        await using TupTakipDbContext db = new();
        CylinderTripStop stop = await db.CylinderTripStops.FirstOrDefaultAsync(x => x.Id == stopId && x.TripId == tripId)
            ?? throw new ArgumentException("Durak bulunamadı.");
        EnsurePlate(stop, onlyPlate);
        await EnsureOpenAsync(db, tripId, null);
        List<CylinderTripStop> stops = await db.CylinderTripStops.Where(x => x.TripId == tripId && x.Plate == stop.Plate)
            .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();
        List<int> order = Move(stops.Select(x => x.Id).ToList(), stopId, direction);
        for (int i = 0; i < order.Count; i++)
        {
            stops.First(x => x.Id == order[i]).Position = i + 1;
        }

        await db.SaveChangesAsync();
    }

    public static async Task RemoveAsync(int tripId, int stopId, string? onlyPlate = null)
    {
        await using TupTakipDbContext db = new();
        CylinderTripStop stop = await db.CylinderTripStops.FirstOrDefaultAsync(x => x.Id == stopId && x.TripId == tripId)
            ?? throw new ArgumentException("Durak bulunamadı.");
        EnsurePlate(stop, onlyPlate);
        await EnsureOpenAsync(db, tripId, null);
        db.CylinderTripStops.Remove(stop);
        await db.SaveChangesAsync();
    }

    public static async Task SetDoneAsync(int tripId, int stopId, bool done, string? note, string? onlyPlate = null)
    {
        await using TupTakipDbContext db = new();
        CylinderTripStop stop = await db.CylinderTripStops.FirstOrDefaultAsync(x => x.Id == stopId && x.TripId == tripId)
            ?? throw new ArgumentException("Durak bulunamadı.");
        EnsurePlate(stop, onlyPlate);
        stop.DoneAt = done ? stop.DoneAt ?? DateTime.Now : null;
        if (note is not null)
        {
            string clean = note.Trim();
            stop.Note = clean.Length == 0 ? null : clean.Length > 300 ? throw new ArgumentException("Not en fazla 300 karakter olabilir.") : clean;
        }

        await db.SaveChangesAsync();
    }

    // Due within two days, or late by at most one ordering cycle (two weeks at least); later ones have probably stopped
    // ordering and would bury the real candidates.
    public static bool IsSuggested(ReorderForecast forecast) =>
        forecast.DaysUntilExpected <= 2 && forecast.DaysUntilExpected >= -Math.Max(14, forecast.TypicalIntervalDays);

    // Customers whose usual ordering rhythm says they are due now and are not on the route yet, the most urgent first.
    public static async Task<List<TripStopSuggestion>> SuggestionsAsync(int tripId, string? plate)
    {
        string? normalized = CylinderTracking.NormalizePlate(plate);
        await using TupTakipDbContext db = new();
        HashSet<int> onRoute = (await db.CylinderTripStops.AsNoTracking()
                .Where(x => x.TripId == tripId && (normalized == null || x.Plate == normalized))
                .Select(x => x.CustomerId).ToListAsync())
            .ToHashSet();
        List<ReorderForecast> due = (await new CrudService().ReorderForecastsAsync())
            .Where(x => IsSuggested(x) && !onRoute.Contains(x.CustomerId))
            .OrderBy(x => Math.Abs(x.DaysUntilExpected))
            .Take(20)
            .ToList();
        List<int> ids = due.Select(x => x.CustomerId).ToList();
        Dictionary<int, string?> cities = await db.Customers.AsNoTracking().Where(x => ids.Contains(x.Id)).ToDictionaryAsync(x => x.Id, x => x.City);
        return due.Select(x => new TripStopSuggestion(x.CustomerId, x.CustomerName, cities.GetValueOrDefault(x.CustomerId), "Sipariş zamanı: " + x.Timing)).ToList();
    }

    private static void EnsurePlate(CylinderTripStop stop, string? onlyPlate)
    {
        if (onlyPlate is not null && !string.Equals(stop.Plate, onlyPlate, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Bu durak sizin aracınızın rotasında değil.");
        }
    }

    private static async Task EnsureOpenAsync(TupTakipDbContext db, int tripId, string? plate)
    {
        CylinderTrip trip = await db.CylinderTrips.AsNoTracking().FirstOrDefaultAsync(x => x.Id == tripId)
            ?? throw new InvalidOperationException($"Sefer bulunamadı: {tripId}.");
        if (trip.Status != CylinderTripStatus.Active)
        {
            throw new InvalidOperationException("Sefer kapanmış; rota değiştirilemez.");
        }

        if (plate is not null && !await db.CylinderTripVehicles.AnyAsync(x => x.TripId == tripId && x.Plate == plate))
        {
            throw new ArgumentException($"{plate} bu seferde değil.");
        }
    }
}
