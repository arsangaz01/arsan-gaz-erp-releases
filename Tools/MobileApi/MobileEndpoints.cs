using System.Globalization;
using ArsanGazERP.Data;
using ArsanGazERP.ErpMcpServer;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record PairRequest(string Code, string? DeviceName);
internal sealed record MoveRequest(string Code, CylinderStatus Status, int? CustomerId, string? Note);
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
                note = "APK yalnızca bilgisayardaki MobileApi üzerinden indirilir; kurulumdan sonra eşleşme anahtarı korunur."
            });
        });

        app.MapGet("/api/yapilandirma", () =>
        {
            string? url = MobileConnectionHub.ReadPublicUrl();
            return Results.Ok(new
            {
                serverUrl = url,
                message = url is { Length: > 0 }
                    ? "Güncel sunucu adresi. Uygulama bunu otomatik kaydedebilir."
                    : "Tünel adresi henüz yok; Telegram bildirimini bekleyin."
            });
        });

        app.MapGet("/api/hub", () => Results.Ok(MobileConnectionHub.GetHubStatus()));

        app.MapGet("/indir", () => File.Exists(MobileApiPaths.ApkFile)
            ? Results.File(MobileApiPaths.ApkFile, "application/vnd.android.package-archive", "ArsanGazERP-Mobil.apk")
            : Results.NotFound("APK henüz yüklenmedi."));

        app.MapPost("/api/pair/request", (PairingService pairing) => Results.Ok(new { message = pairing.RequestCode() }));

        app.MapPost("/api/pair", (PairRequest request, PairingService pairing) =>
            pairing.TryPair(request.Code ?? string.Empty, request.DeviceName ?? string.Empty) is { } token
                ? Results.Ok(new { token })
                : Results.Json(new { error = "Kod hatalı veya süresi dolmuş. Yeni kod isteyin." }, statusCode: StatusCodes.Status400BadRequest));

        app.MapGet("/api/ozet", () => Text(() => "ERP\n" + ErpTools.Summary() + "\nCRM\n" + ErpTools.CrmSummary()));

        app.MapGet("/api/musteriler", async (string? q) =>
        {
            await using ArsanGazDbContext db = new();
            List<Customer> customers = await db.Customers.AsNoTracking().OrderBy(c => c.CompanyName).ToListAsync();
            Dictionary<int, int> counts = await db.Cylinders.AsNoTracking()
                .Where(y => y.Status == CylinderStatus.Customer && y.CurrentCustomerId != null)
                .GroupBy(y => y.CurrentCustomerId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);
            string term = (q ?? string.Empty).Trim();
            return Results.Ok(customers
                .Where(c => term.Length == 0 || Contains(c.CompanyName, term) || Contains(c.Code, term) || Contains(c.City, term))
                .Take(50)
                .Select(c => new
                {
                    c.Id,
                    c.Code,
                    Name = c.CompanyName,
                    c.City,
                    c.Phone,
                    c.IsActive,
                    Cylinders = counts.GetValueOrDefault(c.Id)
                }));
        });

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
        });

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
        });

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
        });

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
        });

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
        });

        app.MapPost("/api/tup/hareket", async (MoveRequest request, HttpContext context) =>
        {
            CrudService crud = new();
            Cylinder? cylinder = await crud.FindCylinderByCodeAsync(request.Code ?? string.Empty);
            if (cylinder is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            try
            {
                string note = string.IsNullOrWhiteSpace(request.Note) ? $"Mobil ({DeviceName(context)})" : request.Note.Trim();
                await crud.MoveCylinderAsync(cylinder.Id, request.Status, request.CustomerId, note, "Mobil hareket");
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }

            return Results.Ok(new { message = $"{cylinder.SerialNumber}: {ErpTools.StatusName((int)cylinder.Status)} → {ErpTools.StatusName((int)request.Status)} kaydedildi." });
        });

        app.MapGet("/api/tahmin", () => Text(ErpTools.ReorderForecasts));
        app.MapGet("/api/test-tarihleri", (int? gun) => Text(() => ErpTools.TestDue(gun ?? 30)));
        app.MapGet("/api/hareketler", (int? gun) => Text(() => ErpTools.RecentMovements(gun ?? 7)));
        app.MapGet("/api/musterideki-tupler", (string? musteri) => Text(() => ErpTools.CylindersAtCustomers(musteri)));

        app.MapGet("/api/ajanlar", () => Results.Ok(AgentChatService.Profiles.Select(p => new { p.Id, p.Name, p.Role })));

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
        });

        app.MapPost("/api/cursor", (CursorRequest request, HttpContext context) =>
        {
            string device = context.Items["device"] is PairedDevice paired ? paired.Name : "Telefon";
            try
            {
                return Results.Ok(new { id = CursorRelay.Enqueue(request.Message ?? string.Empty, device) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        app.MapGet("/api/cursor/{id}", (string id) => CursorRelay.GetStatus(id) is { } status
            ? Results.Ok(status)
            : Results.NotFound(new { error = "İstek bulunamadı." }));
    }

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

    private static string DeviceName(HttpContext context) =>
        context.Items["device"] is PairedDevice device ? device.Name : "bilinmeyen cihaz";

    private static bool Contains(string? value, string term) =>
        value is not null && Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;
}
