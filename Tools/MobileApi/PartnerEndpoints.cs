using System.Globalization;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record PartnerAssignRequest(List<int>? CustomerIds, bool Assign = true);

// Yalçın (ve ileride diğer partner) paneli: atama + stok/hareket görünümü. Veri aynı DB; sefer/stok senkron.
internal static class PartnerEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void Map(RouteGroupBuilder api)
    {
        api.MapGet("/yalcin/ozet", async () =>
        {
            await using ArsanGazDbContext db = new();
            string key = PartnerWorkspace.Yalcin;
            int customers = await db.Customers.AsNoTracking().CountAsync(c => c.IsActive && c.PartnerKey == key);
            List<int> ids = await db.Customers.AsNoTracking()
                .Where(c => c.PartnerKey == key)
                .Select(c => c.Id)
                .ToListAsync();
            int cylinders = ids.Count == 0
                ? 0
                : await db.Cylinders.AsNoTracking()
                    .CountAsync(c => c.IsActive && c.Status == CylinderStatus.Customer
                        && c.CurrentCustomerId != null && ids.Contains(c.CurrentCustomerId.Value));
            int movements7 = ids.Count == 0
                ? 0
                : await db.CylinderMovements.AsNoTracking()
                    .CountAsync(m => m.CustomerId != null && ids.Contains(m.CustomerId.Value)
                        && m.MovementDate >= DateTime.UtcNow.AddDays(-7));
            return Results.Ok(new
            {
                partner = key,
                title = PartnerWorkspace.DisplayName(key),
                customers,
                cylindersAtCustomer = cylinders,
                movementsLast7Days = movements7,
                note = "Sefer/teslim/iade aynı süreçte işlenir; Yalçın müşterileri genel Müşteri Stoku listesinde gizlenir."
            });
        }).Requires(PanelPermissions.PartnerYalcinView);

        api.MapGet("/yalcin/musteriler", async (string? q, int? take) =>
            Results.Ok(await CustomerChoices.SearchAsync(q, take ?? 200, partner: PartnerWorkspace.Yalcin)))
            .Requires(PanelPermissions.PartnerYalcinView);

        api.MapGet("/yalcin/musteri-stok", async (string? q, string? gaz, bool? uzun, HttpContext context) =>
        {
            // Reuse panel stock with partner filter via query — call same helper through HTTP-less path.
            DateTime today = DateTime.Today;
            int longDays = CylinderTracking.LongAtCustomerDays;
            bool phones = PanelRoles.Has(WebPanelEndpoints.Account(context), PanelPermissions.CustomerView);
            bool totals = PanelRoles.Has(WebPanelEndpoints.Account(context), PanelPermissions.CustomerStockTotals)
                || PanelRoles.Has(WebPanelEndpoints.Account(context), PanelPermissions.PartnerYalcinView);
            // Delegate to stock endpoint logic by invoking WebPanel stock with partner=yalcin through shared helper.
            return await YalcinStockAsync(q, gaz, uzun, today, longDays, phones, totals);
        }).Requires(PanelPermissions.PartnerYalcinView);

        api.MapGet("/yalcin/hareketler", async (string? q, int? take) =>
        {
            await using ArsanGazDbContext db = new();
            HashSet<int> ids = [.. await db.Customers.AsNoTracking()
                .Where(c => c.PartnerKey == PartnerWorkspace.Yalcin)
                .Select(c => c.Id)
                .ToListAsync()];
            if (ids.Count == 0)
            {
                return Results.Ok(new { rows = Array.Empty<object>(), count = 0 });
            }

            int limit = Math.Clamp(take ?? 100, 1, 500);
            string term = (q ?? string.Empty).Trim();
            var query = db.CylinderMovements.AsNoTracking()
                .Where(m => m.CustomerId != null && ids.Contains(m.CustomerId.Value))
                .OrderByDescending(m => m.MovementDate)
                .ThenByDescending(m => m.Id)
                .Take(limit);
            List<CylinderMovement> moves = await query.ToListAsync();
            Dictionary<int, string> names = await db.Customers.AsNoTracking()
                .Where(c => ids.Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.CompanyName);
            Dictionary<int, string> serials = await db.Cylinders.AsNoTracking()
                .Where(c => moves.Select(m => m.CylinderId).Contains(c.Id))
                .ToDictionaryAsync(c => c.Id, c => c.SerialNumber);
            return Results.Ok(new
            {
                count = moves.Count,
                rows = moves.Select(m => new
                {
                    m.Id,
                    Date = m.MovementDate.ToString("dd.MM.yyyy HH:mm", Turkish),
                    Serial = serials.GetValueOrDefault(m.CylinderId, "?"),
                    Customer = m.CustomerId is int cid ? names.GetValueOrDefault(cid, "?") : "-",
                    m.MovementType,
                    m.VehiclePlate,
                    m.Description
                }).Where(r => term.Length == 0
                    || (r.Serial?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || (r.Customer?.Contains(term, StringComparison.CurrentCultureIgnoreCase) ?? false))
            });
        }).Requires(PanelPermissions.PartnerYalcinView);

        api.MapPost("/yalcin/ata", async (PartnerAssignRequest request, HttpContext context) =>
        {
            WebPanelUser user = WebPanelEndpoints.Account(context);
            if (!PanelRoles.Has(user, PanelPermissions.PartnerYalcinManage))
            {
                return Results.Json(new { error = "Yalçın'a müşteri atama yalnızca Yöneticide." }, statusCode: 403);
            }

            List<int> ids = (request.CustomerIds ?? []).Where(id => id > 0).Distinct().ToList();
            if (ids.Count == 0)
            {
                return Results.BadRequest(new { error = "En az bir müşteri seçin." });
            }

            await using ArsanGazDbContext db = new();
            List<Customer> customers = await db.Customers.Where(c => ids.Contains(c.Id)).ToListAsync();
            if (customers.Count == 0)
            {
                return Results.NotFound(new { error = "Müşteri bulunamadı." });
            }

            string? key = request.Assign ? PartnerWorkspace.Yalcin : null;
            foreach (Customer customer in customers)
            {
                customer.PartnerKey = key;
                customer.UpdatedAt = DateTime.UtcNow;
            }

            await db.SaveChangesAsync();
            MobileLog.Write($"Yalçın {(request.Assign ? "atama" : "çıkarma")}: {customers.Count} müşteri ({user.UserName})");
            return Results.Ok(new
            {
                message = request.Assign
                    ? $"{customers.Count} müşteri Yalçın'a aktarıldı; genel listelerde gizlenir, sefer/stok senkron devam eder."
                    : $"{customers.Count} müşteri Yalçın'dan çıkarıldı; genel listelere döner.",
                count = customers.Count,
                partner = key
            });
        }).Requires(PanelPermissions.PartnerYalcinManage);

        api.MapGet("/yalcin/adaylar", async (string? q, int? take) =>
            Results.Ok(await CustomerChoices.SearchAsync(q, take ?? 100, generalOnly: true)))
            .Requires(PanelPermissions.PartnerYalcinManage);
    }

    private static async Task<IResult> YalcinStockAsync(
        string? q, string? gaz, bool? uzun, DateTime today, int longDays, bool phones, bool totals)
    {
        // Mirror /musteri-stok response shape with partner=yalcin.
        await using ArsanGazDbContext db = new();
        List<CustomerStockCylinder> allStock = await CustomerStock.LoadAsync();
        if (!string.IsNullOrWhiteSpace(gaz))
        {
            string g = gaz.Trim();
            allStock = allStock.Where(c => c.GasType.Contains(g, StringComparison.CurrentCultureIgnoreCase)).ToList();
        }

        HashSet<int> partnerIds = [.. await db.Customers.AsNoTracking()
            .Where(c => c.PartnerKey == PartnerWorkspace.Yalcin)
            .Select(c => c.Id)
            .ToListAsync()];
        allStock = allStock.Where(c => partnerIds.Contains(c.CustomerId)).ToList();
        Dictionary<int, Customer> customers = await db.Customers.AsNoTracking()
            .Where(c => partnerIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id);
        string term = (q ?? string.Empty).Trim();
        bool Match(Customer c) => term.Length == 0
            || c.CompanyName.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || c.Code.Contains(term, StringComparison.CurrentCultureIgnoreCase)
            || (c.City != null && c.City.Contains(term, StringComparison.CurrentCultureIgnoreCase));

        var rows = allStock.GroupBy(c => c.CustomerId)
            .Where(g => customers.ContainsKey(g.Key))
            .Select(g => (
                Customer: customers[g.Key],
                Totals: CustomerStock.Totals(g.ToList(), today, longDays),
                Groups: CustomerStock.Groups(g, today, longDays)))
            .Where(x => Match(x.Customer))
            .Where(x => uzun != true || x.Totals.Long > 0)
            .OrderByDescending(x => x.Totals.Count)
            .ThenBy(x => x.Customer.CompanyName, StringComparer.Create(Turkish, true))
            .ToList();
        HashSet<int> withStock = [.. rows.Select(x => x.Customer.Id)];
        var withoutStock = customers.Values
            .Where(c => c.IsActive && !withStock.Contains(c.Id) && Match(c))
            .OrderBy(c => c.CompanyName, StringComparer.Create(Turkish, true))
            .Select(c => new
            {
                c.Id,
                Name = c.CompanyName,
                c.Code,
                c.City,
                c.District,
                Phone = phones ? c.Phone : null,
                Count = 0
            })
            .ToList();
        return Results.Ok(new
        {
            partner = PartnerWorkspace.Yalcin,
            longDays,
            customers = rows.Count + withoutStock.Count,
            withStock = rows.Count,
            withoutStockCount = withoutStock.Count,
            totals,
            cylinders = totals ? rows.Sum(x => x.Totals.Count) : (int?)null,
            full = totals ? rows.Sum(x => x.Totals.Full) : (int?)null,
            empty = totals ? rows.Sum(x => x.Totals.Empty) : (int?)null,
            longCount = totals ? rows.Sum(x => x.Totals.Long) : (int?)null,
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
            }),
            withoutStock
        });
    }
}
