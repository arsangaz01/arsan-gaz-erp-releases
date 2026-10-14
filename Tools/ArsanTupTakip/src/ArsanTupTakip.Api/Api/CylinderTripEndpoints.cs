using ArsanTupTakip.Models;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Api;

internal sealed record TripStartRequest(string? Note, List<string?>? Plates);
internal sealed record TripVehicleRequest(int TripId, string? Plate);
internal sealed record TripLoadRequest(int TripId, string? Plate, List<string?>? Codes);
internal sealed record TripDeliverRequest(int TripId, int? CustomerId, List<string?>? Codes, string? Note);
internal sealed record TripStopAddRequest(int TripId, string? Plate, int CustomerId);
internal sealed record TripStopMoveRequest(int TripId, int StopId, int Direction);
internal sealed record TripStopRequest(int TripId, int StopId);
internal sealed record TripStopDoneRequest(int TripId, int StopId, bool Done, string? Note);
internal sealed record CustomerLocationRequest(double Latitude, double Longitude, double? Accuracy);
internal sealed record TripPickupRequest(int TripId, string? Plate, int? CustomerId, List<string?>? Codes, string? Note);
internal sealed record TripReturnRequest(int TripId, string? Plate, List<string?>? Codes, string? Note);
internal sealed record TripCloseRequest(int TripId);
internal sealed record TripReasonRequest(int TripId, List<int>? CylinderIds, string? Reason, string? Note, int? CustomerId = null);

internal sealed class FixedVehicleBusyException(int tripId, string message) : InvalidOperationException(message)
{
    internal int TripId { get; } = tripId;
}

// A user with a fixed vehicle (WebPanelUser.FixedPlate, e.g. the depo1..depo6 drivers) works with that vehicle only:
// trips start with it automatically and other plates are refused for starting, adding, loading and returning.
internal static class DriverVehicle
{
    internal static IReadOnlyList<string?>? StartPlates(string? fixedPlate, IReadOnlyList<string?>? requested)
    {
        if (string.IsNullOrEmpty(fixedPlate))
        {
            return requested;
        }

        foreach (string? raw in requested ?? [])
        {
            PlateFor(fixedPlate, raw);
        }

        return [fixedPlate];
    }

    // The plate to use: the fixed vehicle when none is given, otherwise the given plate if it is allowed.
    internal static string? PlateFor(string? fixedPlate, string? plate)
    {
        if (string.IsNullOrEmpty(fixedPlate))
        {
            return plate;
        }

        string? normalized = CylinderTracking.NormalizePlate(plate);
        return normalized is null || normalized == fixedPlate
            ? fixedPlate
            : throw new ArgumentException($"Sabit aracınız {fixedPlate}; {normalized} aracıyla işlem yapamazsınız.");
    }

    internal static ActiveTripSummary? OpenTrip(IEnumerable<ActiveTripSummary> trips, string fixedPlate) =>
        trips.FirstOrDefault(t => t.Vehicles.Any(v => v.OnTrip && string.Equals(v.Plate, fixedPlate, StringComparison.OrdinalIgnoreCase)));
}

internal static class CylinderTripEndpoints
{
    // Whoever scans on a trip may explain why a cylinder was not scanned.
    internal static readonly string[] ReasonWriters = [Perm.TripDeliver, Perm.TripReturn, Perm.TripClose];

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/sefer/aktif", async () => Results.Ok(await new CylinderTripService().ActiveTripsAsync())).Requires(Perm.TripView);

        app.MapPost("/api/sefer/baslat", async (TripStartRequest request, HttpContext context) =>
        {
            try
            {
                (CylinderTrip trip, IReadOnlyList<string?>? plates) = await StartAsync(
                    User(context), request, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                string? plate = plates?.Count == 1 ? plates[0] : null;
                return Results.Ok(new { trip.Id, trip.Title, trip.StartedAt, plate, message = $"{trip.Title} başlatıldı." + (plate is null ? string.Empty : $" Araç: {plate}.") });
            }
            catch (FixedVehicleBusyException busy)
            {
                return Results.BadRequest(new { error = busy.Message, tripId = busy.TripId });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripStart);

        app.MapPost("/api/sefer/arac", async (TripVehicleRequest request, HttpContext context) =>
        {
            try
            {
                string? plate = DriverVehicle.PlateFor(User(context).FixedPlate, request.Plate);
                await new CylinderTripService().AddVehicleAsync(request.TripId, plate);
                await ApplyPlansAsync(request.TripId, plate, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(new { message = $"{plate} sefere eklendi." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripStart);

        app.MapPost("/api/sefer/yukle", async (TripLoadRequest request, HttpContext context) =>
        {
            try
            {
                TripLoadResult result = await new CylinderTripService().LoadCylindersAsync(
                    request.TripId, DriverVehicle.PlateFor(User(context).FixedPlate, request.Plate), request.Codes ?? [], "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
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
        }).Requires(Perm.TripLoad);

        app.MapPost("/api/sefer/teslim", async (TripDeliverRequest request, HttpContext context) =>
        {
            try
            {
                CylinderTripService service = new();
                List<CylinderBulkResult> results = await service.DeliverCylindersAsync(
                    request.TripId, request.Codes ?? [], request.CustomerId, request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                int ok = results.Count(r => r.Ok);
                string failures = string.Join("\n", results.Where(r => !r.Ok).Select(r => $"{r.Code}: {r.Message}"));
                string message = $"{ok}/{results.Count} tüp teslim kaydedildi." + (failures.Length > 0 ? "\n" + failures : string.Empty);
                if (ok > 0)
                {
                    message += await CompleteRequestsAsync(request.CustomerId!.Value, CustomerRequests.Order, request.TripId);
                }

                if (!Features.IsOn(Features.CustomerExchange))
                {
                    return Results.Ok(new { message });
                }

                CustomerTripBalance balance = await service.CustomerBalanceAsync(request.TripId, request.CustomerId!.Value);
                return Results.Ok(new { message = message + "\n" + balance.Text, balance });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripDeliver);

        // Empties collected at the customer: onto the vehicle and the trip, then the customer's exchange balance.
        app.MapPost("/api/sefer/bos-alim", async (TripPickupRequest request, HttpContext context) =>
        {
            try
            {
                TripPickupResult result = await new CylinderTripService().PickupCylindersAsync(
                    request.TripId, DriverVehicle.PlateFor(User(context).FixedPlate, request.Plate), request.CustomerId, request.Codes ?? [],
                    request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                int ok = result.Results.Count(r => r.Ok);
                string details = string.Join("\n", result.Results.Where(r => !r.Ok || r.Message.Contains('⚠')).Select(r => r.Ok ? r.Message : $"{r.Code}: {r.Message}"));
                string completed = ok > 0 ? await CompleteRequestsAsync(request.CustomerId!.Value, CustomerRequests.Pickup, request.TripId) : string.Empty;
                return Results.Ok(new
                {
                    message = $"{ok}/{result.Results.Count} boş tüp araca alındı." + (details.Length > 0 ? "\n" + details : string.Empty) + completed + "\n" + result.Balance.Text,
                    balance = result.Balance
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripPickup);

        app.MapPost("/api/sefer/donus", async (TripReturnRequest request, HttpContext context) =>
        {
            try
            {
                TripReconcileResult reconcile = await new CylinderTripService().ReturnCylindersAsync(
                    request.TripId, DriverVehicle.PlateFor(User(context).FixedPlate, request.Plate), request.Codes ?? [], request.Note, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
                return Results.Ok(TripReconcileDto(reconcile));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripReturn);

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
        }).Requires(Perm.TripView);

        app.MapPost("/api/sefer/kapat", async (TripCloseRequest request, HttpContext context) =>
        {
            try
            {
                TripCloseResult result = await new CylinderTripService().CloseTripAsync(request.TripId, "Mobil (" + MobileEndpoints.DeviceName(context) + ")");
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
        }).Requires(Perm.TripClose);

        // Route (visiting order) of a trip vehicle. A driver with a fixed vehicle sees and edits only that vehicle's stops.
        app.MapGet("/api/sefer/{tripId:int}/rota", async (int tripId, string? plaka, HttpContext context) =>
        {
            try
            {
                WebPanelUser user = User(context);
                string? plate = string.IsNullOrEmpty(user.FixedPlate) ? plaka : DriverVehicle.PlateFor(user.FixedPlate, plaka);
                return Results.Ok(await TripRoutes.ListAsync(tripId, plate, Perm.Effective(user).Contains(Perm.CustomerView), Features.IsOn(Features.TripPlan)));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripRoute);

        // Saved by a driver standing at the customer; the phone sends its own GPS fix.
        app.MapPost("/api/musteri/{customerId:int}/konum", async (int customerId, CustomerLocationRequest request, HttpContext context) =>
        {
            if (request.Accuracy is > 100)
            {
                return Results.BadRequest(new { error = $"Konum hassasiyeti düşük (±{request.Accuracy:0} m). Açık alanda birkaç saniye bekleyip yeniden deneyin." });
            }

            try
            {
                string by = "Mobil (" + MobileEndpoints.DeviceName(context) + ")";
                CustomerLocationView saved = await TripPlans.SetLocationAsync(customerId, request.Latitude, request.Longitude, by, "telefon");
                AppLog.Write($"Müşteri #{customerId} konumu telefondan kaydedildi — {by}.");
                return Results.Ok(new { message = "Müşterinin konumu kaydedildi.", saved.Latitude, saved.Longitude, saved.MapUrl });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.CustomerLocation);

        app.MapGet("/api/sefer/{tripId:int}/rota/oneriler", async (int tripId, string? plaka) =>
            Results.Ok(await TripRoutes.SuggestionsAsync(tripId, plaka, Features.IsOn(Features.CustomerPortal)))).Requires(Perm.TripRoute);

        app.MapPost("/api/sefer/rota/ekle", async (TripStopAddRequest request, HttpContext context) =>
            await RouteAsync(async () => await TripRoutes.AddAsync(request.TripId, DriverVehicle.PlateFor(User(context).FixedPlate, request.Plate),
                request.CustomerId, "Mobil (" + MobileEndpoints.DeviceName(context) + ")"))).Requires(RouteEditors);

        app.MapPost("/api/sefer/rota/sira", async (TripStopMoveRequest request, HttpContext context) =>
            await RouteAsync(async () =>
            {
                await TripRoutes.MoveAsync(request.TripId, request.StopId, request.Direction, OwnPlate(context));
                return "Sıra güncellendi.";
            })).Requires(RouteEditors);

        app.MapPost("/api/sefer/rota/sil", async (TripStopRequest request, HttpContext context) =>
            await RouteAsync(async () =>
            {
                await TripRoutes.RemoveAsync(request.TripId, request.StopId, OwnPlate(context));
                return "Durak rotadan çıkarıldı.";
            })).Requires(RouteEditors);

        app.MapPost("/api/sefer/rota/tamam", async (TripStopDoneRequest request, HttpContext context) =>
            await RouteAsync(async () =>
            {
                await TripRoutes.SetDoneAsync(request.TripId, request.StopId, request.Done, request.Note, OwnPlate(context));
                return request.Done ? "Durak tamamlandı olarak işaretlendi." : "Durak yeniden açıldı.";
            })).Requires(RouteEditors);

        app.MapGet("/api/sefer/gerekceler", () => Results.Ok(ReasonCatalog())).Requires(Perm.TripView);

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
                Checked = c.CheckedAt is not null
            }));
        }).Requires(Perm.TripView);

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
        }).Requires(ReasonWriters);
    }

    private static WebPanelUser User(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;

    internal static readonly string[] RouteEditors = [Perm.TripRoute];

    // With the customer portal on, a delivery closes the customer's open orders and a pickup the open pickup requests.
    private static async Task<string> CompleteRequestsAsync(int customerId, string kind, int tripId)
    {
        if (!Features.IsOn(Features.CustomerPortal))
        {
            return string.Empty;
        }

        int closed = await CustomerRequests.CompleteForCustomerAsync(customerId, kind, "Sefer #" + tripId, $"Seferde {(kind == CustomerRequests.Pickup ? "boş alım" : "teslim")} okutuldu (sefer #{tripId}).");
        return closed > 0 ? $"\nMüşterinin {closed} açık talebi ({CustomerRequests.KindLabel(kind).ToLower(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"))}) tamamlandı." : string.Empty;
    }

    private static string? OwnPlate(HttpContext context) => User(context).FixedPlate is { Length: > 0 } plate ? plate : null;

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

    // Shared by the phone and the panel. A user with a fixed vehicle always starts with that vehicle only, and not while
    // it is still out on an open trip (the phone then continues that trip).
    internal static async Task<(CylinderTrip Trip, IReadOnlyList<string?>? Plates)> StartAsync(WebPanelUser user, TripStartRequest request, string createdBy)
    {
        CylinderTripService service = new();
        IReadOnlyList<string?>? plates = DriverVehicle.StartPlates(user.FixedPlate, request.Plates);
        if (user.FixedPlate is string fixedPlate && DriverVehicle.OpenTrip(await service.ActiveTripsAsync(), fixedPlate) is ActiveTripSummary open)
        {
            throw new FixedVehicleBusyException(open.Id, $"Aracınız {fixedPlate} zaten açık seferde ({open.Title}). Yeni sefer açmadan bu sefere devam edin.");
        }

        CylinderTrip trip = await service.StartTripAsync(plates, request.Note, createdBy);
        await ApplyPlansAsync(trip.Id, null, createdBy);
        return (trip, plates);
    }

    // Today's trip plans of the trip's vehicles go onto the route; a failure here never undoes the trip start.
    internal static async Task ApplyPlansAsync(int tripId, string? plate, string by)
    {
        if (!Features.IsOn(Features.TripPlan))
        {
            return;
        }

        try
        {
            foreach (string message in await TripPlans.ApplyForTripAsync(tripId, plate, by))
            {
                AppLog.Write($"Sefer planı sefer #{tripId} rotasına işlendi — {message} ({by}).");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            AppLog.Write($"Sefer planı sefer #{tripId} rotasına işlenemedi: {exception.Message}");
        }
    }

    internal static object ReasonCatalog() =>
        TripMissingReasons.All.Select(r => new { key = r.Key, label = r.Label, noteRequired = r.NoteRequired, asksCustomer = r.AsksCustomer });

    internal static string ReasonMessage(TripReasonResult result, string done) =>
        $"{result.Updated} {done}" + (result.Messages.Count > 0 ? "\n" + string.Join("\n", result.Messages) : string.Empty);

    internal static IResult ReasonsRequired(TripReasonsRequiredException exception) =>
        Results.BadRequest(new { error = exception.Message, reasonsRequired = true, missing = exception.Serials });

    // The unscanned cylinders of a closed trip go to the managers (never to customers).
    internal static void NotifyManagers(TripCloseResult result)
    {
        if (result.ManagerNotice is string text)
        {
            ManagerNotices.Send($"sefer-kayip-{result.TripId}", text);
        }
    }

    private static object TripReconcileDto(TripReconcileResult reconcile) => new
    {
        message = reconcile.AlertText,
        hasAlerts = reconcile.HasAlerts,
        notDeliveredNotReturned = reconcile.NotDeliveredNotReturned,
        shortagesByGas = reconcile.ShortagesByGas.Select(x => new { gasType = x.GasType, count = x.Count }),
        unexpectedReturnCodes = reconcile.UnexpectedReturnCodes,
        loadedButNotAccountedFor = reconcile.LoadedButNotAccountedFor,
        unexplainedMissing = reconcile.UnexplainedMissing
    };
}
