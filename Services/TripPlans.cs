using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record TripPlanStopInput(int CustomerId, string? Note);

public sealed record TripPlanStopView(
    int CustomerId, string CustomerName, string? City, string? District, string? Address, string? Note,
    double? Latitude, double? Longitude, bool? LocationVerified, bool Done);

// Status: "bekliyor" (no trip yet), "seferde" (copied onto an open trip), "kapandi" (that trip is closed).
public sealed record TripPlanView(
    int Id, DateTime Date, string Plate, int AlertMeters, string? Note, string Status, int? TripId,
    string CreatedBy, DateTime UpdatedAt, string UpdatedBy, int StopCount, int Located, int Unverified, int Done, List<TripPlanStopView> Stops);

public sealed record CustomerLocationView(
    int CustomerId, double Latitude, double Longitude, DateTime UpdatedAt, string UpdatedBy, string Source, string MapUrl,
    bool Verified, string? Label);

// Day plans for vehicles and the customer locations the phone's approach warning uses. A plan is copied onto the route
// of the open trip that has its vehicle on the plan's day; the stops it added carry the "Plan #id" mark so that a later
// change of the plan can take out the ones the driver has not visited yet.
public static class TripPlans
{
    public const int DefaultAlertMeters = 300;
    public const int MinAlertMeters = 50;
    public const int MaxAlertMeters = 5000;
    public const int MaxDaysAhead = 60;

    public static string Mark(int planId) => $"Plan #{planId}";

    // Plan days are compared as plain dates: a Local-kind DateTime parameter does not match the stored timestamp.
    private static DateTime Day(DateTime value) => DateTime.SpecifyKind(value.Date, DateTimeKind.Unspecified);

    private static DateTime Today => Day(DateTime.Today);

    public static async Task<List<TripPlanView>> ListAsync(DateTime date)
    {
        await using ArsanGazDbContext db = new();
        DateTime day = Day(date);
        List<int> ids = await db.TripPlans.AsNoTracking().Where(x => x.PlanDate == day).OrderBy(x => x.Plate).Select(x => x.Id).ToListAsync();
        List<TripPlanView> result = [];
        foreach (int id in ids)
        {
            result.Add((await GetAsync(db, id))!);
        }

        return result;
    }

    // Today's plans for the phone: only the given vehicle's when a plate is given.
    public static async Task<List<TripPlanView>> TodayAsync(string? plate)
    {
        string? only = CylinderTracking.NormalizePlate(plate);
        return (await ListAsync(Today)).Where(x => only is null || x.Plate == only).ToList();
    }

    public static async Task<TripPlanView?> GetAsync(int id)
    {
        await using ArsanGazDbContext db = new();
        return await GetAsync(db, id);
    }

    private static async Task<TripPlanView?> GetAsync(ArsanGazDbContext db, int id)
    {
        TripPlan? plan = await db.TripPlans.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (plan is null)
        {
            return null;
        }

        List<TripPlanStop> stops = await db.TripPlanStops.AsNoTracking().Where(x => x.PlanId == id).OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();
        List<int> customerIds = stops.Select(x => x.CustomerId).ToList();
        Dictionary<int, Customer> customers = await db.Customers.AsNoTracking().Where(x => customerIds.Contains(x.Id)).ToDictionaryAsync(x => x.Id);
        Dictionary<int, CustomerLocation> locations = await db.CustomerLocations.AsNoTracking()
            .Where(x => customerIds.Contains(x.CustomerId)).ToDictionaryAsync(x => x.CustomerId);

        string status = "bekliyor";
        HashSet<int> done = [];
        if (plan.TripId is int tripId)
        {
            CylinderTripStatus? tripStatus = await db.CylinderTrips.AsNoTracking().Where(x => x.Id == tripId).Select(x => (CylinderTripStatus?)x.Status).FirstOrDefaultAsync();
            status = tripStatus == CylinderTripStatus.Active ? "seferde" : "kapandi";
            done = (await TripRoutes.ListAsync(tripId, plan.Plate, false)).Where(x => x.Done).Select(x => x.CustomerId).ToHashSet();
        }

        List<TripPlanStopView> views = stops.Select(s =>
        {
            Customer? c = customers.GetValueOrDefault(s.CustomerId);
            CustomerLocation? l = locations.GetValueOrDefault(s.CustomerId);
            return new TripPlanStopView(s.CustomerId, c?.CompanyName ?? $"#{s.CustomerId}", c?.City, c?.District, c?.Address, s.Note,
                l?.Latitude, l?.Longitude, l?.Verified, done.Contains(s.CustomerId));
        }).ToList();
        return new TripPlanView(plan.Id, plan.PlanDate, plan.Plate, plan.AlertMeters, plan.Note, status, plan.TripId,
            plan.CreatedBy, plan.UpdatedAt, plan.UpdatedBy, views.Count, views.Count(x => x.Latitude is not null),
            views.Count(x => x.LocationVerified == false), views.Count(x => x.Done), views);
    }

    // Creates or replaces a plan; a plan for today is copied onto the open trip of its vehicle straight away.
    // Returns the plan id and a message for the user.
    public static async Task<(int Id, string Message)> SaveAsync(
        int? id, DateTime date, string? plate, int alertMeters, string? note, IReadOnlyList<TripPlanStopInput> stops, string by)
    {
        DateTime day = Day(date);
        if (day < Today)
        {
            throw new ArgumentException("Geçmiş bir güne plan yapılamaz.");
        }

        if (day > Today.AddDays(MaxDaysAhead))
        {
            throw new ArgumentException($"En fazla {MaxDaysAhead} gün sonrasına plan yapılabilir.");
        }

        string normalized = CylinderTracking.NormalizePlate(plate) ?? throw new ArgumentException("Araç seçin.");
        if (alertMeters < MinAlertMeters || alertMeters > MaxAlertMeters)
        {
            throw new ArgumentException($"Uyarı mesafesi {MinAlertMeters} ile {MaxAlertMeters} metre arasında olmalıdır.");
        }

        string? cleanNote = Clean(note, 300, "Plan notu");
        List<TripPlanStopInput> clean = stops.Select(s => s with { Note = Clean(s.Note, 200, "Durak notu") }).ToList();
        if (clean.Count == 0)
        {
            throw new ArgumentException("Plana en az bir müşteri ekleyin.");
        }

        if (clean.Count > TripRoutes.MaxStops)
        {
            throw new ArgumentException($"Bir plana en fazla {TripRoutes.MaxStops} müşteri eklenebilir.");
        }

        if (clean.Select(x => x.CustomerId).Distinct().Count() != clean.Count)
        {
            throw new ArgumentException("Aynı müşteri planda iki kez var.");
        }

        await using ArsanGazDbContext db = new();
        Vehicle vehicle = await db.Vehicles.AsNoTracking().FirstOrDefaultAsync(x => x.Plate == normalized)
            ?? throw new ArgumentException($"{normalized} kayıtlı bir araç değil.");
        if (!vehicle.IsActive)
        {
            throw new ArgumentException($"{normalized} pasif araç.");
        }

        List<int> customerIds = clean.Select(x => x.CustomerId).ToList();
        int active = await db.Customers.CountAsync(x => customerIds.Contains(x.Id) && x.IsActive);
        if (active != customerIds.Count)
        {
            throw new ArgumentException("Plandaki müşterilerden biri bulunamadı veya pasif.");
        }

        if (await db.TripPlans.AnyAsync(x => x.PlanDate == day && x.Plate == normalized && x.Id != (id ?? 0)))
        {
            throw new ArgumentException($"{normalized} için {day:dd.MM.yyyy} gününe zaten plan var; onu düzenleyin.");
        }

        DateTime now = DateTime.Now;
        TripPlan plan;
        if (id is int existingId)
        {
            plan = await db.TripPlans.FirstOrDefaultAsync(x => x.Id == existingId) ?? throw new ArgumentException("Plan bulunamadı.");
            if (plan.TripId is int tripId && !await db.CylinderTrips.AnyAsync(x => x.Id == tripId && x.Status == CylinderTripStatus.Active))
            {
                throw new InvalidOperationException("Bu planın seferi kapandı; plan artık değiştirilemez.");
            }

            if (plan.TripId is not null && (plan.PlanDate != day || plan.Plate != normalized))
            {
                throw new InvalidOperationException("Sefere eklenmiş planın günü veya aracı değiştirilemez; müşterileri ve mesafeyi değiştirebilirsiniz.");
            }

            db.TripPlanStops.RemoveRange(await db.TripPlanStops.Where(x => x.PlanId == plan.Id).ToListAsync());
        }
        else
        {
            plan = new TripPlan { CreatedAt = now, CreatedBy = by };
            db.TripPlans.Add(plan);
        }

        plan.PlanDate = day;
        plan.Plate = normalized;
        plan.AlertMeters = alertMeters;
        plan.Note = cleanNote;
        plan.UpdatedAt = now;
        plan.UpdatedBy = by;
        await db.SaveChangesAsync();
        for (int i = 0; i < clean.Count; i++)
        {
            db.TripPlanStops.Add(new TripPlanStop { PlanId = plan.Id, CustomerId = clean[i].CustomerId, Position = i + 1, Note = clean[i].Note });
        }

        await db.SaveChangesAsync();
        string? applied = await ApplyAsync(db, plan, by);
        return (plan.Id, applied is null ? "Plan kaydedildi." : "Plan kaydedildi. " + applied);
    }

    public static async Task<string> DeleteAsync(int id)
    {
        await using ArsanGazDbContext db = new();
        TripPlan plan = await db.TripPlans.FirstOrDefaultAsync(x => x.Id == id) ?? throw new ArgumentException("Plan bulunamadı.");
        db.TripPlans.Remove(plan);
        await db.SaveChangesAsync();
        return plan.TripId is null ? "Plan silindi." : "Plan silindi; seferdeki rota olduğu gibi kaldı.";
    }

    // Called when a trip starts or a vehicle joins it: today's plans of those vehicles go onto the route.
    public static async Task<List<string>> ApplyForTripAsync(int tripId, string? onlyPlate, string by)
    {
        List<string> messages = [];
        string? only = CylinderTracking.NormalizePlate(onlyPlate);
        await using ArsanGazDbContext db = new();
        List<string> plates = await db.CylinderTripVehicles.AsNoTracking()
            .Where(x => x.TripId == tripId && x.ReturnedAt == null && (only == null || x.Plate == only))
            .Select(x => x.Plate).Distinct().ToListAsync();
        foreach (string plate in plates)
        {
            DateTime today = Today;
            TripPlan? plan = await db.TripPlans.FirstOrDefaultAsync(x => x.PlanDate == today && x.Plate == plate && x.TripId == null);
            string? message = plan is null ? null : await ApplyAsync(db, plan, by, tripId);
            if (message is not null)
            {
                messages.Add($"{plate}: {message}");
            }
        }

        return messages;
    }

    // Copies the plan onto the route of its trip (or of the open trip that has the vehicle when the plan is today's and
    // not applied yet) and keeps them in line: new customers are added, removed ones that were not visited are taken
    // out, the order follows the plan and every stop of the vehicle gets the plan's warning distance.
    private static async Task<string?> ApplyAsync(ArsanGazDbContext db, TripPlan plan, string by, int? onlyTrip = null)
    {
        int? tripId = plan.TripId;
        if (tripId is null)
        {
            if (plan.PlanDate != Today)
            {
                return null;
            }

            tripId = await db.CylinderTripVehicles.AsNoTracking()
                .Where(v => v.Plate == plan.Plate && v.ReturnedAt == null && v.Trip.Status == CylinderTripStatus.Active
                    && (onlyTrip == null || v.TripId == onlyTrip))
                .OrderByDescending(v => v.Trip.StartedAt)
                .Select(v => (int?)v.TripId)
                .FirstOrDefaultAsync();
            if (tripId is null)
            {
                return null;
            }
        }
        else if (!await db.CylinderTrips.AnyAsync(x => x.Id == tripId && x.Status == CylinderTripStatus.Active))
        {
            return null;
        }

        string mark = Mark(plan.Id);
        List<TripPlanStop> planStops = await db.TripPlanStops.AsNoTracking().Where(x => x.PlanId == plan.Id).OrderBy(x => x.Position).ToListAsync();
        HashSet<int> planned = planStops.Select(x => x.CustomerId).ToHashSet();
        List<CylinderTripStop> routeStops = await db.CylinderTripStops.Where(x => x.TripId == tripId && x.Plate == plan.Plate)
            .OrderBy(x => x.Position).ThenBy(x => x.Id).ToListAsync();

        var scans = await db.CylinderTripCylinders.AsNoTracking()
            .Where(x => x.TripId == tripId && ((x.DeliveredCustomerId != null && x.DeliveredAt != null) || x.PickupCustomerId != null))
            .Select(x => new { x.DeliveredCustomerId, x.DeliveredAt, x.PickupCustomerId })
            .ToListAsync();
        HashSet<int> visited = [.. scans.Where(x => x.DeliveredAt != null).Select(x => x.DeliveredCustomerId).OfType<int>(),
            .. scans.Select(x => x.PickupCustomerId).OfType<int>()];
        int removed = 0;
        foreach (CylinderTripStop stop in routeStops.Where(s => s.AddedBy == mark && s.DoneAt == null && !planned.Contains(s.CustomerId) && !visited.Contains(s.CustomerId)).ToList())
        {
            db.CylinderTripStops.Remove(stop);
            routeStops.Remove(stop);
            removed++;
        }

        int added = 0;
        int skipped = 0;
        DateTime now = DateTime.Now;
        foreach (TripPlanStop planStop in planStops)
        {
            CylinderTripStop? stop = routeStops.FirstOrDefault(x => x.CustomerId == planStop.CustomerId);
            if (stop is null)
            {
                if (routeStops.Count >= TripRoutes.MaxStops)
                {
                    skipped++;
                    continue;
                }

                stop = new CylinderTripStop { TripId = tripId.Value, Plate = plan.Plate, CustomerId = planStop.CustomerId, AddedAt = now, AddedBy = mark };
                db.CylinderTripStops.Add(stop);
                routeStops.Add(stop);
                added++;
            }

            if (!string.IsNullOrEmpty(planStop.Note) && string.IsNullOrEmpty(stop.Note))
            {
                stop.Note = planStop.Note;
            }
        }

        // Plan order first, then the stops the driver added himself in their current order.
        List<CylinderTripStop> ordered = [.. planStops.Select(p => routeStops.FirstOrDefault(r => r.CustomerId == p.CustomerId)).OfType<CylinderTripStop>()];
        ordered.AddRange(routeStops.Where(r => !planned.Contains(r.CustomerId)));
        for (int i = 0; i < ordered.Count; i++)
        {
            ordered[i].Position = i + 1;
            ordered[i].AlertMeters = plan.AlertMeters;
        }

        bool first = plan.TripId is null;
        TripPlan tracked = db.TripPlans.Local.FirstOrDefault(x => x.Id == plan.Id) ?? await db.TripPlans.FirstAsync(x => x.Id == plan.Id);
        tracked.TripId = tripId;
        tracked.AppliedAt ??= now;
        await db.SaveChangesAsync();
        plan.TripId = tripId;

        string text = first ? $"Sefer #{tripId} rotasına eklendi ({added} durak)." : $"Seferdeki rota güncellendi (+{added}, -{removed}).";
        return skipped > 0 ? text + $" Rota {TripRoutes.MaxStops} durakla sınırlı olduğu için {skipped} müşteri eklenemedi." : text;
    }

    private static string? Clean(string? text, int max, string label)
    {
        string clean = (text ?? string.Empty).Trim();
        return clean.Length == 0 ? null : clean.Length > max ? throw new ArgumentException($"{label} en fazla {max} karakter olabilir.") : clean;
    }

    // ---- Customer locations ----

    public static async Task<CustomerLocationView?> GetLocationAsync(int customerId)
    {
        await using ArsanGazDbContext db = new();
        CustomerLocation? l = await db.CustomerLocations.AsNoTracking().FirstOrDefaultAsync(x => x.CustomerId == customerId);
        return l is null ? null : View(l);
    }

    public static async Task<Dictionary<int, CustomerLocationView>> LocationsAsync(IReadOnlyCollection<int> customerIds)
    {
        await using ArsanGazDbContext db = new();
        return await db.CustomerLocations.AsNoTracking().Where(x => customerIds.Contains(x.CustomerId))
            .ToDictionaryAsync(x => x.CustomerId, View);
    }

    public static async Task<CustomerLocationView> SetLocationAsync(int customerId, double latitude, double longitude, string by, string source)
    {
        if (!InTurkey(latitude, longitude))
        {
            throw new ArgumentException("Konum Türkiye sınırları içinde değil; enlem ve boylamı kontrol edin.");
        }

        await using ArsanGazDbContext db = new();
        string name = await db.Customers.AsNoTracking().Where(x => x.Id == customerId).Select(x => x.CompanyName).FirstOrDefaultAsync()
            ?? throw new ArgumentException("Müşteri bulunamadı.");
        CustomerLocation? location = await db.CustomerLocations.FirstOrDefaultAsync(x => x.CustomerId == customerId);
        if (location is null)
        {
            location = new CustomerLocation { CustomerId = customerId };
            db.CustomerLocations.Add(location);
        }

        location.Latitude = Math.Round(latitude, 6);
        location.Longitude = Math.Round(longitude, 6);
        location.UpdatedAt = DateTime.Now;
        location.UpdatedBy = by;
        location.Source = source;
        location.Verified = true;
        location.Label = null;
        await db.SaveChangesAsync();
        return View(location);
    }

    // Saves a location found on Google as not verified. A verified one (entered, saved at the customer or confirmed)
    // is kept unless replaceVerified is set by a user asking for it; returns null when it was kept.
    public static async Task<CustomerLocationView?> SetFoundLocationAsync(
        int customerId, double latitude, double longitude, string? label, string by, bool replaceVerified)
    {
        if (!InTurkey(latitude, longitude))
        {
            throw new ArgumentException("Bulunan konum Türkiye sınırları içinde değil.");
        }

        await using ArsanGazDbContext db = new();
        if (!await db.Customers.AnyAsync(x => x.Id == customerId))
        {
            throw new ArgumentException("Müşteri bulunamadı.");
        }

        CustomerLocation? location = await db.CustomerLocations.FirstOrDefaultAsync(x => x.CustomerId == customerId);
        if (location is { Verified: true } && !replaceVerified)
        {
            return null;
        }

        if (location is null)
        {
            location = new CustomerLocation { CustomerId = customerId };
            db.CustomerLocations.Add(location);
        }

        location.Latitude = Math.Round(latitude, 6);
        location.Longitude = Math.Round(longitude, 6);
        location.UpdatedAt = DateTime.Now;
        location.UpdatedBy = by;
        location.Source = "google";
        location.Verified = false;
        location.Label = label is { Length: > 300 } ? label[..300] : label;
        await db.SaveChangesAsync();
        return View(location);
    }

    // Confirms a found location as it is. Null when the customer has no location.
    public static async Task<CustomerLocationView?> VerifyLocationAsync(int customerId, string by)
    {
        await using ArsanGazDbContext db = new();
        CustomerLocation? location = await db.CustomerLocations.FirstOrDefaultAsync(x => x.CustomerId == customerId);
        if (location is null)
        {
            return null;
        }

        if (!location.Verified)
        {
            location.Verified = true;
            location.UpdatedAt = DateTime.Now;
            location.UpdatedBy = by;
            await db.SaveChangesAsync();
        }

        return View(location);
    }

    public static async Task<bool> ClearLocationAsync(int customerId, string by)
    {
        await using ArsanGazDbContext db = new();
        CustomerLocation? location = await db.CustomerLocations.FirstOrDefaultAsync(x => x.CustomerId == customerId);
        if (location is null)
        {
            return false;
        }

        db.CustomerLocations.Remove(location);
        await db.SaveChangesAsync();
        return true;
    }

    private static CustomerLocationView View(CustomerLocation l) =>
        new(l.CustomerId, l.Latitude, l.Longitude, l.UpdatedAt, l.UpdatedBy, l.Source, TripRoutes.CoordinateMapUrl(l.Latitude, l.Longitude),
            l.Verified, l.Label);

    public static bool InTurkey(double latitude, double longitude) =>
        latitude is >= 35.0 and <= 43.0 && longitude is >= 25.0 and <= 46.0;

    // Great-circle distance in metres.
    public static double DistanceMeters(double lat1, double lng1, double lat2, double lng2)
    {
        const double R = 6371000;
        double dLat = (lat2 - lat1) * Math.PI / 180;
        double dLng = (lng2 - lng1) * Math.PI / 180;
        double a = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
            + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * R * Math.Asin(Math.Min(1, Math.Sqrt(a)));
    }

    // ---- Reading coordinates from what users paste ----

    private static readonly Regex[] UrlPatterns =
    [
        new(@"!3d(-?\d{1,3}\.\d+)!4d(-?\d{1,3}\.\d+)", RegexOptions.CultureInvariant),
        new(@"@(-?\d{1,3}\.\d+),(-?\d{1,3}\.\d+)", RegexOptions.CultureInvariant),
        new(@"[?&](?:q|query|ll|sll|destination|daddr|center)=(?:loc:)?\s*(-?\d{1,3}\.\d+)\s*,\s*\+?(-?\d{1,3}\.\d+)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase),
        new(@"/(?:search|place|dir)/(?:[^/]*/)?\+?(-?\d{1,3}\.\d+)\s*,\s*\+?(-?\d{1,3}\.\d+)", RegexOptions.CultureInvariant)
    ];

    private static readonly Regex PlainPair = new(@"^\s*(-?\d{1,3}[.,]\d+)\s*[,;\s]\s*(-?\d{1,3}[.,]\d+)\s*$", RegexOptions.CultureInvariant);

    private static readonly Regex Dms = new(
        @"(\d{1,3})°\s*(\d{1,2})['′]\s*(\d{1,2}(?:[.,]\d+)?)[""″]?\s*([NSKG])\W+(\d{1,3})°\s*(\d{1,2})['′]\s*(\d{1,2}(?:[.,]\d+)?)[""″]?\s*([EWDB])",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    // Coordinates from "37.0001, 35.3213", "37,0001 35,3213", 37°00'00.4"N 35°19'16.7"E or a Google Maps link that
    // carries them. Null when nothing usable is found; short links have to be opened first (ResolveAsync).
    public static (double Latitude, double Longitude)? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        string value = text.Trim();
        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(value.Replace('+', ' '));
        }
        catch (UriFormatException)
        {
            decoded = value;
        }

        foreach (Regex pattern in UrlPatterns)
        {
            Match m = pattern.Match(decoded);
            if (m.Success && TryPair(m.Groups[1].Value, m.Groups[2].Value) is { } found)
            {
                return found;
            }
        }

        Match plain = PlainPair.Match(decoded);
        if (plain.Success)
        {
            return TryPair(plain.Groups[1].Value, plain.Groups[2].Value);
        }

        Match dms = Dms.Match(decoded);
        if (dms.Success)
        {
            double lat = Degrees(dms.Groups[1].Value, dms.Groups[2].Value, dms.Groups[3].Value) * (dms.Groups[4].Value.ToUpperInvariant() is "S" or "G" ? -1 : 1);
            double lng = Degrees(dms.Groups[5].Value, dms.Groups[6].Value, dms.Groups[7].Value) * (dms.Groups[8].Value.ToUpperInvariant() is "W" or "B" ? -1 : 1);
            return InRange(lat, lng) ? (lat, lng) : null;
        }

        return null;
    }

    private static (double, double)? TryPair(string a, string b) =>
        double.TryParse(a.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double lat)
        && double.TryParse(b.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double lng)
        && InRange(lat, lng)
            ? (lat, lng)
            : null;

    private static bool InRange(double lat, double lng) => lat is >= -90 and <= 90 && lng is >= -180 and <= 180;

    private static double Degrees(string d, string m, string s) =>
        double.Parse(d, CultureInfo.InvariantCulture) + double.Parse(m, CultureInfo.InvariantCulture) / 60
        + double.Parse(s.Replace(',', '.'), CultureInfo.InvariantCulture) / 3600;

    private static readonly HttpClient ShortLinks = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(5) };

    public static bool IsShortLink(Uri uri) =>
        uri.Scheme == Uri.UriSchemeHttps && uri.Host.ToLowerInvariant() is "maps.app.goo.gl" or "goo.gl" or "g.co";

    private static bool IsGoogleMaps(Uri uri)
    {
        string host = uri.Host.ToLowerInvariant();
        return uri.Scheme == Uri.UriSchemeHttps && (host == "maps.google.com" || host == "www.google.com" || host == "google.com"
            || host.StartsWith("www.google.", StringComparison.Ordinal) || host.StartsWith("maps.google.", StringComparison.Ordinal));
    }

    // Like Parse, but a Google short link (maps.app.goo.gl/...) is followed to the full link first. Only Google's own
    // short-link hosts are contacted.
    public static async Task<(double Latitude, double Longitude)?> ResolveAsync(string? text)
    {
        if (Parse(text) is { } direct)
        {
            return direct;
        }

        if (!Uri.TryCreate(text?.Trim(), UriKind.Absolute, out Uri? uri) || !IsShortLink(uri))
        {
            return null;
        }

        for (int hop = 0; hop < 4 && uri is not null; hop++)
        {
            using HttpRequestMessage request = new(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (ArsanGazERP)");
            using HttpResponseMessage response = await ShortLinks.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            Uri? next = response.Headers.Location;
            if (next is null)
            {
                return null;
            }

            next = next.IsAbsoluteUri ? next : new Uri(uri, next);
            if (Parse(next.ToString()) is { } found)
            {
                return found;
            }

            uri = IsShortLink(next) || IsGoogleMaps(next) ? next : null;
        }

        return null;
    }
}
