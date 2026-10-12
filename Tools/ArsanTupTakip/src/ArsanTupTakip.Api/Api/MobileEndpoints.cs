using ArsanTupTakip.Models;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Api;

internal sealed record PairRequest(string Code, string? DeviceName);
internal sealed record MoveRequest(string Code, CylinderStatus Status, int? CustomerId, string? Note, string? Plate = null, bool? IsFull = null, bool ConfirmCustomerChange = false);
internal sealed record BulkMoveRequest(List<string?>? Codes, CylinderStatus Status, int? CustomerId, string? Note, string? Plate = null, bool? IsFull = null, bool ConfirmCustomerChange = false);
internal sealed record AuditRequest(int CustomerId, List<string?>? Codes, string? Note);
internal sealed record NewCylinderRequest(string? SerialNumber, string? Barcode, string? GasType, decimal Capacity, string? CapacityUnit, bool? IsFull, DateTime? TestDueDate, int? CustomerId = null);
internal sealed record CustomerCreateRequest(
    string? Name, string? ContactName, string? Phone, string? Email, string? City, string? District, string? Address,
    string? TaxOffice, string? TaxNumber, bool AllowDuplicate = false);

internal static class MobileEndpoints
{
    public static void Map(WebApplication app)
    {
        app.MapGet("/api/merhaba", () =>
        {
            MobileUpdateInfo update = AppUpdates.Get();
            return Results.Ok(new
            {
                app = "ArsanTupTakip.Api",
                version = update.ApiVersion,
                mobileVersion = update.PublishedMobileVersion,
                mobileBuild = update.PublishedMobileBuild,
                updateAvailable = false
            });
        });

        app.MapGet("/api/guncelleme", () =>
        {
            MobileUpdateInfo update = AppUpdates.Get();
            return Results.Ok(new
            {
                update.ApiVersion,
                update.PublishedMobileVersion,
                update.PublishedMobileBuild,
                update.ApkReady,
                update.ApkSizeBytes,
                downloadUrl = update.DownloadPath,
                note = "APK bu sunucunun /indir adresinden indirilir; kurulumdan sonra eşleşme anahtarı korunur."
            });
        });

        app.MapGet("/api/yapilandirma", () =>
        {
            string? url = AppUpdates.PublicUrl();
            return Results.Ok(new
            {
                serverUrl = url,
                stable = url is not null,
                autoManaged = url is not null,
                message = url is not null
                    ? "Sabit sunucu adresi kullanılıyor; telefonda elle değiştirmeniz gerekmez."
                    : "Sunucu adresi yapılandırılmamış."
            });
        });

        app.MapGet("/indir", () => File.Exists(AppUpdates.ApkFile)
            ? Results.File(AppUpdates.ApkFile, "application/vnd.android.package-archive", AppUpdates.ApkFileName, enableRangeProcessing: true)
            : Results.NotFound("APK henüz yüklenmedi."));

        app.MapPost("/api/pair/request", () => Results.Ok(new { message = PairingService.RequestMessage }));

        app.MapPost("/api/pair", (PairRequest request, PairingService pairing) =>
            pairing.TryPair(request.Code ?? string.Empty, request.DeviceName ?? string.Empty) is { } token
                ? Results.Ok(new { token })
                : Results.Json(new { error = "Kod hatalı veya süresi dolmuş. Yöneticinizden yeni kod isteyin." }, statusCode: StatusCodes.Status400BadRequest));

        app.MapGet("/api/ozet", () => Text(TupReports.SummaryAsync));

        app.MapGet("/api/musteriler", async (string? q) => Results.Ok(await CustomerChoices.SearchAsync(q)));

        app.MapPost("/api/musteri", (CustomerCreateRequest request, HttpContext context) =>
            CustomerChoices.CreateAsync(request, "mobil: " + DeviceName(context)));

        app.MapGet("/api/tup", async (string kod) =>
        {
            Cylinder? cylinder = await new CrudService().FindCylinderByCodeAsync(kod ?? string.Empty);
            return cylinder is null
                ? Results.NotFound(new { error = $"'{kod}' barkodlu veya seri numaralı tüp bulunamadı." })
                : Results.Ok(new
                {
                    cylinder.Id,
                    cylinder.Barcode,
                    cylinder.SerialNumber,
                    Status = cylinder.Status,
                    StatusName = CylinderTracking.StatusName((int)cylinder.Status),
                    Customer = cylinder.CurrentCustomer?.CompanyName,
                    Detail = await TupReports.FindCylinderAsync(kod!)
                });
        });

        app.MapPost("/api/tup/hareket", async (MoveRequest request, HttpContext context) =>
        {
            CrudService crud = new();
            Cylinder? cylinder = await crud.FindCylinderByCodeAsync(request.Code ?? string.Empty);
            if (cylinder is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            string? warning;
            try
            {
                string note = string.IsNullOrWhiteSpace(request.Note) ? $"Mobil ({DeviceName(context)})" : request.Note.Trim();
                warning = await crud.MoveCylinderAsync(cylinder.Id, request.Status, request.CustomerId, note, "Mobil hareket", request.Plate, request.IsFull, request.ConfirmCustomerChange);
            }
            catch (CylinderConflictException conflict)
            {
                return ConflictResult(conflict);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            return Results.Ok(new
            {
                message = $"{cylinder.SerialNumber}: {CylinderTracking.StatusName((int)cylinder.Status)} → {CylinderTracking.StatusName((int)request.Status)} kaydedildi."
                    + (warning is null ? string.Empty : "\n" + warning)
            });
        });

        app.MapPost("/api/tup/toplu-hareket", async (BulkMoveRequest request, HttpContext context) =>
        {
            try
            {
                string note = string.IsNullOrWhiteSpace(request.Note) ? $"Mobil toplu ({DeviceName(context)})" : request.Note.Trim();
                List<CylinderBulkResult> results = await new CrudService().MoveCylindersAsync(
                    request.Codes ?? [], request.Status, request.CustomerId, note, "Mobil toplu hareket", request.Plate, request.IsFull, request.ConfirmCustomerChange);
                int ok = results.Count(r => r.Ok);
                return Results.Ok(new
                {
                    message = $"{ok}/{results.Count} tüp {CylinderTracking.StatusName((int)request.Status)} olarak kaydedildi.",
                    ok,
                    failed = results.Count - ok,
                    results
                });
            }
            catch (CylinderConflictException conflict)
            {
                return ConflictResult(conflict);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapPost("/api/tup/yeni", async (NewCylinderRequest request, HttpContext context) =>
        {
            try
            {
                Cylinder cylinder = await new CrudService().RegisterCylinderAsync(
                    request.SerialNumber ?? string.Empty, request.Barcode, request.GasType ?? string.Empty, request.Capacity,
                    request.CapacityUnit, request.IsFull, request.TestDueDate, $"Mobil kayıt ({DeviceName(context)})", request.CustomerId);
                return Results.Ok(new
                {
                    message = $"{cylinder.SerialNumber} ({cylinder.GasType}, {cylinder.Capacity:0.##} {cylinder.CapacityUnit}) {await CustomerChoices.PlaceTextAsync(cylinder)} olarak envantere eklendi.",
                    cylinder.Barcode,
                    cylinder.SerialNumber
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (Microsoft.EntityFrameworkCore.DbUpdateException)
            {
                return Results.BadRequest(new { error = "Bu seri numarası veya barkod zaten kayıtlı." });
            }
        });

        app.MapGet("/api/tup/gaz-turleri", async () => Results.Ok(await new CrudService().KnownGasTypesAsync()));

        app.MapGet("/api/tup/durum", async () => Results.Ok(await new CrudService().CylinderOverviewAsync()));

        app.MapGet("/api/tup/liste", async (int? durum, string? plaka, bool? uzun) =>
        {
            CylinderStatus? status = durum is int value && Enum.IsDefined((CylinderStatus)value) ? (CylinderStatus)value : null;
            return Results.Ok(await new CrudService().CylinderListAsync(status, plaka, uzun == true));
        });

        app.MapGet("/api/araclar", async () => Results.Ok(await new CrudService().KnownVehiclePlatesAsync()));

        app.MapPost("/api/tup/sayim", async (AuditRequest request, HttpContext context) =>
        {
            try
            {
                CylinderAuditReport report = await new CrudService().AuditCustomerCylindersAsync(
                    request.CustomerId, request.Codes ?? [], request.Note, "Mobil (" + DeviceName(context) + ")");
                return Results.Ok(new { message = report.Text, report.Expected, report.Found, report.Missing, report.Unexpected, report.Unknown });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/tup/uzun-sure", (int? gun) => Text(() => TupReports.LongAtCustomerAsync(gun ?? CylinderTracking.LongAtCustomerDays)));
        app.MapGet("/api/tahmin", () => Text(TupReports.ReorderForecastsAsync));
        app.MapGet("/api/test-tarihleri", (int? gun) => Text(() => TupReports.TestDueAsync(gun ?? 30)));
        app.MapGet("/api/hareketler", (int? gun) => Text(() => TupReports.RecentMovementsAsync(gun ?? 7)));
        app.MapGet("/api/musterideki-tupler", (string? musteri) => Text(() => TupReports.CylindersAtCustomersAsync(musteri)));
    }

    // 409 tells the phone that nothing was saved and the same request may be repeated with ConfirmCustomerChange.
    internal static IResult ConflictResult(CylinderConflictException conflict) => Results.Json(new
    {
        error = conflict.Message,
        conflicts = conflict.Conflicts.Select(c => new { c.Code, c.SerialNumber, c.CurrentCustomer, c.TargetCustomer, c.Days, c.Text })
    }, statusCode: StatusCodes.Status409Conflict);

    private static async Task<IResult> Text(Func<Task<string>> build)
    {
        try
        {
            return Results.Ok(new { text = await build() });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static string DeviceName(HttpContext context) =>
        context.Items["device"] is PairedDevice device ? device.Name : "bilinmeyen cihaz";
}
