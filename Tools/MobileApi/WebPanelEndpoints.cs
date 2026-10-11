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
internal sealed record PanelEditRequest(string? GasType, decimal Capacity, string? CapacityUnit, DateTime? TestDueDate, bool IsActive);

// Browser panel for inspecting and managing cylinder tracking: /panel (page) and /panel/api/* (data).
internal static class WebPanelEndpoints
{
    private const int MaxMovementRows = 500;
    private const int MaxExportRows = 20_000;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(WebApplication app)
    {
        WebPanelAuth auth = new();

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
                    "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; form-action 'self'; base-uri 'none'";
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
            WebPanelSession? session = auth.Current(context);
            if (session is null)
            {
                return Results.Json(new { error = "Oturum kapalı; yeniden giriş yapın." }, statusCode: StatusCodes.Status401Unauthorized);
            }

            if (!HttpMethods.IsGet(context.Request.Method) && context.Request.Headers[WebPanelAuth.CsrfHeader] != "1")
            {
                return Results.Json(new { error = "Geçersiz istek." }, statusCode: StatusCodes.Status403Forbidden);
            }

            context.Items["panelUser"] = session.User;
            return await next(invocation);
        });

        api.MapGet("/ben", (HttpContext context) => Results.Ok(new { user = context.Items["panelUser"] }));

        api.MapPost("/sifre", async (PanelPasswordRequest request, HttpContext context) =>
        {
            string? error = await auth.ChangePasswordAsync(context, request.Mevcut, request.Yeni, request.YeniTekrar);
            return error is null ? Results.Ok(new { message = "Şifre değiştirildi. Diğer cihazlardaki oturumlar kapatıldı." }) : Results.BadRequest(new { error });
        });

        api.MapGet("/ozet", async () =>
        {
            CylinderOverview overview = await new CrudService().CylinderOverviewAsync();
            await using ArsanGazDbContext db = new();
            DateTime today = DateTime.Today;
            int todayMoves = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.MovementDate >= today);
            int weekMoves = await db.CylinderMovements.AsNoTracking().CountAsync(m => m.MovementDate >= today.AddDays(-6));
            int inactive = await db.Cylinders.AsNoTracking().CountAsync(c => !c.IsActive);
            int testSoon = await db.Cylinders.AsNoTracking().CountAsync(c => c.IsActive && c.TestDueDate != null && c.TestDueDate <= today.AddDays(30));
            List<object> recent = (await MovementQuery(db).Take(10).ToListAsync()).Select(MovementRow).ToList();
            return Results.Ok(new { overview, todayMoves, weekMoves, inactive, testSoon, recent });
        });

        api.MapGet("/tupler", async (string? q, int? durum, string? plaka, int? musteri, string? doluluk, bool? uzun, bool? pasif) =>
        {
            await using ArsanGazDbContext db = new();
            IQueryable<Cylinder> query = db.Cylinders.AsNoTracking().Include(c => c.CurrentCustomer).Where(c => c.IsActive == (pasif != true));
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

            List<Cylinder> rows = await query.OrderBy(c => c.SerialNumber).Take(5000).ToListAsync();
            string term = (q ?? string.Empty).Trim();
            DateTime today = DateTime.Today;
            return Results.Ok(rows
                .Where(c => term.Length == 0 || Contains(c.SerialNumber, term) || Contains(c.Barcode, term) || Contains(c.GasType, term)
                    || Contains(c.CurrentCustomer?.CompanyName, term) || Contains(c.VehiclePlate, term))
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
                    TestOverdue = c.TestDueDate != null && c.TestDueDate < today
                }));
        });

        api.MapGet("/tup/{id:int}", async (int id) =>
        {
            await using ArsanGazDbContext db = new();
            Cylinder? c = await db.Cylinders.AsNoTracking().Include(x => x.CurrentCustomer).FirstOrDefaultAsync(x => x.Id == id);
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
                })
            });
        });

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
        });

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
        });

        api.MapGet("/musteriler", async (string? q) => Results.Ok(await CustomerChoices.SearchAsync(q)));

        api.MapGet("/secenekler", async () => Results.Ok(new
        {
            statuses = CylinderTracking.AllStatuses.Select(s => new { id = s, name = CylinderTracking.StatusName(s) }),
            gasTypes = await new CrudService().KnownGasTypesAsync(),
            plates = await new CrudService().KnownVehiclePlatesAsync(),
            longDays = CylinderTracking.LongAtCustomerDays
        }));

        api.MapPost("/tup/{id:int}/hareket", async (int id, PanelMoveRequest request, HttpContext context) =>
        {
            string note = $"Web panel ({context.Items["panelUser"]})" + (string.IsNullOrWhiteSpace(request.Note) ? string.Empty : ": " + request.Note.Trim());
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
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        });

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
        });

        api.MapPost("/tup/{id:int}/duzenle", async (int id, PanelEditRequest request) =>
        {
            if (string.IsNullOrWhiteSpace(request.GasType) || request.Capacity <= 0)
            {
                return Results.BadRequest(new { error = "Gaz türü zorunludur ve kapasite sıfırdan büyük olmalıdır." });
            }

            await using ArsanGazDbContext db = new();
            Cylinder? cylinder = await db.Cylinders.FirstOrDefaultAsync(c => c.Id == id);
            if (cylinder is null)
            {
                return Results.NotFound(new { error = "Tüp bulunamadı." });
            }

            cylinder.GasType = request.GasType.Trim();
            cylinder.Capacity = request.Capacity;
            cylinder.CapacityUnit = string.IsNullOrWhiteSpace(request.CapacityUnit) ? "Litre" : request.CapacityUnit.Trim();
            cylinder.TestDueDate = request.TestDueDate?.Date;
            cylinder.IsActive = request.IsActive;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = $"{cylinder.SerialNumber} güncellendi." + (request.IsActive ? string.Empty : " Tüp pasife alındı; listelerde görünmez.") });
        });
    }

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
