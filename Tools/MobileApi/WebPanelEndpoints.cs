using System.Globalization;
using System.Reflection;
using System.Text;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record PanelLoginRequest(string? Kullanici, string? Sifre);
internal sealed record PanelPasswordRequest(string? Mevcut, string? Yeni, string? YeniTekrar);
internal sealed record PanelMoveRequest(CylinderStatus Status, int? CustomerId, string? Plate, bool? IsFull, string? Note, bool Confirm = false);
internal sealed record PanelEditRequest(string? GasType, decimal Capacity, string? CapacityUnit, DateTime? TestDueDate, bool IsActive, string? Surum = null);
internal sealed record PanelUserCreateRequest(string? KullaniciAdi, string? AdSoyad, string? Rol, string? Sifre, bool SifreDegistirsin = true);
internal sealed record PanelUserUpdateRequest(string? AdSoyad, string? Rol, bool Aktif);
internal sealed record PanelTripLoadRequest(string? Plate, List<string?>? Codes);
internal sealed record PanelTripReasonRequest(List<int>? CylinderIds, string? Reason, string? Note);
internal sealed record PanelTripCheckRequest(List<int>? CylinderIds, bool Undo);
internal sealed record PanelUserPasswordRequest(string? Sifre, bool SifreDegistirsin = true);
internal sealed record PanelPermissionsRequest(List<string>? Izinler, bool Sifirla = false);
internal sealed record PanelUserDeleteRequest(string? Onay);

// Endpoint metadata read by the /panel/api filter and the /api middleware.
internal sealed record PanelPermission(string Name);
internal sealed class PanelOpenBeforePasswordChange;

internal static class EndpointRules
{
    internal static TBuilder Requires<TBuilder>(this TBuilder builder, string permission) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new PanelPermission(permission));

    internal static TBuilder SignedIn<TBuilder>(this TBuilder builder) where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new SignedInOnly());
}

// Browser panel for inspecting and managing cylinder tracking: /panel (page) and /panel/api/* (data).
internal static class WebPanelEndpoints
{
    private static readonly SemaphoreSlim EditGate = new(1, 1);

    private const int MaxMovementRows = 500;
    private const int MaxExportRows = 20_000;
    private const int MaxCylinderRows = 5000;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(WebApplication app)
    {
        WebPanelAuth auth = app.Services.GetRequiredService<WebPanelAuth>();
        DeviceStore devices = app.Services.GetRequiredService<DeviceStore>();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/panel"))
            {
                IHeaderDictionary headers = context.Response.Headers;
                headers["X-Content-Type-Options"] = "nosniff";
                headers["X-Frame-Options"] = "DENY";
                headers["Referrer-Policy"] = "no-referrer";
                headers["Cache-Control"] = "no-store";
                headers["Content-Security-Policy"] =
                    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self' http://127.0.0.1:39201; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
            }

            await next(context);
        });

        app.MapGet("/", () => Results.Redirect("/panel"));
        app.MapGet("/panel", () => Asset("panel.html", "text/html; charset=utf-8"));
        app.MapGet("/panel/app.js", () => Asset("panel.js", "text/javascript; charset=utf-8"));

        app.MapPost("/panel/giris", async (PanelLoginRequest request, HttpContext context) =>
        {
            string? error = await auth.LoginAsync(context, request.Kullanici, request.Sifre);
            return error is null ? Results.Ok(new { ok = true }) : Results.Json(new { error }, statusCode: StatusCodes.Status401Unauthorized);
        });

        app.MapPost("/panel/cikis", (HttpContext context) =>
        {
            auth.Logout(context);
            return Results.Ok(new { ok = true });
        });

        RouteGroupBuilder api = app.MapGroup("/panel/api");
        api.AddEndpointFilter(async (invocation, next) =>
        {
            HttpContext context = invocation.HttpContext;
            if (auth.Current(context) is not (_, WebPanelUser user))
            {
                return Results.Json(new { error = "Oturum kapalı; yeniden giriş yapın." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers[WebPanelAuth.CsrfHeader] != "1")
            {
                return Results.Json(new { error = "Geçersiz istek." }, statusCode: StatusCodes.Status403Forbidden);
            }

            EndpointMetadataCollection? metadata = context.GetEndpoint()?.Metadata;
            if (user.MustChangePassword && metadata?.GetMetadata<PanelOpenBeforePasswordChange>() is null)
            {
                return Results.Json(new { error = "Devam etmeden önce şifrenizi değiştirmelisiniz.", mustChangePassword = true }, statusCode: StatusCodes.Status403Forbidden);
            }

            if (DeviceAuthMiddleware.PermissionProblem(metadata, user) is string problem)
            {
                return Results.Json(new { error = problem, forbidden = true }, statusCode: StatusCodes.Status403Forbidden);
            }

            context.Items["panelUser"] = user.UserName;
            context.Items["panelAccount"] = user;
            return await next(invocation);
        });

        api.MapGet("/ben", (HttpContext context) =>
        {
            WebPanelUser user = Account(context);
            return Results.Ok(new
            {
                user = user.UserName,
                displayName = user.DisplayName,
                role = user.Role,
                permissions = PanelRoles.Effective(user),
                mustChangePassword = user.MustChangePassword,
                sessionMinutes = (int)WebPanelAuth.SessionLifetime.TotalMinutes,
                roles = PanelRoles.All.Select(r => new { name = r, description = PanelRoles.Description(r) })
            });
        }).SignedIn().WithMetadata(new PanelOpenBeforePasswordChange());

        api.MapPost("/sifre", async (PanelPasswordRequest request, HttpContext context) =>
        {
            string? error = await auth.ChangePasswordAsync(context, request.Mevcut, request.Yeni, request.YeniTekrar);
            return error is null ? Results.Ok(new { message = "Şifre değiştirildi. Diğer cihazlardaki oturumlar kapatıldı." }) : Results.BadRequest(new { error });
        }).SignedIn().WithMetadata(new PanelOpenBeforePasswordChange());

        api.MapGet("/yonetim/yetkiler", () => Results.Ok(new
        {
            groups = PanelPermissions.Catalog.GroupBy(p => p.Group).Select(g => new
            {
                name = g.Key,
                items = g.Select(p => new { key = p.Key, label = p.Label, adminOnly = p.AdminOnly })
            }),
            roles = PanelRoles.All.Select(r => new { name = r, description = PanelRoles.Description(r), defaults = PanelRoles.Defaults(r) })
        })).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}/yetkiler", (int id, PanelPermissionsRequest request, HttpContext context) =>
        {
            string? error = auth.SetPermissions(Account(context), WebPanelAuth.ClientIp(context), id, request.Izinler, request.Sifirla);
            return error is null
                ? Results.Ok(new { message = request.Sifirla ? "Yetkiler rolün varsayılanına döndü." : "Yetkiler kaydedildi; kullanıcının bir sonraki işleminden itibaren geçerli." })
                : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapGet("/yonetim/cihazlar", () =>
        {
            DateTime now = DateTime.UtcNow;
            return Results.Ok(devices.All()
                .OrderBy(d => d.UserId is null)
                .ThenByDescending(d => d.LastSeenUtc ?? d.CreatedUtc)
                .Select(d =>
                {
                    WebPanelUser? owner = d.UserId is int userId ? WebPanelUserStore.Find(userId) : null;
                    string state = d.UserId is null ? "Eski eşleştirme (giriş yapılmamış, erişimi yok)"
                        : owner is null ? "Kullanıcı silinmiş"
                        : d.ExpiresUtc is not DateTime expires || expires <= now ? "Süresi doldu"
                        : !owner.IsActive ? "Hesap pasif"
                        : owner.SecurityStamp != d.Stamp ? "Şifre/yetki değişti; yeniden giriş gerekli"
                        : "Aktif";
                    return new
                    {
                        d.Id,
                        d.Name,
                        User = owner?.UserName,
                        DisplayName = owner?.DisplayName,
                        State = state,
                        Active = state == "Aktif",
                        Created = LocalText(d.CreatedUtc),
                        LastSeen = d.LastSeenUtc is DateTime seen ? LocalText(seen) : null,
                        Expires = d.ExpiresUtc is DateTime until ? LocalText(until) : null,
                        d.Ip
                    };
                }));
        }).Requires(PanelPermissions.ManageDevices);

        api.MapPost("/yonetim/cihaz/{id}/kapat", (string id, HttpContext context) =>
        {
            PairedDevice? device = devices.All().FirstOrDefault(d => d.Id == id);
            if (device is null || !devices.Remove(id))
            {
                return Results.NotFound(new { error = "Cihaz oturumu bulunamadı (zaten kapanmış olabilir)." });
            }

            string owner = device.UserId is int userId ? WebPanelUserStore.Find(userId)?.UserName ?? "?" : "eski eşleştirme";
            WebPanelAudit.Write(Account(context).UserName, "Mobil oturum kapatıldı", device.Name, owner, WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = $"{device.Name} ({owner}) oturumu kapatıldı; telefon yeniden giriş isteyecek." });
        }).Requires(PanelPermissions.ManageDevices);

        api.MapGet("/yonetim/kullanicilar", (HttpContext context) =>
        {
            int self = Account(context).Id;
            DateTime now = DateTime.UtcNow;
            IReadOnlyList<PairedDevice> phones = devices.All();
            return Results.Ok(WebPanelUserStore.All()
                .OrderByDescending(u => u.IsActive)
                .ThenBy(u => Array.IndexOf(PanelRoles.All, u.Role))
                .ThenBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(u => new
                {
                    u.Id,
                    u.UserName,
                    u.DisplayName,
                    u.Role,
                    u.IsActive,
                    u.MustChangePassword,
                    Created = LocalText(u.CreatedAt),
                    u.CreatedBy,
                    LastLogin = u.LastLoginAt is DateTime last ? LocalText(last) : null,
                    u.LastLoginIp,
                    PasswordChanged = u.PasswordChangedAt is DateTime changed ? LocalText(changed) : null,
                    Sessions = auth.ActiveSessionCount(u.Id),
                    Phones = phones.Count(d => d.UserId == u.Id && d.Stamp == u.SecurityStamp && d.ExpiresUtc > now),
                    Permissions = PanelRoles.Effective(u),
                    Extra = u.Role == PanelRoles.Admin ? [] : u.ExtraPermissions.Where(PanelPermissions.IsKnown).ToList(),
                    Removed = u.Role == PanelRoles.Admin ? [] : u.RemovedPermissions.Where(PanelPermissions.IsKnown).ToList(),
                    IsSelf = u.Id == self
                }));
        }).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/kullanici", (PanelUserCreateRequest request, HttpContext context) =>
        {
            string? error = auth.CreateUser(Account(context), WebPanelAuth.ClientIp(context), request.KullaniciAdi, request.AdSoyad, request.Rol, request.Sifre, request.SifreDegistirsin);
            return error is null ? Results.Ok(new { message = $"{request.KullaniciAdi?.Trim()} kullanıcısı oluşturuldu." }) : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}", (int id, PanelUserUpdateRequest request, HttpContext context) =>
        {
            string? error = auth.UpdateUser(Account(context), WebPanelAuth.ClientIp(context), id, request.AdSoyad, request.Rol, request.Aktif);
            return error is null ? Results.Ok(new { message = "Kullanıcı güncellendi. Yetkisi veya durumu değiştiyse açık oturumları kapatıldı." }) : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}/sifre", (int id, PanelUserPasswordRequest request, HttpContext context) =>
        {
            string? error = auth.ResetPassword(Account(context), WebPanelAuth.ClientIp(context), id, request.Sifre, request.SifreDegistirsin);
            return error is null
                ? Results.Ok(new { message = "Şifre sıfırlandı; kullanıcının açık oturumları kapatıldı." + (request.SifreDegistirsin ? " İlk girişte yeni şifre belirleyecek." : string.Empty) })
                : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}/sil", (int id, PanelUserDeleteRequest request, HttpContext context) =>
        {
            string? error = auth.DeleteUser(Account(context), WebPanelAuth.ClientIp(context), id, request.Onay, devices, out string summary);
            return error is null
                ? Results.Ok(new { message = $"{request.Onay?.Trim()} kullanıcısı kalıcı olarak silindi; {summary}. Geçmiş kayıtlardaki adı korunur." })
                : Results.BadRequest(new { error });
        }).Requires(PanelPermissions.ManageUsers);

        api.MapGet("/yonetim/denetim", () => Results.Ok(WebPanelAudit.Latest(200).Select(e => new
        {
            At = LocalText(e.At),
            e.Actor,
            e.Action,
            e.Target,
            e.Detail,
            e.Ip
        }))).Requires(PanelPermissions.AuditLog);

        api.MapPost("/musteri", async (CustomerCreateRequest request, HttpContext context) =>
        {
            IResult result = await CustomerChoices.CreateAsync(request, "web panel: " + context.Items["panelUser"]);
            if (result is IStatusCodeHttpResult { StatusCode: 200 })
            {
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Müşteri oluşturuldu", request.Name?.Trim(), ip: WebPanelAuth.ClientIp(context));
            }

            return result;
        }).Requires(PanelPermissions.CustomerCreate);

        api.MapGet("/ozet", async () =>
        {
            CylinderOverview overview = await new CrudService().CylinderOverviewAsync();
            await using ArsanGazDbContext db = new();
            DateTime today = DateTime.Today;
            int todayMoves = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.MovementDate >= today);
            int weekMoves = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.MovementDate >= today.AddDays(-6));
            int inactive = await db.Cylinders.AsNoTracking().CountAsync(c => !c.IsActive);
            int testSoon = await db.Cylinders.AsNoTracking().CountAsync(c => c.IsActive && c.TestDueDate != null && c.TestDueDate <= today.AddDays(30));
            int companyOwned = await db.Cylinders.AsNoTracking().CountAsync(c => c.IsActive && c.OwnerCustomerId == null);
            int customerOwned = await db.Cylinders.AsNoTracking().CountAsync(c => c.IsActive && c.OwnerCustomerId != null);
            // Top owners by cylinder count for the homepage ownership grid (click → Tüpler filter).
            const int ownerTiles = 12;
            var ownerGroups = await (
                from c in db.Cylinders.AsNoTracking()
                join o in db.Customers.AsNoTracking() on c.OwnerCustomerId equals o.Id
                where c.IsActive && c.OwnerCustomerId != null
                group c by new { Id = o.Id, o.CompanyName } into g
                select new { id = g.Key.Id, name = g.Key.CompanyName, count = g.Count() })
                .OrderByDescending(x => x.count).ThenBy(x => x.name)
                .ToListAsync();
            List<object> topOwners = ownerGroups.Take(ownerTiles).Cast<object>().ToList();
            int otherOwners = ownerGroups.Skip(ownerTiles).Sum(x => x.count);
            int ownerCustomerCount = ownerGroups.Count;
            List<object> recent = (await MovementQuery(db).Take(10).ToListAsync()).Select(MovementRow).ToList();
            return Results.Ok(new
            {
                overview, todayMoves, weekMoves, inactive, testSoon, recent,
                ownership = new { companyOwned, customerOwned, ownerCustomerCount, topOwners, otherOwners }
            });
        }).Requires(PanelPermissions.Summary);

        api.MapGet("/tupler", async (string? q, int? durum, string? plaka, int? musteri, string? doluluk, bool? uzun, bool? pasif, string? sahip, int? sahipId) =>
        {
            await using ArsanGazDbContext db = new();
            IQueryable<Cylinder> query = db.Cylinders.AsNoTracking().Include(c => c.CurrentCustomer).Include(c => c.OwnerCustomer).Where(c => c.IsActive == (pasif != true));
            if (durum is int status && Enum.IsDefined((CylinderStatus)status))
            {
                query = query.Where(c => c.Status == (CylinderStatus)status);
            }

            if (!string.IsNullOrWhiteSpace(plaka))
            {
                query = query.Where(c => c.VehiclePlate == plaka.Trim());
            }

            if (musteri is int choice && await LinkedCustomerIdAsync(db, choice) is var customerId)
            {
                query = query.Where(c => c.CurrentCustomerId == customerId && c.Status == CylinderStatus.Customer);
            }

            // sahip=firma → company stock; sahip=musteri → any customer-owned; sahipId → that owner.
            if (sahipId is int ownerId && ownerId > 0)
            {
                query = query.Where(c => c.OwnerCustomerId == ownerId);
            }
            else if (string.Equals(sahip, "firma", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(c => c.OwnerCustomerId == null);
            }
            else if (string.Equals(sahip, "musteri", StringComparison.OrdinalIgnoreCase))
            {
                query = query.Where(c => c.OwnerCustomerId != null);
            }

            if (doluluk is "dolu" or "bos" or "bilinmiyor")
            {
                bool? wanted = doluluk switch { "dolu" => true, "bos" => false, _ => null };
                query = query.Where(c => c.IsFull == wanted);
            }

            if (uzun == true)
            {
                DateTime limit = DateTime.Today.AddDays(-CylinderTracking.LongAtCustomerDays);
                query = query.Where(c => c.Status == CylinderStatus.Customer && c.LastMovementAt != null && c.LastMovementAt < limit);
            }

            // Search before Take so a match outside the first 5000 serials is still found (same as TupTakip).
            string term = (q ?? string.Empty).Trim();
            List<Cylinder> rows = await (term.Length == 0
                ? query.OrderBy(c => c.SerialNumber).Take(MaxCylinderRows)
                : query.OrderBy(c => c.SerialNumber)).ToListAsync();
            DateTime today = DateTime.Today;
            return Results.Ok(rows
                .Where(c => term.Length == 0 || Contains(c.SerialNumber, term) || Contains(c.Barcode, term) || Contains(c.GasType, term)
                    || Contains(c.CurrentCustomer?.CompanyName, term) || Contains(c.OwnerCustomer?.CompanyName, term) || Contains(c.VehiclePlate, term))
                .Take(MaxCylinderRows)
                .Select(c => new
                {
                    c.Id,
                    c.SerialNumber,
                    c.Barcode,
                    c.GasType,
                    Capacity = $"{c.Capacity.ToString("0.##", Turkish)} {c.CapacityUnit}",
                    Status = (int)c.Status,
                    StatusName = CylinderTracking.StatusName((int)c.Status),
                    Fullness = CylinderTracking.FullnessName(c.IsFull),
                    Customer = c.CurrentCustomer?.CompanyName,
                    Plate = c.VehiclePlate,
                    LastMovement = c.LastMovementAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                    DaysThere = CylinderTracking.DaysSince(c.LastMovementAt, today),
                    TestDue = c.TestDueDate?.ToString("dd.MM.yyyy", Turkish),
                    Owner = c.OwnerCustomer?.CompanyName,
                    TestOverdue = c.TestDueDate != null && c.TestDueDate < today
                }));
        }).Requires(PanelPermissions.CylinderView);

        api.MapGet("/tup/{id:int}", async (int id) =>
        {
            await using ArsanGazDbContext db = new();
            Cylinder? c = await db.Cylinders.AsNoTracking().Include(x => x.CurrentCustomer).Include(x => x.OwnerCustomer).FirstOrDefaultAsync(x => x.Id == id);
            if (c is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            List<CylinderMovement> movements = await db.CylinderMovements.AsNoTracking().Include(m => m.Customer)
                .Where(m => m.CylinderId == id).OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id).ToListAsync();
            return Results.Ok(new
            {
                c.Id,
                c.SerialNumber,
                c.Barcode,
                Owner = c.OwnerCustomer?.CompanyName,
                c.GasType,
                c.Capacity,
                c.CapacityUnit,
                Status = (int)c.Status,
                StatusName = CylinderTracking.StatusName((int)c.Status),
                c.IsFull,
                Fullness = CylinderTracking.FullnessName(c.IsFull),
                CustomerId = c.CurrentCustomerId,
                Customer = c.CurrentCustomer?.CompanyName,
                Plate = c.VehiclePlate,
                LastMovement = c.LastMovementAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                DaysThere = CylinderTracking.DaysSince(c.LastMovementAt, DateTime.Today),
                TestDue = c.TestDueDate?.ToString("yyyy-MM-dd"),
                c.IsActive,
                Created = c.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy", Turkish),
                Movements = movements.Select(m => new
                {
                    Date = m.MovementDate.ToString("dd.MM.yyyy HH:mm", Turkish),
                    From = CylinderTracking.StatusName((int)m.FromStatus),
                    To = CylinderTracking.StatusName((int)m.ToStatus),
                    m.MovementType,
                    Customer = m.Customer?.CompanyName,
                    Plate = m.VehiclePlate,
                    Fullness = CylinderTracking.FullnessName(m.IsFull),
                    Note = m.Description
                }),
                Version = EditVersion(c)
            });
        }).Requires(PanelPermissions.CylinderView);

        api.MapGet("/hareketler", async (string? bas, string? bit, int? musteri, int? durum, string? q) =>
        {
            await using ArsanGazDbContext db = new();
            List<CylinderMovement> rows = await FilteredMovementsAsync(db, bas, bit, musteri, durum, q, MaxMovementRows + 1);
            return Results.Ok(new
            {
                rows = rows.Take(MaxMovementRows).Select(MovementRow),
                truncated = rows.Count > MaxMovementRows,
                limit = MaxMovementRows
            });
        }).Requires(PanelPermissions.MovementView);

        api.MapGet("/hareketler.csv", async (string? bas, string? bit, int? musteri, int? durum, string? q) =>
        {
            await using ArsanGazDbContext db = new();
            List<CylinderMovement> rows = await FilteredMovementsAsync(db, bas, bit, musteri, durum, q, MaxExportRows);
            StringBuilder csv = new();
            csv.AppendLine("Tarih;Seri No;Barkod;Gaz;Hareket;Önceki durum;Yeni durum;Müşteri;Plaka;Dolu/Boş;Not");
            foreach (CylinderMovement m in rows)
            {
                csv.AppendLine(string.Join(';', new[]
                {
                    m.MovementDate.ToString("dd.MM.yyyy HH:mm", Turkish), m.Cylinder.SerialNumber, m.Cylinder.Barcode, m.Cylinder.GasType, m.MovementType,
                    CylinderTracking.StatusName((int)m.FromStatus), CylinderTracking.StatusName((int)m.ToStatus), m.Customer?.CompanyName,
                    m.VehiclePlate, CylinderTracking.FullnessName(m.IsFull), m.Description
                }.Select(CsvCell)));
            }

            byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
            return Results.File(bytes, "text/csv; charset=utf-8", $"tup-hareketleri-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        }).Requires(PanelPermissions.Export);

        api.MapGet("/seferler", async (string? durum) =>
        {
            bool? active = durum switch { "aktif" => true, "kapali" => false, _ => null };
            return Results.Ok((await new CylinderTripService().TripsAsync(active)).Select(TripRow));
        }).Requires(PanelPermissions.TripView);

        api.MapGet("/sefer/{id:int}", async (int id) =>
        {
            TripDetail? detail = await new CylinderTripService().TripDetailAsync(id);
            if (detail is null)
            {
                return Results.NotFound(new { error = "Sefer bulunamadı." });
            }

            return Results.Ok(new
            {
                trip = TripRow(detail.Trip),
                cylinders = detail.Cylinders.Select(c => new
                {
                    c.CylinderId,
                    c.SerialNumber,
                    c.GasType,
                    c.Plate,
                    Loaded = c.LoadedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
                    Delivered = c.DeliveredAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                    Returned = c.ReturnedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                    c.Missing,
                    c.Reason,
                    c.ReasonLabel,
                    c.Note,
                    ReasonAt = c.ReasonAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                    c.ReasonBy,
                    CheckedAt = c.CheckedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                    c.CheckedBy,
                    c.IsPickup,
                    c.PickupCustomer
                }),
                unexpected = detail.UnexpectedReturns.Select(u => new
                {
                    u.CylinderId,
                    u.SerialNumber,
                    u.GasType,
                    u.Plate,
                    At = u.At.ToString("dd.MM.yyyy HH:mm", Turkish)
                })
            });
        }).Requires(PanelPermissions.TripView);

        api.MapGet("/sefer/{id:int}/yuk", async (int id, string? plaka) => await VehicleEndpoints.TripLoadAsync(id, plaka)).Requires(PanelPermissions.TripView);

        api.MapPost("/sefer/baslat", async (TripStartRequest request, HttpContext context) =>
        {
            try
            {
                CylinderTrip trip = await new CylinderTripService().StartTripAsync(request.Plates, request.Note, $"Web panel ({context.Items["panelUser"]})");
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Sefer başlatıldı", trip.Title, string.Join(", ", request.Plates ?? []), WebPanelAuth.ClientIp(context));
                return Results.Ok(new { trip.Id, trip.Title, message = $"{trip.Title} başlatıldı." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripStart);

        api.MapPost("/sefer/{id:int}/yukle", async (int id, PanelTripLoadRequest request, HttpContext context) =>
        {
            try
            {
                TripLoadResult result = await new CylinderTripService().LoadCylindersAsync(id, request.Plate, request.Codes ?? [], $"Web panel ({context.Items["panelUser"]})");
                string message = $"{result.Added} tüp {request.Plate} aracına yüklendi." + (result.Skipped > 0 ? $" {result.Skipped} atlandı:\n" + string.Join("\n", result.Messages) : string.Empty);
                return Results.Ok(new { message, result.Added, result.Skipped, result.Messages });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripLoad);

        api.MapPost("/sefer/{id:int}/kapat", async (int id, HttpContext context) =>
        {
            try
            {
                // ClosedBy must be the panel username so depot accounts see only trips they closed.
                TripCloseResult result = await new CylinderTripService().CloseTripAsync(id, context.Items["panelUser"]?.ToString());
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Sefer kapatıldı", $"Sefer #{id}", result.Reconcile.HasAlerts ? result.Reconcile.AlertText : null, WebPanelAuth.ClientIp(context));
                CylinderTripEndpoints.NotifyManagers(result);
                return Results.Ok(new { message = result.Message, hasAlerts = result.Reconcile.HasAlerts });
            }
            catch (TripReasonsRequiredException exception)
            {
                return CylinderTripEndpoints.ReasonsRequired(exception);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripClose);

        api.MapGet("/sefer-gerekceler", () => Results.Ok(CylinderTripEndpoints.ReasonCatalog())).Requires(PanelPermissions.TripView);

        api.MapPost("/sefer/{id:int}/gerekce", async (int id, PanelTripReasonRequest request, HttpContext context) =>
        {
            try
            {
                string user = (string)context.Items["panelUser"]!;
                TripReasonResult result = await new CylinderTripService().ExplainMissingAsync(id, request.CylinderIds ?? [], request.Reason, request.Note, $"Web panel ({user})");
                WebPanelAudit.Write(user, "Sefer eksik gerekçesi", $"Sefer #{id}",
                    $"{TripMissingReasons.Label(request.Reason)} · {result.Updated} tüp" + (string.IsNullOrWhiteSpace(request.Note) ? string.Empty : " · " + request.Note.Trim()),
                    WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = CylinderTripEndpoints.ReasonMessage(result, "tüpe gerekçe yazıldı."), result.Updated, result.Messages });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripDeliver);

        api.MapPost("/sefer/{id:int}/gerekce-kontrol", async (int id, PanelTripCheckRequest request, HttpContext context) =>
        {
            try
            {
                string user = (string)context.Items["panelUser"]!;
                TripReasonResult result = await new CylinderTripService().CheckMissingAsync(id, request.CylinderIds ?? [], $"Web panel ({user})", request.Undo);
                WebPanelAudit.Write(user, request.Undo ? "Sefer eksik kontrolü geri alındı" : "Sefer eksik gerekçesi kontrol edildi", $"Sefer #{id}",
                    $"{result.Updated} tüp", WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = CylinderTripEndpoints.ReasonMessage(result, request.Undo ? "tüpün kontrolü geri alındı." : "tüp kontrol edildi."), result.Updated, result.Messages });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.TripClose);

        api.MapGet("/sefer-eksikler", async (int? gun, bool? tumu) =>
            Results.Ok((await new CylinderTripService().MissingReportAsync(gun ?? 60, tumu == true)).Select(m => new
            {
                m.TripId,
                m.TripTitle,
                m.TripActive,
                TripStarted = m.TripStartedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
                m.CylinderId,
                m.SerialNumber,
                m.GasType,
                m.Plate,
                Loaded = m.LoadedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
                m.Reason,
                m.ReasonLabel,
                m.Note,
                ReasonAt = m.ReasonAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                m.ReasonBy,
                CheckedAt = m.CheckedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                m.CheckedBy,
                m.CurrentStatus,
                m.CurrentCustomer,
                m.CurrentPlate,
                LastMovement = m.LastMovementAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
                m.SeenAfterTrip,
                m.IsPickup
            }))).Requires(PanelPermissions.TripView);

        api.MapGet("/araclar", async (bool? pasif) => Results.Ok((await new VehicleService().ListAsync(pasif == true)).Select(VehicleEndpoints.VehicleRow))).Requires(PanelPermissions.VehicleView);

        api.MapGet("/arac/{id:int}", async (int id) =>
        {
            VehicleDetail? detail = await new VehicleService().DetailAsync(id);
            return detail is null ? Results.NotFound(new { error = "Araç bulunamadı." }) : Results.Ok(VehicleEndpoints.DetailDto(detail));
        }).Requires(PanelPermissions.VehicleView);

        api.MapPost("/arac", async (VehicleSaveRequest request, HttpContext context) =>
            await VehicleEndpoints.SaveAsync(async () =>
            {
                Vehicle vehicle = await new VehicleService().CreateAsync(request.Input);
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Araç eklendi", vehicle.Plate, vehicle.Name, WebPanelAuth.ClientIp(context));
                return vehicle;
            }, v => $"{v.Plate} araç olarak kaydedildi.")).Requires(PanelPermissions.VehicleManage);

        api.MapPost("/arac/{id:int}", async (int id, VehicleSaveRequest request, HttpContext context) =>
            await VehicleEndpoints.SaveAsync(async () =>
            {
                Vehicle vehicle = await new VehicleService().UpdateAsync(id, request.Input);
                WebPanelAudit.Write((string)context.Items["panelUser"]!, vehicle.IsActive ? "Araç güncellendi" : "Araç pasif", vehicle.Plate, null, WebPanelAuth.ClientIp(context));
                return vehicle;
            }, v => $"{v.Plate} güncellendi." + (v.IsActive ? string.Empty : " Araç pasif; yeni seferlerde seçilemez."))).Requires(PanelPermissions.VehicleManage);

        api.MapGet("/musteriler", async (string? q, int? take) => Results.Ok(await CustomerChoices.SearchAsync(q, take ?? 50))).Requires(PanelPermissions.CustomerView);

        api.MapGet("/secenekler", async () => Results.Ok(new
        {
            statuses = CylinderTracking.AllStatuses.Select(s => new { id = s, name = CylinderTracking.StatusName(s) }),
            gasTypes = await new CrudService().KnownGasTypesAsync(),
            plates = await new CrudService().KnownVehiclePlatesAsync(),
            longDays = CylinderTracking.LongAtCustomerDays
        })).SignedIn();

        api.MapPost("/tup/{id:int}/hareket", async (int id, PanelMoveRequest request, HttpContext context) =>
        {
            string note = $"Web panel ({context.Items["panelUser"]})" + (string.IsNullOrWhiteSpace(request.Note) ? string.Empty : ": " + request.Note.Trim());
            await using (ArsanGazDbContext db = new())
            {
                CylinderStatus? from = await db.Cylinders.AsNoTracking().Where(c => c.Id == id).Select(c => (CylinderStatus?)c.Status).FirstOrDefaultAsync();
                if (from is null)
                {
                    return Results.NotFound(new { error = "Tüp bulunamadı." });
                }

                string needed = PanelPermissions.ForMove((int)from.Value, (int)request.Status);
                if (!PanelRoles.Has(Account(context), needed))
                {
                    return Results.Json(new { error = DeviceAuthMiddleware.MissingPermission(needed), forbidden = true }, statusCode: StatusCodes.Status403Forbidden);
                }
            }

            try
            {
                string? warning = await new CrudService().MoveCylinderAsync(
                    id, request.Status, request.CustomerId, note, "Web panel hareketi", request.Plate, request.IsFull, request.Confirm);
                return Results.Ok(new { message = $"{CylinderTracking.StatusName((int)request.Status)} olarak kaydedildi." + (warning is null ? string.Empty : "\n" + warning) });
            }
            catch (CylinderConflictException conflict)
            {
                return Results.Json(new { error = conflict.Message, conflict = true }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (StaleRecordException stale)
            {
                return Results.Json(new { error = stale.Message, stale = true }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(PanelPermissions.CylinderView).WithMetadata(new MovePermissionChecked());

        api.MapPost("/tup", async (NewCylinderRequest request, HttpContext context) =>
        {
            try
            {
                Cylinder cylinder = await new CrudService().RegisterCylinderAsync(
                    request.SerialNumber ?? string.Empty, request.Barcode, request.GasType ?? string.Empty, request.Capacity,
                    request.CapacityUnit, request.IsFull, request.TestDueDate, $"Web panel kaydı ({context.Items["panelUser"]})", request.CustomerId);
                return Results.Ok(new { message = $"{cylinder.SerialNumber} {await CustomerChoices.PlaceTextAsync(cylinder)} olarak envantere eklendi.", cylinder.Id });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (DbUpdateException)
            {
                return Results.BadRequest(new { error = "Bu seri numarası veya barkod zaten kayıtlı." });
            }
        }).Requires(PanelPermissions.CylinderRegister);

        api.MapPost("/tup/{id:int}/duzenle", async (int id, PanelEditRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.GasType) || request.Capacity <= 0)
            {
                return Results.BadRequest(new { error = "Gaz türü zorunludur ve kapasite sıfırdan büyük olmalıdır." });
            }

            // Panel edits are serialized and compared with the version the editor loaded, so two people editing the same
            // cylinder cannot silently overwrite each other.
            await EditGate.WaitAsync();
            try
            {
                await using ArsanGazDbContext db = new();
                Cylinder? cylinder = await db.Cylinders.FirstOrDefaultAsync(c => c.Id == id);
                if (cylinder is null)
                {
                    return Results.NotFound(new { error = "Tüp bulunamadı." });
                }

                if (request.Surum is { Length: > 0 } loaded && loaded != EditVersion(cylinder))
                {
                    return Results.Json(new { error = StaleRecordException.DefaultMessage, stale = true }, statusCode: StatusCodes.Status409Conflict);
                }

                cylinder.GasType = request.GasType.Trim();
                cylinder.Capacity = request.Capacity;
                cylinder.CapacityUnit = string.IsNullOrWhiteSpace(request.CapacityUnit) ? "Litre" : request.CapacityUnit.Trim();
                cylinder.TestDueDate = request.TestDueDate?.Date;
                cylinder.IsActive = request.IsActive;
                await db.SaveChangesAsync();
                return Results.Ok(new { message = $"{cylinder.SerialNumber} güncellendi." + (request.IsActive ? string.Empty : " Tüp pasife alındı; listelerde görünmez."), version = EditVersion(cylinder) });
            }
            finally
            {
                EditGate.Release();
            }
        }).Requires(PanelPermissions.CylinderEdit);
    }

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;

    // Fingerprint of the editable cylinder fields, sent with the detail and compared on save.
    internal static string EditVersion(Cylinder c) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\u001f",
            c.GasType, c.Capacity.ToString(CultureInfo.InvariantCulture), c.CapacityUnit, c.TestDueDate?.ToString("yyyy-MM-dd") ?? "", c.IsActive ? "1" : "0"))))[..16];

    private static object TripRow(TripListItem t) => new
    {
        t.Id,
        t.Title,
        t.Active,
        Started = t.StartedAt.ToString("dd.MM.yyyy HH:mm", Turkish),
        Closed = t.ClosedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
        t.Note,
        t.CreatedBy,
        Vehicles = t.Vehicles.Select(v => new
        {
            v.Plate,
            v.OnTrip,
            Returned = v.ReturnedAt?.ToString("dd.MM.yyyy HH:mm", Turkish),
            v.Loaded,
            v.Delivered,
            ReturnedCount = v.Returned,
            v.OnVehicle,
            v.Missing
        }),
        t.Loaded,
        t.Delivered,
        t.Returned,
        t.OnVehicle,
        t.Missing,
        MissingByGas = t.MissingByGas.Select(g => new { g.GasType, g.Count }),
        t.MissingUnexplained,
        t.MissingUnchecked,
        t.PickedUp,
        t.RequireReasons
    };

    private static string LocalText(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish);

    private static IQueryable<CylinderMovement> MovementQuery(ArsanGazDbContext db) =>
        db.CylinderMovements.AsNoTracking().Include(m => m.Cylinder).Include(m => m.Customer)
            .OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id);

    private static async Task<List<CylinderMovement>> FilteredMovementsAsync(
        ArsanGazDbContext db, string? from, string? to, int? customerId, int? status, string? search, int take)
    {
        DateTime start = ParseDate(from) ?? DateTime.Today.AddDays(-30);
        DateTime end = (ParseDate(to) ?? DateTime.Today).AddDays(1);
        IQueryable<CylinderMovement> query = MovementQuery(db).Where(m => m.MovementDate >= start && m.MovementDate < end);
        if (customerId is int choice && await LinkedCustomerIdAsync(db, choice) is var id)
        {
            query = query.Where(m => m.CustomerId == id);
        }

        if (status is int value && Enum.IsDefined((CylinderStatus)value))
        {
            query = query.Where(m => m.ToStatus == (CylinderStatus)value);
        }

        string term = (search ?? string.Empty).Trim();
        if (term.Length == 0)
        {
            return await query.Take(take).ToListAsync();
        }

        List<CylinderMovement> all = await query.Take(MaxExportRows * 2).ToListAsync();
        return all.Where(m => Contains(m.Cylinder.SerialNumber, term) || Contains(m.Cylinder.Barcode, term) || Contains(m.Customer?.CompanyName, term)
                || Contains(m.VehiclePlate, term) || Contains(m.Description, term) || Contains(m.MovementType, term))
            .Take(take)
            .ToList();
    }

    private static object MovementRow(CylinderMovement m) => new
    {
        CylinderId = m.CylinderId,
        Date = m.MovementDate.ToString("dd.MM.yyyy HH:mm", Turkish),
        Serial = m.Cylinder.SerialNumber,
        m.Cylinder.GasType,
        m.MovementType,
        From = CylinderTracking.StatusName((int)m.FromStatus),
        To = CylinderTracking.StatusName((int)m.ToStatus),
        Customer = m.Customer?.CompanyName,
        Plate = m.VehiclePlate,
        Fullness = CylinderTracking.FullnessName(m.IsFull),
        Note = m.Description,
        Warning = m.Description?.Contains("Uyarı onaylandı", StringComparison.Ordinal) == true
    };

    // Filters take picker ids; a CRM company that never became a customer matches nothing (-1).
    private static async Task<int> LinkedCustomerIdAsync(ArsanGazDbContext db, int choice)
    {
        if (choice > 0)
        {
            return choice;
        }

        string code = CrudService.CrmCustomerCodePrefix + (-choice);
        return await db.Customers.AsNoTracking().Where(c => c.Code == code).Select(c => (int?)c.Id).FirstOrDefaultAsync() ?? -1;
    }

    private static DateTime? ParseDate(string? value) =>
        DateTime.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime date) ? date : null;

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.CurrentCultureIgnoreCase);

    // Prevents spreadsheet formula injection and keeps separators inside quoted cells.
    private static string CsvCell(string? value)
    {
        string text = (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ");
        if (text.Length > 0 && "=+-@".Contains(text[0]))
        {
            text = "'" + text;
        }

        return text.Contains(';') || text.Contains('"') ? $"\"{text.Replace("\"", "\"\"")}\"" : text;
    }

    private static IResult Asset(string name, string contentType)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebPanel." + name);
        if (stream is null)
        {
            return Results.NotFound();
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        return Results.Text(reader.ReadToEnd(), contentType);
    }
}
