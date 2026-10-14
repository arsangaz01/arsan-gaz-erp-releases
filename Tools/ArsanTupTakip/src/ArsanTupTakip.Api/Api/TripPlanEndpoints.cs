using ArsanTupTakip.Database;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record TripPlanStopRequest(int MusteriId, string? Not);
internal sealed record TripPlanSaveRequest(int? Id, DateTime? Tarih, string? Plaka, int? Mesafe, string? Not, List<TripPlanStopRequest>? Duraklar);
internal sealed record CustomerLocationTextRequest(string? Metin);
internal sealed record CustomerGoogleRequest(bool? Degistir);

// Panel Sefer Planı tab: day plans per vehicle with the warning distance, and customer locations entered as a Google
// Maps link or coordinates. Everything here works only while the "Sefer planı" feature is on (Perm.TripPlan).
internal static class TripPlanEndpoints
{
    internal static readonly int[] DistanceChoices = [100, 200, 300, 500, 750, 1000, 1500, 2000];

    internal static void MapPanel(RouteGroupBuilder api)
    {
        api.MapGet("/sefer-planlari", async (DateTime? tarih) =>
        {
            DateTime day = (tarih ?? DateTime.Today).Date;
            List<TripPlanView> plans = await TripPlans.ListAsync(day);
            await using TupTakipDbContext db = new();
            var vehicles = await db.Vehicles.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Plate)
                .Select(v => new { v.Plate, v.Name, v.DriverName }).ToListAsync();
            return Results.Ok(new
            {
                date = day.ToString("yyyy-MM-dd"),
                defaultMeters = TripPlans.DefaultAlertMeters,
                distances = DistanceChoices,
                google = CustomerGeocoder.Configured,
                vehicles,
                plans = plans.Select(p => Row(p, false))
            });
        }).Requires(Perm.TripPlan);

        api.MapGet("/sefer-plani/{id:int}", async (int id) =>
        {
            TripPlanView? plan = await TripPlans.GetAsync(id);
            return plan is null ? Results.NotFound(new { error = "Plan bulunamadı." }) : Results.Ok(Row(plan, true));
        }).Requires(Perm.TripPlan);

        api.MapPost("/sefer-plani", async (TripPlanSaveRequest request, HttpContext context) =>
        {
            try
            {
                string user = Account(context).UserName;
                (int id, string message) = await TripPlans.SaveAsync(request.Id, request.Tarih ?? DateTime.Today, request.Plaka,
                    request.Mesafe ?? TripPlans.DefaultAlertMeters, request.Not,
                    (request.Duraklar ?? []).Select(d => new TripPlanStopInput(d.MusteriId, d.Not)).ToList(), "Web panel (" + user + ")");
                WebPanelAudit.Write(user, request.Id is null ? "Sefer planı oluşturuldu" : "Sefer planı güncellendi", $"Plan #{id}",
                    $"{CylinderTracking.NormalizePlate(request.Plaka)} {request.Tarih:dd.MM.yyyy}, {request.Duraklar?.Count ?? 0} müşteri, {request.Mesafe} m",
                    WebPanelAuth.ClientIp(context));
                if (CustomerGeocoder.Configured && request.Duraklar is { Count: > 0 } stops
                    && CustomerGeocoder.Start(stops.Select(d => d.MusteriId).ToList(), $"Plan #{id}", "Google (" + user + ")", AppLog.Write))
                {
                    message += " Konumu olmayan müşteriler Google'da aranıyor.";
                }

                return Results.Ok(new { id, message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripPlan);

        api.MapPost("/sefer-plani/{id:int}/sil", async (int id, HttpContext context) =>
        {
            try
            {
                string message = await TripPlans.DeleteAsync(id);
                WebPanelAudit.Write(Account(context).UserName, "Sefer planı silindi", $"Plan #{id}", null, WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripPlan);

        api.MapGet("/musteri/{id:int}/konum", async (int id) =>
        {
            CustomerLocationView? location = await TripPlans.GetLocationAsync(id);
            return Results.Ok(new { location = location is null ? null : LocationRow(location) });
        }).Requires(Perm.TripPlan);

        // Empty text clears the location.
        api.MapPost("/musteri/{id:int}/konum", async (int id, CustomerLocationTextRequest request, HttpContext context) =>
        {
            string user = Account(context).UserName;
            string text = (request.Metin ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                bool removed = await TripPlans.ClearLocationAsync(id, user);
                if (removed)
                {
                    WebPanelAudit.Write(user, "Müşteri konumu silindi", $"Müşteri #{id}", null, WebPanelAuth.ClientIp(context));
                }

                return Results.Ok(new { message = removed ? "Konum silindi." : "Kayıtlı konum yoktu.", location = (object?)null });
            }

            if (text.Length > 2000)
            {
                return Results.BadRequest(new { error = "Bağlantı çok uzun." });
            }

            (double Latitude, double Longitude)? found;
            try
            {
                found = await TripPlans.ResolveAsync(text);
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
            {
                return Results.BadRequest(new { error = "Kısa harita bağlantısı açılamadı. Bağlantıyı tarayıcıda açıp adres çubuğundaki uzun bağlantıyı veya koordinatı yapıştırın." });
            }

            if (found is null)
            {
                return Results.BadRequest(new { error = "Bağlantıda koordinat bulunamadı. Google Haritalar'da müşterinin yerine uzun basın; üstte çıkan \"37.00123, 35.32111\" gibi koordinatı veya paylaş bağlantısını yapıştırın." });
            }

            try
            {
                CustomerLocationView saved = await TripPlans.SetLocationAsync(id, found.Value.Latitude, found.Value.Longitude, "Web panel (" + user + ")", "panel");
                WebPanelAudit.Write(user, "Müşteri konumu kaydedildi", $"Müşteri #{id}", null, WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = "Konum kaydedildi.", location = LocationRow(saved) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripPlan);

        api.MapPost("/musteri/{id:int}/konum/onayla", async (int id, HttpContext context) =>
        {
            string user = Account(context).UserName;
            CustomerLocationView? location = await TripPlans.VerifyLocationAsync(id, "Web panel (" + user + ")");
            if (location is null)
            {
                return Results.BadRequest(new { error = "Müşterinin kayıtlı konumu yok." });
            }

            WebPanelAudit.Write(user, "Müşteri konumu onaylandı", $"Müşteri #{id}", null, WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = "Konum onaylandı.", location = LocationRow(location) });
        }).Requires(Perm.TripPlan);

        // Looks the customer up on Google now. A verified location is replaced only when the user says so (Degistir).
        api.MapPost("/musteri/{id:int}/konum/google", async (int id, CustomerGoogleRequest request, HttpContext context) =>
        {
            string user = Account(context).UserName;
            await using TupTakipDbContext db = new();
            Models.Customer? customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
            if (customer is null)
            {
                return Results.NotFound(new { error = "Müşteri bulunamadı." });
            }

            GeoMatch? match;
            try
            {
                match = await CustomerGeocoder.FindAsync(customer);
            }
            catch (GeocoderException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
            {
                AppLog.Write($"Google konum araması müşteri #{id}: {exception.Message}");
                return Results.BadRequest(new { error = "Google'a ulaşılamadı; biraz sonra tekrar deneyin." });
            }

            if (match is null)
            {
                return Results.BadRequest(new { error = "Google'da bu müşteri bulunamadı. Haritada yerini bulup bağlantıyı veya koordinatı yapıştırın." });
            }

            CustomerLocationView? saved = await TripPlans.SetFoundLocationAsync(id, match.Latitude, match.Longitude, match.Label, "Google (" + user + ")", request.Degistir == true);
            if (saved is null)
            {
                return Results.Conflict(new { error = "Müşterinin onaylı bir konumu var.", label = match.Label, confirm = true });
            }

            WebPanelAudit.Write(user, "Müşteri konumu Google'dan alındı", $"Müşteri #{id}", match.Method, WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = "Google'da bulundu; haritada kontrol edip onaylayın.", location = LocationRow(saved) });
        }).Requires(Perm.TripPlan);

        // Bulk lookup of every active customer without a location; Yönetici only (the /yonetim path).
        api.MapGet("/yonetim/konum-bul", async () =>
        {
            await using TupTakipDbContext db = new();
            int customers = await db.Customers.CountAsync(x => x.IsActive);
            int located = await db.CustomerLocations.CountAsync(l => db.Customers.Any(c => c.Id == l.CustomerId && c.IsActive));
            int unverified = await db.CustomerLocations.CountAsync(l => !l.Verified && db.Customers.Any(c => c.Id == l.CustomerId && c.IsActive));
            return Results.Ok(new { status = StatusRow(CustomerGeocoder.Status), customers, located, unverified });
        }).Requires(Perm.TripPlan);

        api.MapPost("/yonetim/konum-bul", (HttpContext context) =>
        {
            string user = Account(context).UserName;
            try
            {
                if (!CustomerGeocoder.Start(null, "Tüm müşteriler", "Google (" + user + ")", AppLog.Write))
                {
                    return Results.BadRequest(new { error = "Bir konum arama zaten sürüyor." });
                }
            }
            catch (GeocoderException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            WebPanelAudit.Write(user, "Toplu Google konum arama başlatıldı", "Müşteriler", null, WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = "Konumu olmayan müşteriler Google'da aranıyor. Bulunanlar \"doğrulanmamış\" olarak kaydedilir." });
        }).Requires(Perm.TripPlan);

        api.MapPost("/yonetim/konum-bul/durdur", (HttpContext context) =>
        {
            bool stopped = CustomerGeocoder.Stop();
            if (stopped)
            {
                WebPanelAudit.Write(Account(context).UserName, "Toplu Google konum arama durduruldu", "Müşteriler", null, WebPanelAuth.ClientIp(context));
            }

            return Results.Ok(new { message = stopped ? "Durduruluyor." : "Süren bir arama yok." });
        }).Requires(Perm.TripPlan);
    }

    private static object StatusRow(LocationLookupStatus s) => new
    {
        s.Configured,
        s.Running,
        s.Scope,
        s.Total,
        s.Done,
        s.Found,
        s.NotFound,
        s.Failed,
        s.LastError,
        Started = s.StartedAt?.ToString("dd.MM.yyyy HH:mm"),
        Finished = s.FinishedAt?.ToString("dd.MM.yyyy HH:mm"),
        s.StartedBy
    };

    private static object Row(TripPlanView p, bool withStops) => new
    {
        p.Id,
        date = p.Date.ToString("yyyy-MM-dd"),
        p.Plate,
        p.AlertMeters,
        p.Note,
        p.Status,
        p.TripId,
        p.StopCount,
        p.Located,
        p.Unverified,
        p.Done,
        Drivers = WebPanelUserStore.All()
            .Where(u => u.IsActive && string.Equals(u.FixedPlate, p.Plate, StringComparison.OrdinalIgnoreCase))
            .Select(u => string.IsNullOrWhiteSpace(u.DisplayName) ? u.UserName : u.DisplayName)
            .ToList(),
        Updated = p.UpdatedAt.ToString("dd.MM.yyyy HH:mm"),
        p.UpdatedBy,
        Stops = withStops ? p.Stops : null
    };

    private static object LocationRow(CustomerLocationView l) => new
    {
        l.Latitude,
        l.Longitude,
        l.MapUrl,
        l.Source,
        l.Verified,
        l.Label,
        l.UpdatedBy,
        Updated = l.UpdatedAt.ToString("dd.MM.yyyy HH:mm")
    };

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;
}
