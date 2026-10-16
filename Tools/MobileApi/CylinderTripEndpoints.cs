using ArsanGazERP.Models;
using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed record TripStartRequest(string? Note, List<string?>? Plates);
internal sealed record TripVehicleRequest(int TripId, string? Plate);
internal sealed record TripLoadRequest(int TripId, string? Plate, List<string?>? Codes);
internal sealed record TripDeliverRequest(int TripId, int? CustomerId, List<string?>? Codes, string? Note);
internal sealed record TripReturnRequest(int TripId, string? Plate, List<string?>? Codes, string? Note);
internal sealed record TripCloseRequest(int TripId);
internal sealed record TripReasonRequest(int TripId, List<int>? CylinderIds, string? Reason, string? Note, int? CustomerId = null);
internal sealed record TripCheckRequest(int TripId, List<int>? CylinderIds, bool Undo);

internal static class CylinderTripEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sefer/aktif", async () => Results.Ok(await new CylinderTripService().ActiveTripsAsync())).Requires(PanelPermissions.TripView);

        // Closed trips: Yönetici/Operatör see all; other accounts only see trips they closed (legacy rows: started by them).
        app.MapGet("/api/sefer/gecmis", async (HttpContext context, int? adet) =>
        {
            WebPanelUser user = Account(context);
            bool seeAll = SeesAllClosedTrips(user);
            List<TripListItem> trips = await new CylinderTripService().ClosedTripsAsync(
                null, adet ?? 40, seeAll, seeAll ? null : user.UserName);
            return Results.Ok(trips.Select(HistoryRow));
        }).Requires(PanelPermissions.TripView);

        app.MapGet("/api/sefer/{tripId:int}/detay", async (int tripId, HttpContext context) =>
        {
            TripDetail? detail = await new CylinderTripService().TripDetailAsync(tripId);
            if (detail is null)
            {
                return Results.NotFound(new { error = "Sefer bulunamadı." });
            }

            if (!MayViewClosedOrActive(Account(context), detail.Trip))
            {
                return Results.Json(new { error = "Bu seferi görme yetkiniz yok." }, statusCode: StatusCodes.Status403Forbidden);
            }

            return Results.Ok(new
            {
                Trip = HistoryRow(detail.Trip),
                Cylinders = detail.Cylinders.Select(c => new
                {
                    c.CylinderId,
                    c.SerialNumber,
                    c.GasType,
                    c.Plate,
                    Loaded = c.LoadedAt.ToString("dd.MM.yyyy HH:mm"),
                    Delivered = c.DeliveredAt?.ToString("dd.MM.yyyy HH:mm"),
                    Returned = c.ReturnedAt?.ToString("dd.MM.yyyy HH:mm"),
                    c.Missing,
                    c.ReasonLabel,
                    c.Note,
                    c.IsPickup,
                    c.PickupCustomer
                })
            });
        }).Requires(PanelPermissions.TripView);

        app.MapPost("/api/sefer/baslat", async (TripStartRequest request, HttpContext context) =>
        {
            try
            {
                CylinderTrip trip = await new CylinderTripService().StartTripAsync(
                    request.Plates, request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(new { trip.Id, trip.Title, trip.StartedAt, message = $"{trip.Title} başlatıldı." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripStart);

        app.MapPost("/api/sefer/arac", async (TripVehicleRequest request, HttpContext context) =>
        {
            try
            {
                await new CylinderTripService().AddVehicleAsync(request.TripId, request.Plate);
                return Results.Ok(new { message = $"{request.Plate} sefere eklendi." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripStart);

        app.MapPost("/api/sefer/yukle", async (TripLoadRequest request, HttpContext context) =>
        {
            try
            {
                TripLoadResult result = await new CylinderTripService().LoadCylindersAsync(
                    request.TripId, request.Plate, request.Codes ?? [], "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(new
                {
                    message = $"{result.Added} dolu tüp sefere yüklendi." + (result.Skipped > 0 ? $" {result.Skipped} atlandı." : string.Empty),
                    result.Added,
                    result.Skipped,
                    result.Messages,
                    refused = result.RefusedCodes
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripLoad);

        app.MapPost("/api/sefer/bos-al", async (TripLoadRequest request, HttpContext context) =>
        {
            try
            {
                TripLoadResult result = await new CylinderTripService().PickupEmptiesAsync(
                    request.TripId, request.Plate, request.Codes ?? [], "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(new
                {
                    message = $"{result.Added} boş tüp müşteriden araca alındı." + (result.Skipped > 0 ? $" {result.Skipped} atlandı." : string.Empty),
                    result.Added,
                    result.Skipped,
                    result.Messages,
                    refused = result.RefusedCodes
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripDeliver);

        app.MapPost("/api/sefer/teslim", async (TripDeliverRequest request, HttpContext context) =>
        {
            try
            {
                List<CylinderBulkResult> results = await new CylinderTripService().DeliverCylindersAsync(
                    request.TripId, request.Codes ?? [], request.CustomerId, request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                int ok = results.Count(r => r.Ok);
                string failures = string.Join("\n", results.Where(r => !r.Ok).Select(r => $"{r.Code}: {r.Message}"));
                return Results.Ok(new { message = $"{ok}/{results.Count} tüp teslim kaydedildi." + (failures.Length > 0 ? "\n" + failures : string.Empty) });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripDeliver);

        app.MapPost("/api/sefer/donus", async (TripReturnRequest request, HttpContext context) =>
        {
            try
            {
                TripReturnResult result = await new CylinderTripService().ReturnCylindersAsync(
                    request.TripId, request.Plate, request.Codes ?? [], request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(TripReconcileDto(result.Reconcile, result.Message));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripDeliver);

        app.MapGet("/api/sefer/{tripId:int}/kontrol", async (int tripId, string? plaka) =>
        {
            try
            {
                TripReconcileResult reconcile = await new CylinderTripService().ReconcileTripAsync(tripId, plaka);
                return Results.Ok(TripReconcileDto(reconcile));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripView);

        app.MapPost("/api/sefer/kapat", async (TripCloseRequest request, HttpContext context) =>
        {
            try
            {
                // The account name (not the device) is stored as ClosedBy: it decides who sees the trip in "Tamamlanmış seferler".
                TripCloseResult result = await new CylinderTripService().CloseTripAsync(request.TripId, Account(context).UserName);
                NotifyManagers(result);
                return Results.Ok(new
                {
                    result.Message,
                    result.TripId,
                    reconcile = TripReconcileDto(result.Reconcile)
                });
            }
            catch (TripReasonsRequiredException exception)
            {
                return ReasonsRequired(exception);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripClose);

        app.MapGet("/api/sefer/gerekceler", () => Results.Ok(ReasonCatalog())).Requires(PanelPermissions.TripView);

        // Open (neither delivered nor returned) cylinders of a trip with their written reasons; the driver explains them here.
        app.MapGet("/api/sefer/{tripId:int}/eksikler", async (int tripId) =>
        {
            TripDetail? detail = await new CylinderTripService().TripDetailAsync(tripId);
            if (detail is null)
            {
                return Results.NotFound(new { error = "Sefer bulunamadı." });
            }

            return Results.Ok(detail.Cylinders.Where(c => c.DeliveredAt is null && c.ReturnedAt is null).Select(c => new
            {
                c.CylinderId,
                c.SerialNumber,
                c.GasType,
                c.Plate,
                c.Missing,
                c.Reason,
                c.ReasonLabel,
                c.Note,
                c.ReasonBy,
                Checked = c.CheckedAt is not null,
                c.IsPickup,
                c.PickupCustomer
            }));
        }).Requires(PanelPermissions.TripView);

        app.MapPost("/api/sefer/gerekce", async (TripReasonRequest request, HttpContext context) =>
        {
            try
            {
                TripReasonResult result = await new CylinderTripService().ExplainMissingAsync(
                    request.TripId, request.CylinderIds ?? [], request.Reason, request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")", request.CustomerId);
                return Results.Ok(new { message = ReasonMessage(result, "tüpe gerekçe yazıldı."), result.Updated, result.Messages });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripDeliver);

        app.MapPost("/api/sefer/gerekce-kontrol", async (TripCheckRequest request, HttpContext context) =>
        {
            try
            {
                TripReasonResult result = await new CylinderTripService().CheckMissingAsync(
                    request.TripId, request.CylinderIds ?? [], "Mobil (" + MobileEndpoints.DeviceName(context) + ")", request.Undo);
                return Results.Ok(new { message = ReasonMessage(result, request.Undo ? "tüpün kontrolü geri alındı." : "tüp kontrol edildi."), result.Updated, result.Messages });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripClose);
    }

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;

    private static readonly System.Globalization.CultureInfo Turkish = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");

    private static object HistoryRow(TripListItem t) => new
    {
        t.Id,
        Number = "#" + t.Id,
        t.Title,
        Started = t.StartedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
        Closed = t.ClosedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
        Plates = t.Vehicles.Select(v => v.Plate).ToList(),
        t.Loaded,
        t.Delivered,
        t.Returned,
        t.PickedUp,
        t.OnVehicle,
        t.Missing,
        t.Note,
        t.ClosedBy
    };

    // Yönetici and Operatör see every closed trip; the other accounts only their own.
    private static bool SeesAllClosedTrips(WebPanelUser user) =>
        user.Role is PanelRoles.Admin or PanelRoles.Operator;

    private static bool MayViewClosedOrActive(WebPanelUser user, TripListItem trip)
    {
        if (SeesAllClosedTrips(user) || trip.Active)
        {
            return true;
        }

        // First-app panel users have no FixedPlate; without ClosedBy, hide the trip (no CreatedBy substring leak).
        return !string.IsNullOrWhiteSpace(trip.ClosedBy)
            && string.Equals(trip.ClosedBy, user.UserName, StringComparison.OrdinalIgnoreCase);
    }

    internal static object ReasonCatalog() =>
        TripMissingReasons.All.Select(r => new { key = r.Key, label = r.Label, noteRequired = r.NoteRequired, asksCustomer = r.AsksCustomer });

    internal static string ReasonMessage(TripReasonResult result, string done) =>
        $"{result.Updated} {done}" + (result.Messages.Count > 0 ? "\n" + string.Join("\n", result.Messages) : string.Empty);

    internal static IResult ReasonsRequired(TripReasonsRequiredException exception) =>
        Results.BadRequest(new { error = exception.Message, reasonsRequired = true, missing = exception.Serials });

    // The reasons of a closed trip go to the managers over the server's Telegram notification path (never to customers).
    internal static void NotifyManagers(TripCloseResult result)
    {
        if (result.ManagerNotice is string text)
        {
            TelegramNotifier.Send(text);
        }
    }

    private static object TripReconcileDto(TripReconcileResult reconcile, string? message = null) => new
    {
        message = message ?? reconcile.AlertText,
        hasAlerts = reconcile.HasAlerts,
        notDeliveredNotReturned = reconcile.NotDeliveredNotReturned,
        shortagesByGas = reconcile.ShortagesByGas.Select(x => new { gasType = x.GasType, count = x.Count }),
        unexpectedReturnCodes = reconcile.UnexpectedReturnCodes,
        loadedButNotAccountedFor = reconcile.LoadedButNotAccountedFor,
        unexplainedMissing = reconcile.UnexplainedMissing,
        missingEmpties = reconcile.MissingEmpties
    };
}
