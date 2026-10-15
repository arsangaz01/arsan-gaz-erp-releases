using System.Globalization;
using System.Reflection;
using System.Text;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record PanelLoginRequest(string? Kullanici, string? Sifre);
internal sealed record PanelPasswordRequest(string? Mevcut, string? Yeni, string? YeniTekrar);
internal sealed record PanelMoveRequest(CylinderStatus Status, int? CustomerId, string? Plate, bool? IsFull, string? Note, bool Confirm = false, uint? Version = null);
internal sealed record VehicleDeleteRequest(string? Onay, uint? Version = null);
internal sealed record PanelEditRequest(string? GasType, decimal Capacity, string? CapacityUnit, DateTime? TestDueDate, bool IsActive, uint? Version = null);
internal sealed record PanelUserCreateRequest(string? KullaniciAdi, string? AdSoyad, string? Rol, string? Sifre, bool SifreDegistirsin = true, List<string>? Yetkiler = null, string? SabitPlaka = null);
internal sealed record PanelUserUpdateRequest(string? AdSoyad, string? Rol, bool Aktif, List<string>? Yetkiler = null, string? SabitPlaka = null);
internal sealed record PanelTripLoadRequest(string? Plate, List<string?>? Codes);
internal sealed record PanelTripReasonRequest(List<int>? CylinderIds, string? Reason, string? Note);
internal sealed record PanelTripCheckRequest(List<int>? CylinderIds, bool Undo);
internal sealed record PanelUserPasswordRequest(string? Sifre, bool SifreDegistirsin = true);
internal sealed record PanelUserDeleteRequest(string? Onay);

// Browser panel for inspecting and managing cylinder tracking: /panel (page) and /panel/api/* (data).
internal static class WebPanelEndpoints
{
    private const int MaxMovementRows = 500;
    private const int MaxExportRows = 20_000;
    private const int MaxCylinderRows = 5000;
    private const int MaxDetailMovements = 500;
    private const int TestSoonDays = 30;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(WebApplication app)
    {
        WebPanelAuth auth = app.Services.GetRequiredService<WebPanelAuth>();

        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments("/panel") || context.Request.Path.StartsWithSegments("/musteri"))
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

        app.MapGet("/", (HttpContext context) => Results.Redirect(context.Request.PathBase + "/panel"));
        app.MapGet("/panel", (HttpContext context) => Asset("panel.html", "text/html; charset=utf-8", context));
        app.MapGet("/panel/app.js", (HttpContext context) => Asset("panel.js", "text/javascript; charset=utf-8", context));

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

            context.Items["panelUser"] = user.UserName;
            context.Items["panelAccount"] = user;
            if (user.Role != PanelRoles.Admin && (context.Request.Path.Value ?? string.Empty).Contains("/panel/api/yonetim", StringComparison.OrdinalIgnoreCase))
            {
                return Results.Json(new { error = "Yönetim bölümü yalnızca Yönetici içindir." }, statusCode: StatusCodes.Status403Forbidden);
            }

            return PermissionEndpointExtensions.Check(context, user) ?? await next(invocation);
        });

        api.MapGet("/ben", (HttpContext context) =>
        {
            WebPanelUser user = Account(context);
            return Results.Ok(new
            {
                user = user.UserName,
                displayName = user.DisplayName,
                role = user.Role,
                permissions = Perm.Effective(user).Order(StringComparer.Ordinal).ToArray(),
                mustChangePassword = user.MustChangePassword,
                features = Features.Catalog.Where(f => Features.IsOn(f.Key)).Select(f => f.Key).ToArray(),
                sessionMinutes = (int)WebPanelAuth.SessionLifetime.TotalMinutes,
                roles = PanelRoles.All.Select(r => new { name = r, description = PanelRoles.Description(r) })
            });
        }).SignedIn(beforePasswordChange: true);

        api.MapPost("/sifre", async (PanelPasswordRequest request, HttpContext context) =>
        {
            string? error = await auth.ChangePasswordAsync(context, request.Mevcut, request.Yeni, request.YeniTekrar);
            return error is null ? Results.Ok(new { message = "Şifre değiştirildi. Diğer cihazlardaki oturumlar kapatıldı." }) : Results.BadRequest(new { error });
        }).SignedIn(beforePasswordChange: true);

        api.MapGet("/yonetim/yetkiler", () => Results.Ok(new
        {
            groups = Perm.Catalog.Select(g => new
            {
                name = g.Name,
                permissions = g.Permissions.Select(p => new { key = p.Key, label = p.Label, description = p.Description, requires = p.Requires })
            }),
            roles = PanelRoles.All.Select(r => new { name = r, description = PanelRoles.Description(r), defaults = Perm.RoleDefaults(r) }),
            adminChosen = Perm.AdminChosen,
            adminChosenRole = PanelRoles.Operator
        })).Requires(Perm.ManageUsers);

        api.MapGet("/yonetim/kullanicilar", (HttpContext context, DeviceStore devices) =>
        {
            int self = Account(context).Id;
            IReadOnlyList<MobileSession> phones = devices.All();
            return Results.Ok(WebPanelUserStore.All()
                .Where(u => u.Role != PanelRoles.Customer)
                .OrderByDescending(u => u.IsActive)
                .ThenBy(u => Array.IndexOf(PanelRoles.All, u.Role))
                .ThenBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
                .Select(u =>
                {
                    Dictionary<string, bool> diff = u.Role == PanelRoles.Admin ? [] : Perm.Diff(u.Role, Perm.Assigned(u));
                    return new
                    {
                        u.Id,
                        u.UserName,
                        u.DisplayName,
                        u.Role,
                        u.IsActive,
                        u.MustChangePassword,
                        u.FixedPlate,
                        Created = LocalText(u.CreatedAt),
                        u.CreatedBy,
                        LastLogin = u.LastLoginAt is DateTime last ? LocalText(last) : null,
                        u.LastLoginIp,
                        PasswordChanged = u.PasswordChangedAt is DateTime changed ? LocalText(changed) : null,
                        Sessions = auth.ActiveSessionCount(u.Id),
                        Phones = phones.Count(p => p.UserId == u.Id),
                        Permissions = Perm.Assigned(u).Order(StringComparer.Ordinal).ToArray(),
                        Granted = diff.Where(p => p.Value).Select(p => p.Key).ToArray(),
                        Revoked = diff.Where(p => !p.Value).Select(p => p.Key).ToArray(),
                        IsSelf = u.Id == self
                    };
                }));
        }).Requires(Perm.ManageUsers);

        api.MapPost("/yonetim/kullanici", async (PanelUserCreateRequest request, HttpContext context) =>
        {
            string? error = await FixedPlateProblemAsync(request.SabitPlaka, null)
                ?? auth.CreateUser(Account(context), WebPanelAuth.ClientIp(context), request.KullaniciAdi, request.AdSoyad, request.Rol, request.Sifre, request.SifreDegistirsin, request.Yetkiler, request.SabitPlaka);
            return error is null ? Results.Ok(new { message = $"{request.KullaniciAdi?.Trim()} kullanıcısı oluşturuldu." }) : Results.BadRequest(new { error });
        }).Requires(Perm.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}", async (int id, PanelUserUpdateRequest request, HttpContext context) =>
        {
            string? error = await FixedPlateProblemAsync(request.SabitPlaka, WebPanelUserStore.Find(id)?.FixedPlate)
                ?? auth.UpdateUser(Account(context), WebPanelAuth.ClientIp(context), id, request.AdSoyad, request.Rol, request.Aktif, request.Yetkiler, request.SabitPlaka);
            return error is null ? Results.Ok(new { message = "Kullanıcı güncellendi. Yetkileri veya durumu değiştiyse açık oturumları yeni yetkilerle devam eder ya da kapatılır." }) : Results.BadRequest(new { error });
        }).Requires(Perm.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}/sifre", (int id, PanelUserPasswordRequest request, HttpContext context) =>
        {
            string? error = auth.ResetPassword(Account(context), WebPanelAuth.ClientIp(context), id, request.Sifre, request.SifreDegistirsin);
            return error is null
                ? Results.Ok(new { message = "Şifre sıfırlandı; kullanıcının açık oturumları kapatıldı." + (request.SifreDegistirsin ? " İlk girişte yeni şifre belirleyecek." : string.Empty) })
                : Results.BadRequest(new { error });
        }).Requires(Perm.ManageUsers);

        api.MapPost("/yonetim/kullanici/{id:int}/sil", (int id, PanelUserDeleteRequest request, HttpContext context, DeviceStore devices) =>
        {
            string? error = auth.DeleteUser(Account(context), WebPanelAuth.ClientIp(context), id, request.Onay, devices, out string summary);
            return error is null
                ? Results.Ok(new { message = $"{request.Onay?.Trim()} kullanıcısı kalıcı olarak silindi; {summary}. Geçmiş kayıtlardaki adı korunur." })
                : Results.BadRequest(new { error });
        }).Requires(Perm.ManageUsers);

        api.MapGet("/yonetim/denetim", () => Results.Ok(WebPanelAudit.Latest(200).Select(e => new
        {
            At = LocalText(e.At),
            e.Actor,
            e.Action,
            e.Target,
            e.Detail,
            e.Ip
        }))).Requires(Perm.AuditLog);

        api.MapGet("/yonetim/cihazlar", (DeviceStore devices) => Results.Ok(new
        {
            hours = (int)DeviceStore.SessionLifetime.TotalHours,
            devices = devices.All().OrderByDescending(d => d.CreatedUtc).Select(d => new
            {
                d.Id,
                d.Name,
                d.UserName,
                DisplayName = WebPanelUserStore.Find(d.UserId)?.DisplayName ?? d.UserName,
                Created = LocalText(d.CreatedUtc),
                Expires = LocalText(d.ExpiresUtc),
                LastSeen = d.LastSeenUtc is DateTime seen ? LocalText(seen) : null,
                d.Ip
            })
        })).Requires(Perm.ManageDevices);

        api.MapPost("/yonetim/cihaz/{id}/sil", (string id, DeviceStore devices, HttpContext context) =>
        {
            if (devices.Remove(id) is not MobileSession removed)
            {
                return Results.NotFound(new { error = "Cihaz oturumu bulunamadı (süresi dolmuş olabilir)." });
            }

            WebPanelAuth.Notify(Account(context), WebPanelAuth.ClientIp(context), "Mobil oturum kapatıldı", $"{removed.UserName} – {removed.Name}", "telefon yeniden giriş yapmalı");
            return Results.Ok(new { message = $"{removed.UserName} kullanıcısının {removed.Name} oturumu kapatıldı; telefon yeniden kullanıcı adı ve şifreyle giriş yapmalı." });
        }).Requires(Perm.ManageDevices);

        api.MapPost("/musteri", async (CustomerCreateRequest request, HttpContext context) =>
        {
            IResult result = await CustomerChoices.CreateAsync(request, "web panel: " + context.Items["panelUser"]);
            if (result is IStatusCodeHttpResult { StatusCode: 200 })
            {
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Müşteri oluşturuldu", request.Name?.Trim(), ip: WebPanelAuth.ClientIp(context));
            }

            return result;
        }).Requires(Perm.CustomerAdd);

        api.MapGet("/ozet", async () =>
        {
            CylinderOverview overview = await new CrudService().CylinderOverviewAsync();
            await using TupTakipDbContext db = new();
            DateTime today = DateTime.Today;
            int todayMoves = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.MovementDate >= today);
            int weekMoves = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.MovementDate >= today.AddDays(-6));
            int inactive = await db.Cylinders.AsNoTracking().CountAsync(c => !c.IsActive && !c.NeedsReview);
            int review = await db.Cylinders.AsNoTracking().CountAsync(c => c.NeedsReview);
            int testOverdue = await db.Cylinders.AsNoTracking().CountAsync(c => c.IsActive && c.TestDueDate != null && c.TestDueDate < today);
            int testSoon = await db.Cylinders.AsNoTracking().CountAsync(c => c.IsActive && c.TestDueDate != null && c.TestDueDate >= today && c.TestDueDate <= today.AddDays(TestSoonDays));
            List<object> recent = (await MovementQuery(db).Take(10).ToListAsync()).Select(MovementRow).ToList();
            return Results.Ok(new { overview, todayMoves, weekMoves, inactive, review, testOverdue, testSoon, testSoonDays = TestSoonDays, recent });
        }).Requires(Perm.Summary);

        api.MapGet("/tupler", async (string? q, int? durum, string? plaka, int? musteri, string? doluluk, bool? uzun, bool? pasif, bool? inceleme, string? test) =>
        {
            await using TupTakipDbContext db = new();
            IQueryable<Cylinder> query = db.Cylinders.AsNoTracking().Include(c => c.CurrentCustomer);
            query = inceleme == true ? query.Where(c => c.NeedsReview) : query.Where(c => c.IsActive == (pasif != true));
            if (durum is int status && Enum.IsDefined((CylinderStatus)status))
            {
                query = query.Where(c => c.Status == (CylinderStatus)status);
            }

            if (!string.IsNullOrWhiteSpace(plaka))
            {
                query = query.Where(c => c.VehiclePlate == plaka.Trim());
            }

            if (musteri is int customerId)
            {
                query = query.Where(c => c.CurrentCustomerId == customerId && c.Status == CylinderStatus.Customer);
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

            DateTime testToday = DateTime.Today;
            DateTime testSoon = testToday.AddDays(TestSoonDays);
            query = test switch
            {
                "gecmis" => query.Where(c => c.TestDueDate != null && c.TestDueDate < testToday),
                "yakin" => query.Where(c => c.TestDueDate != null && c.TestDueDate >= testToday && c.TestDueDate <= testSoon),
                "yok" => query.Where(c => c.TestDueDate == null),
                _ => query
            };

            string term = (q ?? string.Empty).Trim();
            List<Cylinder> rows = await (term.Length == 0 ? query.OrderBy(c => c.SerialNumber).Take(MaxCylinderRows) : query.OrderBy(c => c.SerialNumber)).ToListAsync();
            DateTime today = DateTime.Today;
            return Results.Ok(rows
                .Where(c => term.Length == 0 || Contains(c.SerialNumber, term) || Contains(c.Barcode, term) || Contains(c.ManufacturerSerial, term) || Contains(c.GasType, term)
                    || Contains(c.CurrentCustomer?.CompanyName, term) || Contains(c.VehiclePlate, term))
                .Take(MaxCylinderRows)
                .Select(c => new
                {
                    c.Id,
                    c.SerialNumber,
                    c.Barcode,
                    c.ManufacturerSerial,
                    c.NeedsReview,
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
                    TestOverdue = c.TestDueDate != null && c.TestDueDate < today
                }));
        }).Requires(Perm.CylinderView);

        api.MapGet("/tup/{id:int}", async (int id) =>
        {
            await using TupTakipDbContext db = new();
            Cylinder? c = await db.Cylinders.AsNoTracking().Include(x => x.CurrentCustomer).Include(x => x.OwnerCustomer).FirstOrDefaultAsync(x => x.Id == id);
            if (c is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            int movementCount = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.CylinderId == id);
            List<CylinderMovement> movements = await db.CylinderMovements.AsNoTracking().Include(m => m.Customer)
                .Where(m => m.CylinderId == id).OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id).Take(MaxDetailMovements).ToListAsync();
            return Results.Ok(new
            {
                c.Id,
                c.SerialNumber,
                c.Barcode,
                c.ManufacturerSerial,
                Owner = c.OwnerCustomer?.CompanyName,
                c.ProductName,
                ManufactureDate = c.ManufactureDate?.ToString("dd.MM.yyyy", Turkish),
                LastTestDate = c.LastTestDate?.ToString("dd.MM.yyyy", Turkish),
                Legacy = c.LegacyId is null ? null : $"{c.LegacySource} #{c.LegacyId}",
                c.NeedsReview,
                c.ReviewNote,
                MovementCount = movementCount,
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
                c.Version,
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
                })
            });
        }).Requires(Perm.CylinderView);

        api.MapGet("/musteri-stok", async (string? q, string? gaz, bool? uzun, HttpContext context) =>
        {
            DateTime today = DateTime.Today;
            int longDays = CylinderTracking.LongAtCustomerDays;
            bool phones = Perm.Has(Account(context), Perm.CustomerView);
            bool totals = Perm.Has(Account(context), Perm.CustomerStockTotals);
            (List<CustomerStockCylinder> stock, var rows) = await StockCustomerRowsAsync(q, gaz, uzun, today, longDays);
            HashSet<int> shown = [.. rows.Select(x => x.Customer.Id)];
            return Results.Ok(new
            {
                longDays,
                customers = rows.Count,
                totals,
                cylinders = totals ? rows.Sum(x => x.Totals.Count) : (int?)null,
                full = totals ? rows.Sum(x => x.Totals.Full) : (int?)null,
                empty = totals ? rows.Sum(x => x.Totals.Empty) : (int?)null,
                longCount = totals ? rows.Sum(x => x.Totals.Long) : (int?)null,
                gasTotals = totals ? CustomerStock.Groups(stock.Where(c => shown.Contains(c.CustomerId)), today, longDays) : null,
                rows = rows.Select(x => new
                {
                    x.Customer.Id,
                    Name = x.Customer.CompanyName,
                    x.Customer.Code,
                    x.Customer.City,
                    x.Customer.District,
                    Phone = phones ? x.Customer.Phone : null,
                    x.Totals.Count,
                    x.Totals.Full,
                    x.Totals.Empty,
                    x.Totals.CustomerOwned,
                    x.Totals.MaxDays,
                    x.Totals.AvgDays,
                    x.Totals.Long,
                    LastDelivery = x.Totals.LastDelivery?.ToString("dd.MM.yyyy", Turkish),
                    Gases = CustomerStock.GroupText(x.Groups)
                })
            });
        }).Requires(Perm.CylinderView);

        // Every cylinder of one gas type and size held by the customers that match the stock filter, with a per-customer breakdown.
        api.MapGet("/musteri-stok/grup", async (string? q, string? gaz, bool? uzun, string? grupGaz, string? boyut) =>
        {
            DateTime today = DateTime.Today;
            int longDays = CylinderTracking.LongAtCustomerDays;
            string gas = (grupGaz ?? string.Empty).Trim();
            string size = (boyut ?? string.Empty).Trim();
            (List<CustomerStockCylinder> stock, var rows) = await StockCustomerRowsAsync(q, gaz, uzun, today, longDays);
            Dictionary<int, Customer> customers = rows.ToDictionary(x => x.Customer.Id, x => x.Customer);
            List<CustomerStockCylinder> group = stock
                .Where(c => customers.ContainsKey(c.CustomerId)
                    && string.Equals(c.GasType.Trim(), gas, StringComparison.CurrentCultureIgnoreCase)
                    && CustomerStock.CapacityText(c.Capacity, c.CapacityUnit) == size)
                .ToList();
            return Results.Ok(new
            {
                gasType = gas,
                capacity = size,
                longDays,
                totals = CustomerStock.Totals(group, today, longDays),
                customers = group.GroupBy(c => c.CustomerId)
                    .Select(g => (Customer: customers[g.Key], Totals: CustomerStock.Totals(g.ToList(), today, longDays)))
                    .OrderByDescending(x => x.Totals.Count)
                    .ThenBy(x => x.Customer.CompanyName, StringComparer.Create(Turkish, true))
                    .Select(x => new
                    {
                        x.Customer.Id,
                        Name = x.Customer.CompanyName,
                        x.Customer.Code,
                        x.Customer.City,
                        x.Customer.District,
                        x.Totals.Count,
                        x.Totals.Full,
                        x.Totals.Empty,
                        x.Totals.MaxDays,
                        x.Totals.Long
                    }),
                cylinders = group
                    .OrderByDescending(c => CylinderTracking.DaysSince(c.Since, today) ?? -1)
                    .Select(c => new { row = StockRow(c, today), customerId = c.CustomerId, customer = customers[c.CustomerId].CompanyName })
            });
        }).Requires(Perm.CustomerStockTotals);

        api.MapGet("/musteri/{id:int}/stok", (int id, HttpContext context) => CustomerStockAsync(id, Perm.Has(Account(context), Perm.CustomerView)))
            .Requires(Perm.CylinderView);

        api.MapGet("/musteri-stok.csv", async (int? musteri, string? gaz, HttpContext context) =>
        {
            if (musteri is null && !Perm.Has(Account(context), Perm.CustomerStockTotals))
            {
                return Results.Json(new { error = "Tüm müşterilerin stoğunu aktarma yetkiniz yok; bir müşteriyi açıp o müşterinin stoğunu aktarabilirsiniz." }, statusCode: 403);
            }

            DateTime today = DateTime.Today;
            List<CustomerStockCylinder> stock = FilterGas(await CustomerStock.LoadAsync(musteri), gaz);
            Dictionary<int, Customer> customers = await StockCustomersAsync(stock.Select(c => c.CustomerId));
            StringBuilder csv = new();
            csv.AppendLine("Müşteri;Müşteri kodu;Şehir;İlçe;Seri no;Barkod / QR;Üretici seri no;Gaz;Kapasite;Dolu/Boş;Teslim tarihi;Gün;Müşteri malı;Test tarihi");
            foreach (CustomerStockCylinder c in stock
                .Where(c => customers.ContainsKey(c.CustomerId))
                .OrderBy(c => customers[c.CustomerId].CompanyName, StringComparer.Create(Turkish, true))
                .ThenBy(c => c.GasType, StringComparer.Create(Turkish, true))
                .ThenBy(c => c.Since))
            {
                Customer customer = customers[c.CustomerId];
                csv.AppendLine(string.Join(';', new[]
                {
                    customer.CompanyName, customer.Code, customer.City, customer.District, c.SerialNumber, c.Barcode, c.ManufacturerSerial, c.GasType,
                    CustomerStock.CapacityText(c.Capacity, c.CapacityUnit), CylinderTracking.FullnessName(c.IsFull), c.Since?.ToString("dd.MM.yyyy", Turkish),
                    CylinderTracking.DaysSince(c.Since, today)?.ToString(Turkish), c.CustomerOwned ? "Evet" : string.Empty, c.TestDueDate?.ToString("dd.MM.yyyy", Turkish)
                }.Select(CsvCell)));
            }

            byte[] bytes = [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(csv.ToString())];
            string name = musteri is int one && customers.TryGetValue(one, out Customer? only) ? "musteri-" + only.Code : "musteri-stok";
            return Results.File(bytes, "text/csv; charset=utf-8", $"{name}-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        }).Requires(Perm.Export);

        api.MapGet("/hareketler", async (string? bas, string? bit, int? musteri, int? durum, string? q) =>
        {
            await using TupTakipDbContext db = new();
            List<CylinderMovement> rows = await FilteredMovementsAsync(db, bas, bit, musteri, durum, q, MaxMovementRows + 1);
            return Results.Ok(new
            {
                rows = rows.Take(MaxMovementRows).Select(MovementRow),
                truncated = rows.Count > MaxMovementRows,
                limit = MaxMovementRows
            });
        }).Requires(Perm.MovementHistory);

        api.MapGet("/hareketler.csv", async (string? bas, string? bit, int? musteri, int? durum, string? q) =>
        {
            await using TupTakipDbContext db = new();
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
        }).Requires(Perm.Export);

        api.MapGet("/seferler", async (string? durum) =>
        {
            bool? active = durum switch { "aktif" => true, "kapali" => false, _ => null };
            return Results.Ok((await new CylinderTripService().TripsAsync(active)).Select(TripRow));
        }).Requires(Perm.TripView);

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
                    c.Pickup
                }),
                customers = Features.IsOn(Features.CustomerExchange) ? detail.Customers ?? [] : [],
                unexpected = detail.UnexpectedReturns.Select(u => new
                {
                    u.CylinderId,
                    u.SerialNumber,
                    u.GasType,
                    u.Plate,
                    At = u.At.ToString("dd.MM.yyyy HH:mm", Turkish)
                })
            });
        }).Requires(Perm.TripView);

        api.MapPost("/dolum", async (FillRequest request, HttpContext context) =>
        {
            try
            {
                string user = (string)context.Items["panelUser"]!;
                FillResult result = await CylinderFills.FillAsync(request.Codes ?? [], request.Lot, request.Note, $"Web panel ({user})", request.AllowOverdueTest);
                if (result.Filled > 0)
                {
                    WebPanelAudit.Write(user, "Dolum kaydı", $"{result.Filled} tüp", CylinderFills.CleanLot(request.Lot) ?? "lot yok", WebPanelAuth.ClientIp(context));
                }

                return Results.Ok(FillEndpoints.Dto(result));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.FillRecord);

        api.MapGet("/dolumlar", async (int? gun, string? q) => Results.Ok((await CylinderFills.ListAsync(gun ?? 30, q)).Select(FillEndpoints.Row)))
            .Requires(Perm.FillRecord);

        api.MapGet("/sefer/{id:int}/yuk", async (int id, string? plaka) => await VehicleEndpoints.TripLoadAsync(id, plaka)).Requires(Perm.TripView);

        api.MapPost("/sefer/baslat", async (TripStartRequest request, HttpContext context) =>
        {
            try
            {
                (CylinderTrip trip, IReadOnlyList<string?>? plates) = await CylinderTripEndpoints.StartAsync(Account(context), request, $"Web panel ({context.Items["panelUser"]})");
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Sefer başlatıldı", trip.Title, string.Join(", ", plates ?? []), WebPanelAuth.ClientIp(context));
                return Results.Ok(new { trip.Id, trip.Title, message = $"{trip.Title} başlatıldı." });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripStart);

        api.MapPost("/sefer/{id:int}/yukle", async (int id, PanelTripLoadRequest request, HttpContext context) =>
        {
            try
            {
                string? plate = DriverVehicle.PlateFor(Account(context).FixedPlate, request.Plate);
                TripLoadResult result = await new CylinderTripService().LoadCylindersAsync(id, plate, request.Codes ?? [], $"Web panel ({context.Items["panelUser"]})");
                string message = $"{result.Added} tüp {plate} aracına yüklendi." + (result.Skipped > 0 ? $" {result.Skipped} atlandı:\n" + string.Join("\n", result.Messages) : string.Empty);
                return Results.Ok(new { message, result.Added, result.Skipped, result.Messages });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripLoad);

        api.MapPost("/sefer/{id:int}/kapat", async (int id, HttpContext context) =>
        {
            try
            {
                TripCloseResult result = await new CylinderTripService().CloseTripAsync(id, $"Web panel ({context.Items["panelUser"]})");
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
        }).Requires(Perm.TripClose);

        api.MapGet("/sefer-gerekceler", () => Results.Ok(CylinderTripEndpoints.ReasonCatalog())).Requires(Perm.TripView);

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
        }).Requires(CylinderTripEndpoints.ReasonWriters);

        api.MapPost("/sefer/{id:int}/gerekce-kontrol", async (int id, PanelTripCheckRequest request, HttpContext context) =>
        {
            try
            {
                string user = (string)context.Items["panelUser"]!;
                TripReasonResult result = await new CylinderTripService().CheckMissingAsync(id, request.CylinderIds ?? [], $"Web panel ({user})", request.Undo);
                WebPanelAudit.Write(user, request.Undo ? "Sefer eksik kontrolü geri alındı" : "Sefer eksik gerekçesi kontrol edildi", $"Sefer #{id}",
                    $"{result.Updated} tüp", WebPanelAuth.ClientIp(context));
                return Results.Ok(new
                {
                    message = CylinderTripEndpoints.ReasonMessage(result, request.Undo ? "tüpün kontrolü geri alındı." : "tüp kontrol edildi."),
                    result.Updated,
                    result.Messages
                });
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.TripLossCheck);

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
                m.SeenAfterTrip
            }))).Requires(Perm.TripView);

        api.MapGet("/araclar", async (bool? pasif) => Results.Ok((await new VehicleService().ListAsync(pasif == true)).Select(VehicleEndpoints.VehicleRow)))
            .Requires(Perm.VehicleView, Perm.TripStart);

        api.MapGet("/arac/{id:int}", async (int id) =>
        {
            VehicleDetail? detail = await new VehicleService().DetailAsync(id);
            return detail is null ? Results.NotFound(new { error = "Araç bulunamadı." }) : Results.Ok(VehicleEndpoints.DetailDto(detail));
        }).Requires(Perm.VehicleView);

        api.MapPost("/arac", async (VehicleSaveRequest request, HttpContext context) =>
            await VehicleEndpoints.SaveAsync(async () =>
            {
                Vehicle vehicle = await new VehicleService().CreateAsync(request.Input);
                WebPanelAudit.Write((string)context.Items["panelUser"]!, "Araç eklendi", vehicle.Plate, vehicle.Name, WebPanelAuth.ClientIp(context));
                return vehicle;
            }, v => $"{v.Plate} araç olarak kaydedildi.")).Requires(Perm.VehicleAdd);

        api.MapPost("/arac/{id:int}", async (int id, VehicleSaveRequest request, HttpContext context) =>
            await VehicleEndpoints.UpdateAsync(id, request, context, vehicle =>
                WebPanelAudit.Write((string)context.Items["panelUser"]!, vehicle.IsActive ? "Araç güncellendi" : "Araç pasif", vehicle.Plate, null, WebPanelAuth.ClientIp(context))))
            .Requires(Perm.VehicleEdit, Perm.VehicleDeactivate);

        api.MapPost("/arac/{id:int}/sil", async (int id, VehicleDeleteRequest request, HttpContext context) =>
        {
            string user = (string)context.Items["panelUser"]!;
            await using (TupTakipDbContext db = new())
            {
                string? plate = await db.Vehicles.AsNoTracking().Where(x => x.Id == id).Select(x => x.Plate).FirstOrDefaultAsync();
                if (plate is null)
                {
                    return Results.NotFound(new { error = "Araç bulunamadı." });
                }

                static string Compact(string? text) => string.Concat((text ?? string.Empty).Where(c => !char.IsWhiteSpace(c) && c != '-')).ToUpperInvariant();
                if (Compact(request.Onay) != Compact(plate))
                {
                    return Results.BadRequest(new { error = "Onay için aracın plakasını aynen yazın." });
                }
            }

            try
            {
                (string deleted, int moved) = await new VehicleService().DeleteAsync(id, user, request.Version);
                WebPanelAudit.Write(user, "Araç silindi", deleted, moved > 0 ? $"{moved} tüp depoya alındı" : null, WebPanelAuth.ClientIp(context));
                return Results.Ok(new { message = $"{deleted} silindi." + (moved > 0 ? $" Araçtaki {moved} tüp depoya alındı." : string.Empty) });
            }
            catch (InvalidOperationException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.VehicleDelete);

        StockCountEndpoints.MapPanel(api);
        LabelEndpoints.MapPanel(api);
        CustomerPortalEndpoints.MapPanel(api, auth);
        TripPlanEndpoints.MapPanel(api);

        api.MapGet("/musteriler", async (string? q, int? take, HttpContext context) =>
            Results.Ok(await CustomerChoices.SearchAsync(q, Perm.Has(Account(context), Perm.CustomerView), take ?? 50)))
            .Requires(Perm.CustomerPicker);

        api.MapGet("/secenekler", async () => Results.Ok(new
        {
            statuses = CylinderTracking.AllStatuses.Select(s => new { id = s, name = CylinderTracking.StatusName(s) }),
            gasTypes = await new CrudService().KnownGasTypesAsync(),
            plates = await new CrudService().KnownVehiclePlatesAsync(),
            longDays = CylinderTracking.LongAtCustomerDays
        })).SignedIn();

        api.MapPost("/tup/{id:int}/hareket", async (int id, PanelMoveRequest request, HttpContext context) =>
        {
            if (MobileEndpoints.MoveProblem(context, request.Status, request.IsFull, request.Confirm) is IResult denied)
            {
                return denied;
            }

            string note = $"Web panel ({context.Items["panelUser"]})" + (string.IsNullOrWhiteSpace(request.Note) ? string.Empty : ": " + request.Note.Trim());
            try
            {
                string? warning = await new CrudService().MoveCylinderAsync(
                    id, request.Status, request.CustomerId, note, "Web panel hareketi", request.Plate, request.IsFull, request.Confirm, request.Version);
                return Results.Ok(new { message = $"{CylinderTracking.StatusName((int)request.Status)} olarak kaydedildi." + (warning is null ? string.Empty : "\n" + warning) });
            }
            catch (CylinderConflictException conflict)
            {
                return Results.Json(new { error = conflict.Message, conflict = true }, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.AnyMove);

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
        }).Requires(Perm.CylinderRegister);

        // Details need CylinderEdit, the active flag needs CylinderDeactivate; Version guards against overwriting a parallel change.
        api.MapPost("/tup/{id:int}/duzenle", async (int id, PanelEditRequest request, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.GasType) || request.Capacity <= 0)
            {
                return Results.BadRequest(new { error = "Gaz türü zorunludur ve kapasite sıfırdan büyük olmalıdır." });
            }

            await using TupTakipDbContext db = new();
            Cylinder? cylinder = await db.Cylinders.FirstOrDefaultAsync(c => c.Id == id);
            if (cylinder is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            if (request.Version is uint seen && seen != cylinder.Version)
            {
                throw new RecordChangedException();
            }

            string gasType = request.GasType.Trim();
            string unit = string.IsNullOrWhiteSpace(request.CapacityUnit) ? "Litre" : request.CapacityUnit.Trim();
            bool detailsChange = gasType != cylinder.GasType || request.Capacity != cylinder.Capacity || unit != cylinder.CapacityUnit
                || request.TestDueDate?.Date != cylinder.TestDueDate?.Date;
            HashSet<string> mine = Perm.Effective(Account(context));
            if (detailsChange && !mine.Contains(Perm.CylinderEdit))
            {
                return PermissionEndpointExtensions.Denied(Perm.CylinderEdit);
            }

            if (request.IsActive != cylinder.IsActive && !mine.Contains(Perm.CylinderDeactivate))
            {
                return PermissionEndpointExtensions.Denied(Perm.CylinderDeactivate);
            }

            cylinder.GasType = gasType;
            cylinder.Capacity = request.Capacity;
            cylinder.CapacityUnit = unit;
            cylinder.TestDueDate = request.TestDueDate?.Date;
            cylinder.IsActive = request.IsActive;
            if (cylinder.NeedsReview && request.IsActive)
            {
                cylinder.NeedsReview = false;
                cylinder.ReviewNote = $"{cylinder.ReviewNote} · İncelendi ve aktif edildi: {Account(context).UserName} {DateTime.Now:dd.MM.yyyy HH:mm}".Trim(' ', '·');
            }

            await db.SaveChangesAsync();
            WebPanelAudit.Write(Account(context).UserName, "Tüp düzenlendi", cylinder.SerialNumber, request.IsActive ? null : "pasife alındı", WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = $"{cylinder.SerialNumber} güncellendi." + (request.IsActive ? string.Empty : " Tüp pasife alındı; listelerde görünmez.") });
        }).Requires(Perm.CylinderEdit, Perm.CylinderDeactivate);
    }

    private static WebPanelUser Account(HttpContext context) => (WebPanelUser)context.Items["panelAccount"]!;

    // A newly chosen fixed vehicle must be a registered active vehicle; keeping the current one is always allowed.
    private static async Task<string?> FixedPlateProblemAsync(string? requested, string? current)
    {
        string? plate;
        try
        {
            plate = CylinderTracking.NormalizePlate(requested);
        }
        catch (ArgumentException exception)
        {
            return "Sabit araç: " + exception.Message;
        }

        if (plate is null || plate == current)
        {
            return null;
        }

        await using TupTakipDbContext db = new();
        return await db.Vehicles.AsNoTracking().AnyAsync(v => v.Plate == plate && v.IsActive)
            ? null
            : $"Sabit araç: {plate} kayıtlı aktif bir araç değil. Önce Araçlar sekmesinden ekleyin veya aktif edin.";
    }

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
            v.Missing,
            v.PickedUp
        }),
        t.Loaded,
        t.Delivered,
        t.Returned,
        t.OnVehicle,
        t.Missing,
        MissingByGas = t.MissingByGas.Select(g => new { g.GasType, g.Count }),
        t.MissingUnexplained,
        t.MissingUnchecked,
        t.RequireReasons,
        t.PickedUp
    };

    private static string LocalText(DateTime utc) => DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish);

    private static IQueryable<CylinderMovement> MovementQuery(TupTakipDbContext db) =>
        db.CylinderMovements.AsNoTracking().Include(m => m.Cylinder).Include(m => m.Customer)
            .OrderByDescending(m => m.MovementDate).ThenByDescending(m => m.Id);

    private static async Task<List<CylinderMovement>> FilteredMovementsAsync(
        TupTakipDbContext db, string? from, string? to, int? customerId, int? status, string? search, int take)
    {
        DateTime start = ParseDate(from) ?? DateTime.Today.AddDays(-30);
        DateTime end = (ParseDate(to) ?? DateTime.Today).AddDays(1);
        IQueryable<CylinderMovement> query = MovementQuery(db).Where(m => m.MovementDate >= start && m.MovementDate < end);
        if (customerId is int id)
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

    // Shared by the web panel and the phone: one customer's cylinders by gas/size with waiting days and barcodes.
    internal static async Task<IResult> CustomerStockAsync(int id, bool phones)
    {
        await using TupTakipDbContext db = new();
        Customer? customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id);
        if (customer is null)
        {
            return Results.NotFound(new { error = "Müşteri bulunamadı." });
        }

        DateTime today = DateTime.Today;
        int longDays = CylinderTracking.LongAtCustomerDays;
        List<CustomerStockCylinder> stock = await CustomerStock.LoadAsync(id);
        return Results.Ok(new
        {
            customer.Id,
            Name = customer.CompanyName,
            customer.Code,
            customer.City,
            customer.District,
            Phone = phones ? customer.Phone : null,
            customer.IsActive,
            longDays,
            Totals = CustomerStock.Totals(stock, today, longDays),
            Groups = CustomerStock.Groups(stock, today, longDays),
            Cylinders = stock.OrderByDescending(c => CylinderTracking.DaysSince(c.Since, today) ?? -1).Select(c => StockRow(c, today))
        });
    }

    private static List<CustomerStockCylinder> FilterGas(List<CustomerStockCylinder> stock, string? gas)
    {
        string term = (gas ?? string.Empty).Trim();
        return term.Length == 0 ? stock : stock.Where(c => Contains(c.GasType, term)).ToList();
    }

    // The customers listed on the stock tab for the given filter, largest stock first, with the gas filtered stock behind them.
    private static async Task<(List<CustomerStockCylinder> Stock, List<(Customer Customer, CustomerStockTotals Totals, IReadOnlyList<CustomerStockGroup> Groups)> Rows)>
        StockCustomerRowsAsync(string? q, string? gaz, bool? uzun, DateTime today, int longDays)
    {
        List<CustomerStockCylinder> stock = FilterGas(await CustomerStock.LoadAsync(), gaz);
        Dictionary<int, Customer> customers = await StockCustomersAsync(stock.Select(c => c.CustomerId));
        string term = (q ?? string.Empty).Trim();
        var rows = stock.GroupBy(c => c.CustomerId)
            .Where(g => customers.ContainsKey(g.Key))
            .Select(g => (Customer: customers[g.Key], Totals: CustomerStock.Totals(g.ToList(), today, longDays), Groups: CustomerStock.Groups(g, today, longDays)))
            .Where(x => term.Length == 0 || Contains(x.Customer.CompanyName, term) || Contains(x.Customer.Code, term) || Contains(x.Customer.City, term) || Contains(x.Customer.District, term))
            .Where(x => uzun != true || x.Totals.Long > 0)
            .OrderByDescending(x => x.Totals.Count)
            .ThenBy(x => x.Customer.CompanyName, StringComparer.Create(Turkish, true))
            .ToList();
        return (stock, rows);
    }

    private static async Task<Dictionary<int, Customer>> StockCustomersAsync(IEnumerable<int> ids)
    {
        HashSet<int> wanted = [.. ids];
        await using TupTakipDbContext db = new();
        return await db.Customers.AsNoTracking().Where(c => wanted.Contains(c.Id)).ToDictionaryAsync(c => c.Id);
    }

    private static object StockRow(CustomerStockCylinder c, DateTime today) => new
    {
        c.Id,
        c.SerialNumber,
        c.Barcode,
        c.ManufacturerSerial,
        c.GasType,
        Capacity = CustomerStock.CapacityText(c.Capacity, c.CapacityUnit),
        Fullness = CylinderTracking.FullnessName(c.IsFull),
        Since = c.Since?.ToString("dd.MM.yyyy", Turkish),
        Days = CylinderTracking.DaysSince(c.Since, today),
        c.CustomerOwned,
        TestDue = c.TestDueDate?.ToString("dd.MM.yyyy", Turkish),
        TestOverdue = c.TestDueDate != null && c.TestDueDate < today,
        c.NeedsReview
    };

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

    // panel.html refers to {{BASE}} so links and API calls work behind the /tuptakip path prefix.
    internal static IResult Asset(string name, string contentType, HttpContext context)
    {
        using Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("WebPanel." + name);
        if (stream is null)
        {
            return Results.NotFound();
        }

        using StreamReader reader = new(stream, Encoding.UTF8);
        string text = reader.ReadToEnd();
        if (name.EndsWith(".html", StringComparison.Ordinal))
        {
            text = text.Replace("{{BASE}}", System.Net.WebUtility.HtmlEncode(context.Request.PathBase.Value ?? string.Empty), StringComparison.Ordinal);
        }

        return Results.Text(text, contentType);
    }
}
