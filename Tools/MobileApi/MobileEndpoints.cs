using System.Globalization;
using ArsanGazERP.Data;
using ArsanGazERP.ErpMcpServer;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record MobileLoginRequest(string? Kullanici, string? Sifre, string? Cihaz);
internal sealed record MoveRequest(string Code, CylinderStatus Status, int? CustomerId, string? Note, string? Plate = null, bool? IsFull = null, bool ConfirmCustomerChange = false);
internal sealed record BulkMoveRequest(List<string?>? Codes, CylinderStatus Status, int? CustomerId, string? Note, string? Plate = null, bool? IsFull = null, bool ConfirmCustomerChange = false);
internal sealed record AuditRequest(int CustomerId, List<string?>? Codes, string? Note);
internal sealed record NewCylinderRequest(string? SerialNumber, string? Barcode, string? GasType, decimal Capacity, string? CapacityUnit, bool? IsFull, DateTime? TestDueDate, int? CustomerId = null);
internal sealed record CustomerCreateRequest(
    string? Name, string? ContactName, string? Phone, string? Email, string? City, string? District, string? Address,
    string? TaxOffice, string? TaxNumber, bool AllowDuplicate = false);
internal sealed record CrmNoteRequest(string Text);
internal sealed record CursorRequest(string? Message);
internal sealed record CrmFollowUpRequest(DateOnly Date, string? Note);
internal sealed record ChatRequest(string Message, List<ErpChatTurn>? History);

internal static class MobileEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(WebApplication app)
    {
        app.MapGet("/api/merhaba", () =>
        {
            MobileUpdateInfo update = MobileConnectionHub.GetUpdateInfo();
            return Results.Ok(new
            {
                app = "ArsanGazERP.MobileApi",
                version = update.ApiVersion,
                mobileVersion = update.PublishedMobileVersion,
                mobileBuild = update.PublishedMobileBuild,
                updateAvailable = false
            });
        });

        app.MapGet("/api/guncelleme", () =>
        {
            MobileUpdateInfo update = MobileConnectionHub.GetUpdateInfo();
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
            string? url = MobileConnectionHub.ReadPublicUrl();
            bool stable = MobilePublicUrl.IsStableConfigured;
            return Results.Ok(new
            {
                serverUrl = url,
                stable,
                autoManaged = stable || url is { Length: > 0 },
                message = stable
                    ? "Sabit sunucu adresi kullanılıyor; telefonda elle değiştirmeniz gerekmez."
                    : url is { Length: > 0 }
                        ? "Güncel sunucu adresi. Eşleşmiş uygulama bunu otomatik kaydeder."
                        : "Tünel adresi henüz yok; Telegram bildirimini bekleyin."
            });
        }).SignedIn();

        app.MapGet("/api/hub", () => Results.Ok(MobileConnectionHub.GetHubStatus())).SignedIn();

        app.MapGet("/indir", () => File.Exists(MobileApiPaths.ApkFile)
            ? Results.File(MobileApiPaths.ApkFile, "application/vnd.android.package-archive", "ArsanGazERP-Mobil.apk", enableRangeProcessing: true)
            : Results.NotFound("APK henüz yüklenmedi."));

        // 6-digit Telegram pairing gave a phone full access without a user; since 1.8.0 phones sign in with the panel
        // user name and password instead.
        app.MapPost("/api/pair/request", () => PairingRemoved());
        app.MapPost("/api/pair", () => PairingRemoved());

        app.MapPost("/api/giris", async (MobileLoginRequest request, HttpContext context, WebPanelAuth auth, DeviceStore devices) =>
        {
            (MobileLogin? login, string? error) = await auth.MobileLoginAsync(WebPanelAuth.ClientIp(context), request.Kullanici, request.Sifre, request.Cihaz, devices);
            return login is null
                ? Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized)
                : Results.Ok(new { token = login.Token, user = Me(login.User, login.Device) });
        });

        app.MapGet("/api/ben", (HttpContext context) => Results.Ok(Me(Account(context), (PairedDevice)context.Items["device"]!)))
            .SignedIn().WithMetadata(new PanelOpenBeforePasswordChange());

        app.MapPost("/api/sifre", async (PanelPasswordRequest request, HttpContext context, WebPanelAuth auth, DeviceStore devices) =>
        {
            PairedDevice device = (PairedDevice)context.Items["device"]!;
            WebPanelUser user = Account(context);
            (string? error, int stamp) = await auth.ChangeOwnPasswordAsync(user, WebPanelAuth.ClientIp(context), request.Mevcut, request.Yeni, request.YeniTekrar, "mobil");
            if (error is not null)
            {
                return Results.BadRequest(new { error });
            }

            devices.UpdateStamp(device.Id, stamp);
            devices.RemoveForUser(user.Id, exceptId: device.Id);
            return Results.Ok(new { message = "Şifre değiştirildi. Diğer telefon ve tarayıcılardaki oturumlarınız kapatıldı." });
        }).SignedIn().WithMetadata(new PanelOpenBeforePasswordChange());

        app.MapPost("/api/cikis", (HttpContext context, DeviceStore devices) =>
        {
            PairedDevice device = (PairedDevice)context.Items["device"]!;
            devices.Remove(device.Id);
            WebPanelAudit.Write(Account(context).UserName, "Mobil çıkış", device.Name, ip: WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = "Çıkış yapıldı." });
        }).SignedIn().WithMetadata(new PanelOpenBeforePasswordChange());

        app.MapGet("/api/ozet", () => Text(() => "ERP\n" + ErpTools.Summary() + "\nCRM\n" + ErpTools.CrmSummary())).Requires(PanelPermissions.Summary);

        app.MapGet("/api/musteriler", async (string? q) => Results.Ok(await CustomerChoices.SearchAsync(q))).Requires(PanelPermissions.CustomerView);

        app.MapPost("/api/musteri", (CustomerCreateRequest request, HttpContext context) =>
            CustomerChoices.CreateAsync(request, "mobil: " + DeviceName(context))).Requires(PanelPermissions.CustomerCreate);

        app.MapGet("/api/crm", async (string? q) =>
        {
            string term = (q ?? string.Empty).Trim();
            if (term.Length < 2)
            {
                return Results.Ok(Array.Empty<object>());
            }

            await using CrmDbContext db = new();
            List<CrmCompany> companies = await db.CrmCompanies.AsNoTracking().ToListAsync();
            return Results.Ok(companies
                .Where(c => Contains(c.CompanyName, term) || Contains(c.City, term) || Contains(c.Sector, term))
                .OrderByDescending(c => c.Score).ThenBy(c => c.CompanyName)
                .Take(40)
                .Select(CrmView));
        }).Requires(PanelPermissions.CrmView);

        app.MapGet("/api/takipler", async (int? gun) =>
        {
            int days = Math.Clamp(gun ?? 7, 0, 60);
            DateTime limit = DateTime.UtcNow.Date.AddDays(days + 1);
            await using CrmDbContext db = new();
            List<CrmCompany> companies = await db.CrmCompanies.AsNoTracking()
                .Where(c => c.FollowUpAtUtc != null && c.FollowUpAtUtc <= limit)
                .OrderBy(c => c.FollowUpAtUtc)
                .Take(100)
                .ToListAsync();
            return Results.Ok(companies.Select(CrmView));
        }).Requires(PanelPermissions.CrmView);

        app.MapPost("/api/crm/{id:int}/not", async (int id, CrmNoteRequest request, HttpContext context) =>
        {
            string text = (request.Text ?? string.Empty).Trim();
            if (text.Length is 0 or > 1500)
            {
                return Results.BadRequest(new { error = "Not 1-1500 karakter olmalıdır." });
            }

            await using CrmDbContext db = new();
            CrmCompany? company = await db.CrmCompanies.FindAsync(id);
            if (company is null)
            {
                return Results.NotFound(new { error = "Firma bulunamadı." });
            }

            db.CrmNotes.Add(new CrmNote { CrmCompanyId = id, NoteText = text });
            db.CrmActivities.Add(new CrmActivity
            {
                CrmCompanyId = id,
                ActivityType = "Mobil not",
                Description = $"Mobil uygulamadan ({DeviceName(context)}) not eklendi."
            });
            company.LastContactAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"Not {company.CompanyName} kartına kaydedildi." });
        }).Requires(PanelPermissions.CrmWrite);

        app.MapPost("/api/crm/{id:int}/takip", async (int id, CrmFollowUpRequest request, HttpContext context) =>
        {
            await using CrmDbContext db = new();
            CrmCompany? company = await db.CrmCompanies.FindAsync(id);
            if (company is null)
            {
                return Results.NotFound(new { error = "Firma bulunamadı." });
            }

            DateTime? previous = company.FollowUpAtUtc;
            company.FollowUpAtUtc = DateTime.SpecifyKind(request.Date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime();
            string note = string.IsNullOrWhiteSpace(request.Note) ? string.Empty : " Not: " + request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 500)];
            db.CrmActivities.Add(new CrmActivity
            {
                CrmCompanyId = id,
                ActivityType = "Takip planı",
                Description = $"{previous?.ToLocalTime().ToString("dd.MM.yyyy", Turkish) ?? "Önceki takip yok"} → {request.Date:dd.MM.yyyy}; kaynak: mobil ({DeviceName(context)}).{note}"
            });
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"{company.CompanyName} için takip {request.Date:dd.MM.yyyy} tarihine kaydedildi." });
        }).Requires(PanelPermissions.CrmWrite);

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
                    StatusName = ErpTools.StatusName((int)cylinder.Status),
                    Customer = cylinder.CurrentCustomer?.CompanyName,
                    Detail = ErpTools.FindCylinder(kod!)
                });
        }).Requires(PanelPermissions.CylinderView);

        // Read-only status card for the "Tüp Bilgisi" screen; the customer name is shown only with customer view permission.
        app.MapGet("/api/tup/bilgi", async (string? kod, HttpContext context) =>
        {
            string code = (kod ?? string.Empty).Trim();
            if (code.Length == 0)
            {
                return Results.BadRequest(new { error = "Barkod veya seri numarası girin." });
            }

            Cylinder? cylinder = await new CrudService().FindCylinderByCodeAsync(code);
            if (cylinder is null)
            {
                return Results.NotFound(new { error = $"'{code}' barkodlu veya seri numaralı tüp bulunamadı." });
            }

            await using ArsanGazDbContext db = new();
            CylinderMovement? last = await db.CylinderMovements.AsNoTracking()
                .Where(m => m.CylinderId == cylinder.Id)
                .OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id)
                .FirstOrDefaultAsync();
            CylinderTripCylinder? trip = cylinder.Status == CylinderStatus.Vehicle
                ? await db.CylinderTripCylinders.AsNoTracking().Include(t => t.Trip)
                    .Where(t => t.CylinderId == cylinder.Id && t.Trip.Status == CylinderTripStatus.Active && t.DeliveredAt == null && t.ReturnedAt == null)
                    .OrderByDescending(t => t.LoadedAt)
                    .FirstOrDefaultAsync()
                : null;

            bool showCustomer = PanelRoles.Has(Account(context), PanelPermissions.CustomerView);
            string? customer = showCustomer ? cylinder.CurrentCustomer?.CompanyName : null;
            // Customer-owned cylinder (müşteri malı): the owner is a customer name, so it needs the same permission.
            string? owner = showCustomer && cylinder.OwnerCustomerId is int ownerId
                ? await db.Customers.AsNoTracking().Where(x => x.Id == ownerId).Select(x => x.CompanyName).FirstOrDefaultAsync()
                : null;
            string location = cylinder.Status switch
            {
                CylinderStatus.Customer => customer is null
                    ? (showCustomer ? "Müşteride" : "Müşteride (müşteri adını görme yetkiniz yok)")
                    : "Müşteride: " + customer,
                CylinderStatus.Vehicle => "Araçta" + (string.IsNullOrWhiteSpace(cylinder.VehiclePlate) ? string.Empty : ": " + cylinder.VehiclePlate),
                _ => CylinderTracking.StatusName((int)cylinder.Status)
            };
            DateTime today = DateTime.Today;
            return Results.Ok(new
            {
                cylinder.SerialNumber,
                cylinder.Barcode,
                cylinder.GasType,
                Capacity = $"{cylinder.Capacity.ToString("0.##", Turkish)} {cylinder.CapacityUnit}",
                Status = (int)cylinder.Status,
                StatusName = CylinderTracking.StatusName((int)cylinder.Status),
                cylinder.IsFull,
                Fullness = CylinderTracking.FullnessName(cylinder.IsFull),
                Location = location,
                Customer = customer,
                Owner = owner,
                Plate = cylinder.VehiclePlate,
                Trip = trip is null ? null : $"{trip.Trip.Title} (yükleme {trip.LoadedAt.ToString("dd.MM.yyyy HH:mm", Turkish)})",
                DaysThere = CylinderTracking.DaysSince(cylinder.LastMovementAt, today),
                LastMovementDate = (last?.MovementDate ?? cylinder.LastMovementAt)?.ToString("dd.MM.yyyy HH:mm", Turkish),
                LastMovementType = last is null
                    ? null
                    : $"{last.MovementType} ({CylinderTracking.StatusName((int)last.FromStatus)} → {CylinderTracking.StatusName((int)last.ToStatus)})",
                TestDue = cylinder.TestDueDate?.ToString("dd.MM.yyyy", Turkish),
                TestOverdue = cylinder.TestDueDate is DateTime due && due.Date < today,
                TestDays = cylinder.TestDueDate is DateTime dueDate ? (dueDate.Date - today).Days : (int?)null,
                cylinder.IsActive
            });
        }).Requires(PanelPermissions.CylinderView);

        app.MapPost("/api/tup/hareket", async (MoveRequest request, HttpContext context) =>
        {
            CrudService crud = new();
            Cylinder? cylinder = await crud.FindCylinderByCodeAsync(request.Code ?? string.Empty);
            if (cylinder is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            string needed = PanelPermissions.ForMove((int)cylinder.Status, (int)request.Status);
            if (!PanelRoles.Has(Account(context), needed))
            {
                return Results.Json(new { error = DeviceAuthMiddleware.MissingPermission(needed), forbidden = true }, statusCode: StatusCodes.Status403Forbidden);
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
                message = $"{cylinder.SerialNumber}: {ErpTools.StatusName((int)cylinder.Status)} → {ErpTools.StatusName((int)request.Status)} kaydedildi."
                    + (warning is null ? string.Empty : "\n" + warning)
            });
        }).Requires(PanelPermissions.CylinderView).WithMetadata(new MovePermissionChecked());

        app.MapPost("/api/tup/toplu-hareket", async (BulkMoveRequest request, HttpContext context) =>
        {
            if (await DeviceAuthMiddleware.MoveProblemAsync(Account(context), request.Codes ?? [], request.Status) is { } denied)
            {
                return Results.Json(new { error = denied, forbidden = true }, statusCode: StatusCodes.Status403Forbidden);
            }

            try
            {
                string note = string.IsNullOrWhiteSpace(request.Note) ? $"Mobil toplu ({DeviceName(context)})" : request.Note.Trim();
                List<CylinderBulkResult> results = await new CrudService().MoveCylindersAsync(
                    request.Codes ?? [], request.Status, request.CustomerId, note, "Mobil toplu hareket", request.Plate, request.IsFull, request.ConfirmCustomerChange);
                int ok = results.Count(r => r.Ok);
                return Results.Ok(new
                {
                    message = $"{ok}/{results.Count} tüp {ErpTools.StatusName((int)request.Status)} olarak kaydedildi.",
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
        }).Requires(PanelPermissions.CylinderView).WithMetadata(new MovePermissionChecked());

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
        }).Requires(PanelPermissions.CylinderRegister);

        app.MapGet("/api/tup/gaz-turleri", async () => Results.Ok(await new CrudService().KnownGasTypesAsync())).SignedIn();

        app.MapGet("/api/tup/durum", async () => Results.Ok(await new CrudService().CylinderOverviewAsync())).Requires(PanelPermissions.CylinderView);

        app.MapGet("/api/tup/liste", async (int? durum, string? plaka, bool? uzun) =>
        {
            CylinderStatus? status = durum is int value && Enum.IsDefined((CylinderStatus)value) ? (CylinderStatus)value : null;
            return Results.Ok(await new CrudService().CylinderListAsync(status, plaka, uzun == true));
        }).Requires(PanelPermissions.CylinderView);

        app.MapGet("/api/araclar", async () => Results.Ok(await new CrudService().KnownVehiclePlatesAsync())).SignedIn();

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
        }).Requires(PanelPermissions.CylinderAudit);

        app.MapGet("/api/tup/uzun-sure", (int? gun) => Text(() => ErpTools.LongAtCustomer(gun ?? CylinderTracking.LongAtCustomerDays))).Requires(PanelPermissions.Reports);

        app.MapGet("/api/tahmin", () => Text(ErpTools.ReorderForecasts)).Requires(PanelPermissions.Reports);
        app.MapGet("/api/test-tarihleri", (int? gun) => Text(() => ErpTools.TestDue(gun ?? 30))).Requires(PanelPermissions.Reports);
        app.MapGet("/api/hareketler", (int? gun) => Text(() => ErpTools.RecentMovements(gun ?? 7))).Requires(PanelPermissions.MovementView);
        app.MapGet("/api/musterideki-tupler", (string? musteri) => Text(() => ErpTools.CylindersAtCustomers(musteri))).Requires(PanelPermissions.Reports);

        app.MapGet("/api/ajanlar", () => Results.Ok(AgentChatService.Profiles.Select(p => new { p.Id, p.Name, p.Role }))).Requires(PanelPermissions.Assistant);

        app.MapPost("/api/sohbet", async (ChatRequest request, MobileChatService chat, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Mesaj boş olamaz." });
            }

            try
            {
                IReadOnlyList<ErpAgentReply> replies = await chat.SendAsync(request.Message, request.History ?? [], context.RequestAborted);
                return Results.Ok(replies.Select(r => new { agent = r.Agent.Name, role = r.Agent.Role, text = r.Content, ok = r.Succeeded }));
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.Assistant);

        app.MapPost("/api/cursor", async (HttpContext context) =>
        {
            string device = DeviceName(context);
            try
            {
                if (context.Request.HasFormContentType)
                {
                    IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
                    string message = form["message"].ToString().Trim();
                    if (message.Length == 0)
                    {
                        message = form["text"].ToString().Trim();
                    }

                    string id = CursorRelay.CreatePendingId();
                    IReadOnlyList<SavedMobileAttachment> saved = form.Files.Count > 0
                        ? await MobileAttachmentStore.SaveAsync(id, form.Files, context.RequestAborted)
                        : Array.Empty<SavedMobileAttachment>();
                    string queued = CursorRelay.Enqueue(
                        message.Length > 0 ? message : (saved.Count > 0 ? "Mobil ekleri incele." : string.Empty),
                        device,
                        saved,
                        id);
                    return Results.Ok(new { id = queued, attachments = saved.Count });
                }

                CursorRequest? request = await context.Request.ReadFromJsonAsync<CursorRequest>(context.RequestAborted);
                return Results.Ok(new { id = CursorRelay.Enqueue(request?.Message ?? string.Empty, device) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.CursorTask);

        app.MapGet("/api/cursor/{id}", (string id) => CursorRelay.GetStatus(id) is { } status
            ? Results.Ok(status)
            : Results.NotFound(new { error = "İstek bulunamadı." })).Requires(PanelPermissions.CursorTask);
    }

    private static IResult PairingRemoved() => Results.Json(new
    {
        error = "Kodla eşleştirme kaldırıldı. Uygulamayı güncelleyin (1.8.0) ve web paneldeki kullanıcı adı ve şifrenizle giriş yapın.",
        loginRequired = true
    }, statusCode: StatusCodes.Status410Gone);

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;

    internal static object Me(WebPanelUser user, PairedDevice device) => new
    {
        user.Id,
        user.UserName,
        user.DisplayName,
        user.Role,
        RoleName = PanelRoles.Description(user.Role),
        Permissions = PanelRoles.Effective(user),
        user.MustChangePassword,
        ExpiresUtc = device.ExpiresUtc,
        Device = device.Name
    };

    private static object CrmView(CrmCompany c) => new
    {
        c.Id,
        Name = c.CompanyName,
        c.City,
        c.Sector,
        c.Phone,
        c.Status,
        c.Score,
        FollowUp = c.FollowUpAtUtc?.ToLocalTime().ToString("dd.MM.yyyy", Turkish),
        FollowUpDays = c.FollowUpAtUtc is DateTime due ? (due.ToLocalTime().Date - DateTime.Today).Days : (int?)null
    };

    // 409 tells the phone that nothing was saved and the same request may be repeated with ConfirmCustomerChange.
    private static IResult ConflictResult(CylinderConflictException conflict) => Results.Json(new
    {
        error = conflict.Message,
        conflicts = conflict.Conflicts.Select(c => new { c.Code, c.SerialNumber, c.CurrentCustomer, c.TargetCustomer, c.Days, c.Text })
    }, statusCode: StatusCodes.Status409Conflict);

    private static IResult Text(Func<string> build)
    {
        try
        {
            return Results.Ok(new { text = build() });
        }
        catch (InvalidOperationException exception)
        {
            return Results.Json(new { error = exception.Message }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }

    internal static string DeviceName(HttpContext context) =>
        context.Items["device"] is PairedDevice device
            ? (context.Items["panelUser"] is string user ? $"{user} · {device.Name}" : device.Name)
            : "bilinmeyen cihaz";

    private static bool Contains(string? value, string term) =>
        value is not null && Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;
}
