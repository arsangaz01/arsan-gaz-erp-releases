using ArsanGazERP.Data;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record TripPlanStopRequest(int MusteriId, string? Not);
internal sealed record TripPlanSaveRequest(int? Id, DateTime? Tarih, string? Plaka, int? Mesafe, string? Not, List<TripPlanStopRequest>? Duraklar);
internal sealed record TripStopAddRequest(int TripId, string? Plate, int CustomerId);
internal sealed record TripStopMoveRequest(int TripId, int StopId, int Direction);
internal sealed record TripStopRequest(int TripId, int StopId);
internal sealed record TripStopDoneRequest(int TripId, int StopId, bool Done, string? Note);
internal sealed record CustomerLocationTextRequest(string? Metin);
internal sealed record CustomerLocationSaveRequest(double Latitude, double Longitude, double? Accuracy = null);

// Panel Sefer Planı tab: day plans per vehicle with the warning distance, and customer locations entered as a Google
// Maps link or coordinates. The phone side (today's plan, saving a location at the customer) lives in CylinderTripEndpoints.
internal static class TripPlanEndpoints
{
    internal static readonly int[] DistanceChoices = [100, 200, 300, 500, 750, 1000, 1500, 2000];

    internal static void MapPanel(RouteGroupBuilder api)
    {
        api.MapGet("/sefer-planlari", async (DateTime? tarih) =>
        {
            DateTime day = (tarih ?? DateTime.Today).Date;
            List<TripPlanView> plans = await TripPlans.ListAsync(day);
            await using ArsanGazDbContext db = new();
            var vehicles = await db.Vehicles.AsNoTracking().Where(v => v.IsActive).OrderBy(v => v.Plate)
                .Select(v => new { v.Plate, v.Name, v.DriverName }).ToListAsync();
            return Results.Ok(new
            {
                date = day.ToString("yyyy-MM-dd"),
                defaultMeters = TripPlans.DefaultAlertMeters,
                distances = DistanceChoices,
                google = false,
                vehicles,
                plans = plans.Select(p => Row(p, false, vehicles.Where(v => string.Equals(v.Plate, p.Plate, StringComparison.OrdinalIgnoreCase)
                    && !string.IsNullOrWhiteSpace(v.DriverName)).Select(v => v.DriverName!).ToList()))
            });
        }).Requires(PanelPermissions.TripPlan);

        api.MapGet("/sefer-plani/{id:int}", async (int id) =>
        {
            TripPlanView? plan = await TripPlans.GetAsync(id);
            return plan is null ? Results.NotFound(new { error = "Plan bulunamadı." }) : Results.Ok(Row(plan, true, []));
        }).Requires(PanelPermissions.TripPlan);

        api.MapPost("/sefer-plani", async (TripPlanSaveRequest request, HttpContext context) =>
        {
            try
            {
                string user = Account(context).UserName;
                CrudService crud = new();
                List<TripPlanStopInput> stops = [];
                foreach (TripPlanStopRequest stop in request.Duraklar ?? [])
                {
                    int customerId = await crud.ResolveCustomerIdAsync(stop.MusteriId) ?? stop.MusteriId;
                    stops.Add(new TripPlanStopInput(customerId, stop.Not));
                }

                (int id, string message) = await TripPlans.SaveAsync(request.Id, request.Tarih ?? DateTime.Today, request.Plaka,
                    request.Mesafe ?? TripPlans.DefaultAlertMeters, request.Not, stops, "Web panel (" + user + ")");
                WebPanelAudit.Write(user, request.Id is null ? "Sefer planı oluşturuldu" : "Sefer planı güncellendi", $"Plan #{id}",
                    $"{CylinderTracking.NormalizePlate(request.Plaka)} {request.Tarih:dd.MM.yyyy}, {stops.Count} müşteri, {request.Mesafe} m",
                    WebPanelAuth.ClientIp(context));
                return Results.Ok(new { id, message });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripPlan);

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
        }).Requires(PanelPermissions.TripPlan);

        api.MapGet("/musteri/{id:int}/konum", async (int id) =>
        {
            CustomerLocationView? location = id > 0 ? await TripPlans.GetLocationAsync(id) : null;
            return Results.Ok(new { location = location is null ? null : LocationRow(location) });
        }).Requires(PanelPermissions.TripPlan);

        // Empty text clears the location.
        api.MapPost("/musteri/{id:int}/konum", async (int id, CustomerLocationTextRequest request, HttpContext context) =>
        {
            string user = Account(context).UserName;
            int customerId = await new CrudService().ResolveCustomerIdAsync(id) ?? id;
            string text = (request.Metin ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                bool removed = await TripPlans.ClearLocationAsync(customerId, user);
                if (removed)
                {
                    WebPanelAudit.Write(user, "Müşteri konumu silindi", $"Müşteri #{customerId}", null, WebPanelAuth.ClientIp(context));
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
                CustomerLocationView saved = await TripPlans.SetLocationAsync(customerId, found.Value.Latitude, found.Value.Longitude, "Web panel (" + user + ")", "panel");
                WebPanelAudit.Write(user, "Müşteri konumu kaydedildi", $"Müşteri #{customerId}", null, WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = "Konum kaydedildi.", location = LocationRow(saved) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripPlan);

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
        }).Requires(PanelPermissions.TripPlan);
    }

    // Phone: today's plan, the trip route (visiting order) and saving a customer location at the customer's door.
    internal static void MapMobile(WebApplication app)
    {
        app.MapGet("/api/sefer-plani/bugun", async (string? plaka) =>
            Results.Ok(new { on = true, plans = await TripPlans.TodayAsync(plaka) }))
            .Requires(PanelPermissions.TripRoute);

        app.MapGet("/api/sefer/{tripId:int}/rota", async (int tripId, string? plaka, HttpContext context) =>
        {
            try
            {
                bool phones = PanelRoles.Has(Account(context), PanelPermissions.CustomerView);
                return Results.Ok(await TripRoutes.ListAsync(tripId, plaka, phones, true));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripRoute);

        app.MapGet("/api/sefer/{tripId:int}/rota/oneriler", async (int tripId, string? plaka) =>
            Results.Ok(await TripRoutes.SuggestionsAsync(tripId, plaka, true))).Requires(PanelPermissions.TripRoute);

        app.MapPost("/api/sefer/rota/ekle", async (TripStopAddRequest request, HttpContext context) =>
            await RouteAsync(async () => await TripRoutes.AddAsync(request.TripId, request.Plate,
                request.CustomerId, "Mobil (" + MobileEndpoints.DeviceName(context) + ")"))).Requires(PanelPermissions.TripRoute);

        app.MapPost("/api/sefer/rota/sira", async (TripStopMoveRequest request) =>
            await RouteAsync(async () =>
            {
                await TripRoutes.MoveAsync(request.TripId, request.StopId, request.Direction);
                return "Sıra güncellendi.";
            })).Requires(PanelPermissions.TripRoute);

        app.MapPost("/api/sefer/rota/sil", async (TripStopRequest request) =>
            await RouteAsync(async () =>
            {
                await TripRoutes.RemoveAsync(request.TripId, request.StopId);
                return "Durak rotadan çıkarıldı.";
            })).Requires(PanelPermissions.TripRoute);

        app.MapPost("/api/sefer/rota/tamam", async (TripStopDoneRequest request) =>
            await RouteAsync(async () =>
            {
                await TripRoutes.SetDoneAsync(request.TripId, request.StopId, request.Done, request.Note);
                return request.Done ? "Durak tamamlandı olarak işaretlendi." : "Durak yeniden açıldı.";
            })).Requires(PanelPermissions.TripRoute);

        // Saved by a driver standing at the customer; the phone sends its own GPS fix.
        app.MapPost("/api/musteri/{id:int}/konum", async (int id, CustomerLocationSaveRequest request, HttpContext context) =>
        {
            if (request.Accuracy is > 100)
            {
                return Results.BadRequest(new { error = $"Konum hassasiyeti düşük (±{request.Accuracy:0} m). Açık alanda birkaç saniye bekleyip yeniden deneyin." });
            }

            try
            {
                string by = "Mobil (" + MobileEndpoints.DeviceName(context) + ")";
                CustomerLocationView saved = await TripPlans.SetLocationAsync(id, request.Latitude, request.Longitude, by, "telefon");
                return Results.Ok(new { message = "Müşterinin konumu kaydedildi.", saved.Latitude, saved.Longitude, saved.MapUrl });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.CustomerLocation);

        app.MapPost("/api/musteri/{id:int}/konum/onayla", async (int id, HttpContext context) =>
        {
            string by = "Mobil (" + MobileEndpoints.DeviceName(context) + ")";
            CustomerLocationView? location = await TripPlans.VerifyLocationAsync(id, by);
            return location is null
                ? Results.BadRequest(new { error = "Müşterinin kayıtlı konumu yok." })
                : Results.Ok(new { message = "Konum onaylandı." });
        }).Requires(PanelPermissions.CustomerLocation);
    }

    private static async Task<IResult> RouteAsync(Func<Task<string>> action)
    {
        try
        {
            return Results.Ok(new { message = await action() });
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    // Today's trip plans of the trip's vehicles go onto the route; a failure here never undoes the trip start.
    internal static async Task ApplyPlansAsync(int tripId, string? plate, string by)
    {
        try
        {
            foreach (string message in await TripPlans.ApplyForTripAsync(tripId, plate, by))
            {
                Console.WriteLine($"Sefer planı sefer #{tripId} rotasına işlendi — {message} ({by}).");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Console.WriteLine($"Sefer planı sefer #{tripId} rotasına işlenemedi: {exception.Message}");
        }
    }

    private static object Row(TripPlanView p, bool withStops, List<string> drivers) => new
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
        Drivers = drivers,
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
