using System.Globalization;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record FeatureSwitchRequest(string? Key, bool On);
internal sealed record CustomerAccountCreateRequest(string? KullaniciAdi, string? AdSoyad, int MusteriId, string? Sifre, bool SifreDegistirsin = true);
internal sealed record CustomerAccountUpdateRequest(string? AdSoyad, bool Aktif);
internal sealed record CustomerRequestCreate(string? Tur, int? Adet, string? Not);

// Feature switches and customer accounts (panel Yönetim, Yönetici only) and the customer portal: /musteri (page) and
// /musteri/api/* (data of the signed-in customer only).
internal static class CustomerPortalEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    internal static void MapPanel(RouteGroupBuilder api, WebPanelAuth auth)
    {
        api.MapGet("/yonetim/ozellikler", () => Results.Ok(Features.Catalog.Select(f => new
        {
            f.Key,
            f.Label,
            f.Description,
            On = Features.IsOn(f.Key),
            Permissions = f.Permissions.Select(p => PanelPermissions.Catalog.First(c => c.Key == p).Label)
        }))).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/ozellik", (FeatureSwitchRequest request, HttpContext context) =>
        {
            WebPanelUser actor = Account(context);
            string? error = Features.Set(request.Key ?? string.Empty, request.On, actor.UserName, WebPanelAuth.ClientIp(context));
            return error is null
                ? Results.Ok(new { message = $"\"{Features.ByKey[request.Key!].Label}\" {(request.On ? "açıldı" : "kapatıldı")}. Telefonlar ve paneller yenilendiğinde (en geç bir sonraki ekran açılışında) değişikliği görür." })
                : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapGet("/yonetim/musteri-hesaplari", async () =>
        {
            List<WebPanelUser> accounts = WebPanelUserStore.All().Where(u => u.Role == PanelRoles.Customer).ToList();
            HashSet<int> ids = [.. accounts.Select(u => u.CustomerId ?? 0)];
            await using ArsanGazDbContext db = new();
            Dictionary<int, string> names = await db.Customers.AsNoTracking().Where(c => ids.Contains(c.Id)).ToDictionaryAsync(c => c.Id, c => c.CompanyName);
            return Results.Ok(new
            {
                portalOn = Features.IsOn(Features.CustomerPortal),
                accounts = accounts
                    .OrderByDescending(u => u.IsActive)
                    .ThenBy(u => names.GetValueOrDefault(u.CustomerId ?? 0), StringComparer.Create(Turkish, true))
                    .Select(u => new
                    {
                        u.Id,
                        u.UserName,
                        u.DisplayName,
                        u.Role,
                        u.CustomerId,
                        CustomerName = names.GetValueOrDefault(u.CustomerId ?? 0) ?? "(müşteri kaydı yok)",
                        u.IsActive,
                        u.MustChangePassword,
                        LastLogin = u.LastLoginAt is DateTime last ? LocalText(last) : null,
                        Created = LocalText(u.CreatedAt),
                        u.CreatedBy
                    })
            });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/musteri-hesabi", async (CustomerAccountCreateRequest request, HttpContext context) =>
        {
            await using ArsanGazDbContext db = new();
            Customer? customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.MusteriId);
            if (customer is null)
            {
                return Results.BadRequest(new { error = "Müşteriyi listeden seçin." });
            }

            string? error = auth.CreateCustomerAccount(Account(context), WebPanelAuth.ClientIp(context), request.KullaniciAdi, request.AdSoyad,
                customer.Id, customer.CompanyName, request.Sifre, request.SifreDegistirsin);
            return error is null
                ? Results.Ok(new { message = $"{customer.CompanyName} için {request.KullaniciAdi?.Trim()} müşteri hesabı açıldı." })
                : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/musteri-hesabi/{id:int}", (int id, CustomerAccountUpdateRequest request, HttpContext context) =>
        {
            string? error = auth.UpdateCustomerAccount(Account(context), WebPanelAuth.ClientIp(context), id, request.AdSoyad, request.Aktif);
            return error is null ? Results.Ok(new { message = "Müşteri hesabı güncellendi." }) : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);
    }

    internal static void Map(WebApplication app)
    {
        WebPanelAuth auth = app.Services.GetRequiredService<WebPanelAuth>();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/musteri"))
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cache-Control"] = "no-store";
                headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
            }

            await next(context);
        });

        app.MapGet("/musteri", (HttpContext context) => WebPanelEndpoints.Asset("portal.html", "text/html; charset=utf-8", context));
        app.MapGet("/musteri/app.js", (HttpContext context) => WebPanelEndpoints.Asset("portal.js", "text/javascript; charset=utf-8", context));

        app.MapPost("/musteri/giris", async (PanelLoginRequest request, HttpContext context) =>
        {
            string? error = await auth.CustomerLoginAsync(context, request.Kullanici, request.Sifre);
            return error is null ? Results.Ok(new { ok = true }) : Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);
        });

        app.MapPost("/musteri/cikis", (HttpContext context) =>
        {
            auth.CustomerLogout(context);
            return Results.Ok(new { ok = true });
        });

        RouteGroupBuilder portal = app.MapGroup("/musteri/api");
        portal.AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            if (auth.CurrentCustomer(context) is not (_, WebPanelUser user))
            {
                return Results.Json(new { error = "Oturum kapalı; yeniden giriş yapın." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers[WebPanelAuth.CsrfHeader] != "1")
            {
                return Results.Json(new { error = "Geçersiz istek." }, statusCode: StatusCodes.Status403Forbidden);
            }

            string path = context.Request.Path.Value ?? string.Empty;
            if (user.MustChangePassword && !path.EndsWith("/ben", StringComparison.Ordinal) && !path.EndsWith("/sifre", StringComparison.Ordinal))
            {
                return Results.Json(new { error = "Devam etmeden önce şifrenizi değiştirmelisiniz.", mustChangePassword = true }, statusCode: StatusCodes.Status403Forbidden);
            }

            context.Items["customerAccount"] = user;
            return await next(invocation);
        });

        portal.MapGet("/ben", async (HttpContext context) =>
        {
            WebPanelUser user = Customer(context);
            await using ArsanGazDbContext db = new();
            Customer? customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == user.CustomerId);
            return Results.Ok(new
            {
                user = user.UserName,
                displayName = user.DisplayName,
                customerName = customer?.CompanyName ?? user.DisplayName,
                mustChangePassword = user.MustChangePassword,
                maxOpen = CustomerRequests.MaxOpenPerCustomer
            });
        });

        portal.MapPost("/sifre", async (PanelPasswordRequest request, HttpContext context) =>
        {
            string? error = await auth.CustomerChangePasswordAsync(context, request.Mevcut, request.Yeni, request.YeniTekrar);
            return error is null ? Results.Ok(new { message = "Şifreniz değiştirildi." }) : Results.BadRequest(new { error });
        });

        portal.MapGet("/tupler", async (HttpContext context) =>
        {
            int customerId = Customer(context).CustomerId!.Value;
            DateTime today = DateTime.Today;
            int longDays = CylinderTracking.LongAtCustomerDays;
            List<CustomerStockCylinder> stock = await CustomerStock.LoadAsync(customerId);
            return Results.Ok(new
            {
                totals = CustomerStock.Totals(stock, today, longDays),
                groups = CustomerStock.Groups(stock, today, longDays).Select(g => new { g.GasType, g.Capacity, g.Count, g.Full, g.Empty }),
                cylinders = stock
                    .OrderBy(c => c.GasType, StringComparer.Create(Turkish, true))
                    .ThenBy(c => c.Since)
                    .Select(c => new
                    {
                        c.SerialNumber,
                        c.GasType,
                        Capacity = CustomerStock.CapacityText(c.Capacity, c.CapacityUnit),
                        Fullness = CylinderTracking.FullnessName(c.IsFull),
                        Since = c.Since?.ToString("dd.MM.yyyy", Turkish),
                        Days = CylinderTracking.DaysSince(c.Since, today),
                        TestDue = c.TestDueDate?.ToString("dd.MM.yyyy", Turkish),
                        TestOverdue = c.TestDueDate != null && c.TestDueDate < today
                    })
            });
        });

        portal.MapGet("/talepler", async (HttpContext context) =>
        {
            List<CustomerRequestView> rows = await CustomerRequests.ListAsync(Customer(context).CustomerId, false, 90, 100);
            return Results.Ok(rows.Select(OwnRequestRow));
        });

        portal.MapPost("/talep", async (CustomerRequestCreate request, HttpContext context) =>
        {
            WebPanelUser user = Customer(context);
            try
            {
                CustomerRequest created = await CustomerRequests.CreateAsync(user.CustomerId!.Value, request.Tur ?? string.Empty, request.Adet, request.Not,
                    "Müşteri portalı (" + user.UserName + ")");
                await using ArsanGazDbContext db = new();
                string customerName = await db.Customers.AsNoTracking().Where(c => c.Id == created.CustomerId).Select(c => c.CompanyName).FirstOrDefaultAsync() ?? user.DisplayName;
                string label = CustomerRequests.KindLabel(created.Kind);
                WebPanelAudit.Write(user.UserName, "Müşteri talebi", $"Talep #{created.Id}", $"{customerName}: {label}", WebPanelAuth.ClientIp(context));
                TelegramNotifier.Send($"📨 Müşteri talebi #{created.Id}\n{customerName}: {label}" + (created.Quantity is int q ? $" ({q} tüp)" : string.Empty)
                    + (created.Note is string note ? $"\nNot: {note}" : string.Empty)
                    + "\nPanel > Müşteri Talepleri bölümünden takip edin; seferde o müşteriye teslim veya boş alım okutulunca talep kendiliğinden kapanır.");
                return Results.Ok(new { message = $"Talebiniz alındı ({label}). En kısa sürede dönüş yapılacaktır." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

        portal.MapPost("/talep/{id:int}/iptal", async (int id, HttpContext context) =>
        {
            WebPanelUser user = Customer(context);
            try
            {
                await CustomerRequests.CloseAsync(id, CustomerRequests.Cancelled, "Müşteri (" + user.UserName + ")", "Müşteri iptal etti.", user.CustomerId);
                WebPanelAudit.Write(user.UserName, "Müşteri talebini iptal etti", $"Talep #{id}", ip: WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = "Talebiniz iptal edildi." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });
    }

    private static object OwnRequestRow(CustomerRequestView r) => new
    {
        r.Id,
        r.KindLabel,
        r.Quantity,
        r.Note,
        Created = r.CreatedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
        r.Status,
        r.StatusLabel,
        Handled = r.HandledAt?.ToString("dd.MM.yyyy HH:mm", Turkish)
    };

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;

    private static WebPanelUser Customer(HttpContext context) => (WebPanelUser)context.Items["customerAccount"]!;

    private static string LocalText(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish);
}
