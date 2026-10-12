using ArsanTupTakip.Models;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Api;

internal sealed record TripStartRequest(string? Note, List<string?>? Plates);
internal sealed record TripVehicleRequest(int TripId, string? Plate);
internal sealed record TripLoadRequest(int TripId, string? Plate, List<string?>? Codes);
internal sealed record TripDeliverRequest(int TripId, int? CustomerId, List<string?>? Codes, string? Note);
internal sealed record TripReturnRequest(int TripId, string? Plate, List<string?>? Codes, string? Note);
internal sealed record TripCloseRequest(int TripId);

internal static class CylinderTripEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sefer/aktif", async () => Results.Ok(await new CylinderTripService().ActiveTripsAsync()));

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
        });

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
        });

        app.MapPost("/api/sefer/yukle", async (TripLoadRequest request, HttpContext context) =>
        {
            try
            {
                TripLoadResult result = await new CylinderTripService().LoadCylindersAsync(
                    request.TripId, request.Plate, request.Codes ?? [], "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(new
                {
                    message = $"{result.Added} tüp sefere yüklendi." + (result.Skipped > 0 ? $" {result.Skipped} atlandı." : string.Empty),
                    result.Added,
                    result.Skipped,
                    result.Messages
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

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
        });

        app.MapPost("/api/sefer/donus", async (TripReturnRequest request, HttpContext context) =>
        {
            try
            {
                TripReconcileResult reconcile = await new CylinderTripService().ReturnCylindersAsync(
                    request.TripId, request.Plate, request.Codes ?? [], request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(TripReconcileDto(reconcile));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

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
        });

        app.MapPost("/api/sefer/kapat", async (TripCloseRequest request) =>
        {
            try
            {
                TripCloseResult result = await new CylinderTripService().CloseTripAsync(request.TripId);
                return Results.Ok(new
                {
                    result.Message,
                    result.TripId,
                    reconcile = TripReconcileDto(result.Reconcile)
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }

    private static object TripReconcileDto(TripReconcileResult reconcile) => new
    {
        message = reconcile.AlertText,
        hasAlerts = reconcile.HasAlerts,
        notDeliveredNotReturned = reconcile.NotDeliveredNotReturned,
        shortagesByGas = reconcile.ShortagesByGas.Select(x => new { gasType = x.GasType, count = x.Count }),
        unexpectedReturnCodes = reconcile.UnexpectedReturnCodes,
        loadedButNotAccountedFor = reconcile.LoadedButNotAccountedFor
    };
}
