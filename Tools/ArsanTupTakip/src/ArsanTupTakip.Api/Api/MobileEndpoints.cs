using System.Globalization;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record MobileLoginRequest(string? Kullanici, string? Sifre, string? CihazAdi);
internal sealed record MobilePasswordRequest(string? Mevcut, string? Yeni, string? Tekrar);
internal sealed record MoveRequest(string Code, CylinderStatus Status, int? CustomerId, string? Note, string? Plate = null, bool? IsFull = null, bool ConfirmCustomerChange = false, uint? Version = null);
internal sealed record BulkMoveRequest(List<string?>? Codes, CylinderStatus Status, int? CustomerId, string? Note, string? Plate = null, bool? IsFull = null, bool ConfirmCustomerChange = false);
internal sealed record AuditRequest(int CustomerId, List<string?>? Codes, string? Note);
internal sealed record NewCylinderRequest(string? SerialNumber, string? Barcode, string? GasType, decimal Capacity, string? CapacityUnit, bool? IsFull, DateTime? TestDueDate, int? CustomerId = null);
internal sealed record CustomerCreateRequest(
    string? Name, string? ContactName, string? Phone, string? Email, string? City, string? District, string? Address,
    string? TaxOffice, string? TaxNumber, bool AllowDuplicate = false);

internal static class MobileEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

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
                note = "APK bu sunucunun /indir adresinden indirilir; kurulumdan sonra oturum korunur (24 saat)."
            });
        });

        app.MapGet("/api/guncelleme/el-terminali", () =>
        {
            MobileUpdateInfo update = AppUpdates.GetHandheld();
            return Results.Ok(new
            {
                update.ApiVersion,
                update.PublishedMobileVersion,
                update.PublishedMobileBuild,
                update.ApkReady,
                update.ApkSizeBytes,
                downloadUrl = update.DownloadPath
            });
        });

        app.MapPost("/api/giris", async (MobileLoginRequest request, HttpContext context, WebPanelAuth auth, DeviceStore devices) =>
        {
            (string? token, MobileSession? session, WebPanelUser? user, string? error) =
                await auth.MobileLoginAsync(context, devices, request.Kullanici, request.Sifre, request.CihazAdi);
            return token is null || session is null || user is null
                ? Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(new { token, expiresUtc = session.ExpiresUtc, account = Account(user, session) });
        });

        app.MapGet("/api/ben", (HttpContext context) =>
            Results.Ok(Account((WebPanelUser)context.Items["panelAccount"]!, (MobileSession)context.Items["device"]!)))
            .SignedIn(beforePasswordChange: true);

        app.MapPost("/api/sifre", async (MobilePasswordRequest request, HttpContext context, WebPanelAuth auth, DeviceStore devices) =>
        {
            WebPanelUser user = (WebPanelUser)context.Items["panelAccount"]!;
            MobileSession session = (MobileSession)context.Items["device"]!;
            (string? error, int stamp) = await auth.ChangePasswordCoreAsync(user, WebPanelAuth.ClientIp(context), request.Mevcut, request.Yeni, request.Tekrar);
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            devices.Restamp(session.Id, stamp);
            return Results.Ok(new { message = "Şifreniz değiştirildi. Diğer cihaz ve tarayıcılardaki oturumlarınız kapatıldı." });
        }).SignedIn(beforePasswordChange: true);

        app.MapPost("/api/cikis", (HttpContext context, DeviceStore devices) =>
        {
            MobileSession session = (MobileSession)context.Items["device"]!;
            devices.Remove(session.Id);
            WebPanelAudit.Write(session.UserName, "Çıkış (mobil)", session.Name, ip: WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = "Çıkış yapıldı." });
        }).SignedIn(beforePasswordChange: true);

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
        }).SignedIn();

        app.MapGet("/indir", () => File.Exists(AppUpdates.ApkFile)
            ? Results.File(AppUpdates.ApkFile, "application/vnd.android.package-archive", AppUpdates.ApkFileName, enableRangeProcessing: true)
            : Results.NotFound("APK henüz yüklenmedi."));

        // Android 4.4 handhelds: the device browser cannot open the HTTPS site, so the first install uses http:// here.
        app.MapGet("/indir/el-terminali", () => File.Exists(AppUpdates.HandheldApkFile)
            ? Results.File(AppUpdates.HandheldApkFile, "application/vnd.android.package-archive", AppUpdates.HandheldApkFileName, enableRangeProcessing: true)
            : Results.NotFound("El terminali APK'sı henüz yüklenmedi."));

        app.MapGet("/api/ozet", () => Text(TupReports.SummaryAsync)).Requires(Perm.Summary);

        app.MapGet("/api/musteriler", async (string? q, HttpContext context) =>
            Results.Ok(await CustomerChoices.SearchAsync(q, Perm.Has((WebPanelUser)context.Items["panelAccount"]!, Perm.CustomerView))))
            .Requires(Perm.CustomerPicker);

        app.MapPost("/api/musteri", (CustomerCreateRequest request, HttpContext context) =>
            CustomerChoices.CreateAsync(request, "mobil: " + DeviceName(context))).Requires(Perm.CustomerAdd);

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
                    StatusName = CylinderTracking.StatusName((int)cylinder.Status, cylinder.VehiclePlate),
                    Customer = cylinder.CurrentCustomer?.CompanyName,
                    Detail = await TupReports.FindCylinderAsync(kod!),
                    cylinder.Version
                });
        }).Requires([Perm.CylinderView, .. Perm.AnyMove]);

        // Read-only status for the "Tüp Bilgisi" screen; customer phones are never included (they need CustomerView).
        app.MapGet("/api/tup/bilgi", async (string? kod) =>
        {
            Cylinder? c = await new CrudService().FindCylinderByCodeAsync(kod ?? string.Empty);
            if (c is null)
            {
                return Results.NotFound(new { error = $"'{(kod ?? string.Empty).Trim()}' barkodlu veya seri numaralı tüp bulunamadı." });
            }

            await using TupTakipDbContext db = new();
            CylinderMovement? last = await db.CylinderMovements.AsNoTracking()
                .Where(m => m.CylinderId == c.Id).OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id).FirstOrDefaultAsync();
            string? trip = c.Status == CylinderStatus.Vehicle
                ? await db.CylinderTripCylinders.AsNoTracking()
                    .Where(t => t.CylinderId == c.Id && t.DeliveredAt == null && t.ReturnedAt == null && t.Trip.Status == CylinderTripStatus.Active)
                    .OrderByDescending(t => t.LoadedAt).Select(t => t.Trip.Title).FirstOrDefaultAsync()
                : null;
            string? customer = c.Status == CylinderStatus.Customer ? c.CurrentCustomer?.CompanyName : null;
            string? plate = c.Status == CylinderStatus.Vehicle ? c.VehiclePlate : null;
            DateTime today = DateTime.Today;
            return Results.Ok(new
            {
                c.SerialNumber,
                c.Barcode,
                c.GasType,
                Capacity = $"{c.Capacity.ToString("0.##", Turkish)} {c.CapacityUnit}",
                Status = (int)c.Status,
                StatusName = CylinderTracking.StatusName((int)c.Status, plate),
                IsFull = c.Status == CylinderStatus.Customer ? null : c.IsFull,
                Fullness = c.Status == CylinderStatus.Customer ? "-" : c.IsFull is null ? "Bilinmiyor" : CylinderTracking.FullnessName(c.IsFull),
                Location = CylinderTracking.LocationText((int)c.Status, customer, plate, trip),
                Customer = customer,
                Plate = plate,
                Trip = trip,
                DaysThere = CylinderTracking.DaysSince(c.LastMovementAt, today),
                LastMovementDate = (last?.MovementDate ?? c.LastMovementAt)?.ToString("dd.MM.yyyy HH:mm", Turkish),
                LastMovementType = last is null ? null : $"{last.MovementType}: {CylinderTracking.MovementPlaces((int)last.FromStatus, (int)last.ToStatus, last.VehiclePlate)}",
                TestDue = c.TestDueDate?.ToString("dd.MM.yyyy", Turkish),
                TestOverdue = c.TestDueDate != null && c.TestDueDate < today,
                c.IsActive,
                c.ManufacturerSerial,
                c.ProductName,
                c.NeedsReview
            });
        }).Requires(Perm.CylinderView);

        app.MapPost("/api/tup/hareket", async (MoveRequest request, HttpContext context) =>
        {
            if (MoveProblem(context, request.Status, request.IsFull, request.ConfirmCustomerChange) is IResult denied)
            {
                return denied;
            }

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
                warning = await crud.MoveCylinderAsync(cylinder.Id, request.Status, request.CustomerId, note, "Mobil hareket", request.Plate, request.IsFull, request.ConfirmCustomerChange, request.Version);
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
        }).Requires(Perm.AnyMove);

        app.MapPost("/api/tup/toplu-hareket", async (BulkMoveRequest request, HttpContext context) =>
        {
            if (MoveProblem(context, request.Status, request.IsFull, request.ConfirmCustomerChange) is IResult denied)
            {
                return denied;
            }

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
        }).Requires(Perm.MoveBulk);

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
        }).Requires(Perm.CylinderRegister);

        app.MapGet("/api/tup/gaz-turleri", async () => Results.Ok(await new CrudService().KnownGasTypesAsync()))
            .Requires(Perm.CylinderRegister, Perm.CylinderEdit, Perm.CylinderView);

        app.MapGet("/api/tup/durum", async () => Results.Ok(await new CrudService().CylinderOverviewAsync())).Requires(Perm.Summary);

        app.MapGet("/api/tup/liste", async (int? durum, string? plaka, bool? uzun) =>
        {
            CylinderStatus? status = durum is int value && Enum.IsDefined((CylinderStatus)value) ? (CylinderStatus)value : null;
            return Results.Ok(await new CrudService().CylinderListAsync(status, plaka, uzun == true));
        }).Requires(Perm.CylinderView, Perm.Summary);

        app.MapGet("/api/araclar", async () => Results.Ok(await new CrudService().KnownVehiclePlatesAsync()))
            .Requires(Perm.VehicleView, Perm.MoveVehicle, Perm.TripStart);

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
        }).Requires(Perm.CylinderAudit);

        app.MapGet("/api/tup/uzun-sure", (int? gun) => Text(() => TupReports.LongAtCustomerAsync(gun ?? CylinderTracking.LongAtCustomerDays))).Requires(Perm.Reports);
        app.MapGet("/api/tahmin", () => Text(TupReports.ReorderForecastsAsync)).Requires(Perm.Reports);
        app.MapGet("/api/test-tarihleri", (int? gun) => Text(() => TupReports.TestDueAsync(gun ?? 30))).Requires(Perm.Reports);
        app.MapGet("/api/hareketler", (int? gun) => Text(() => TupReports.RecentMovementsAsync(gun ?? 7))).Requires(Perm.MovementHistory);
        app.MapGet("/api/musteri/{id:int}/stok", async (int id, HttpContext context) =>
        {
            WebPanelUser user = (WebPanelUser)context.Items["panelAccount"]!;
            if (user.Role == PanelRoles.Driver)
            {
                return Results.Json(new { error = "Müşteri stok bilgisi depo (şoför) hesaplarında kapalı." }, statusCode: StatusCodes.Status403Forbidden);
            }

            return await WebPanelEndpoints.CustomerStockAsync(id, Perm.Has(user, Perm.CustomerView));
        }).Requires(Perm.CylinderView);
        app.MapGet("/api/musterideki-tupler", (string? musteri) => Text(() => TupReports.CylindersAtCustomersAsync(musteri))).Requires(Perm.Reports);
        StockCountEndpoints.MapMobile(app);
        MobileSimulationEndpoints.Map(app);
        FillEndpoints.Map(app);
    }

    internal static object Account(WebPanelUser user, MobileSession session) => new
    {
        user = user.UserName,
        displayName = user.DisplayName,
        role = user.Role,
        roleText = PanelRoles.Description(user.Role),
        permissions = Perm.Effective(user).Order(StringComparer.Ordinal).ToArray(),
        mustChangePassword = user.MustChangePassword,
        fixedPlate = user.FixedPlate,
        expiresUtc = session.ExpiresUtc,
        device = session.Name
    };

    // The target status, manual full/empty choice and the "save anyway" confirmation each need their own permission.
    internal static IResult? MoveProblem(HttpContext context, CylinderStatus target, bool? isFull, bool confirmCustomerChange)
    {
        HashSet<string> mine = Perm.Effective((WebPanelUser)context.Items["panelAccount"]!);
        if (!Perm.MoveByStatus.TryGetValue((int)target, out string? needed))
        {
            return Results.BadRequest(new { error = "Geçersiz tüp durumu." });
        }

        if (!mine.Contains(needed))
        {
            return PermissionEndpointExtensions.Denied(needed);
        }

        if (isFull is not null && !mine.Contains(Perm.MoveFullness))
        {
            return PermissionEndpointExtensions.Denied(Perm.MoveFullness);
        }

        return confirmCustomerChange && !mine.Contains(Perm.MoveOverride) ? PermissionEndpointExtensions.Denied(Perm.MoveOverride) : null;
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
        context.Items["device"] is MobileSession session ? $"{session.UserName}, {session.Name}" : "bilinmeyen cihaz";
}
