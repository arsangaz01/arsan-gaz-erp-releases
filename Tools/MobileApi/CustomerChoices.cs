using System.Globalization;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

// Customer picker data shared by the mobile app and the web panel: ERP customers plus CRM companies
// (negative Id until first use; the server creates the ERP customer when one is chosen).
internal static class CustomerChoices
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // generalOnly: Yalçın vb. partner müşterileri gizler (genel Müşteriler sekmesi).
    // partner: yalnız o partner anahtarı (ör. yalcin). İkisi boşsa sefer/teslim seçici gibi hepsi gelir.
    public static async Task<List<object>> SearchAsync(string? query, int take = 50, string? partner = null, bool generalOnly = false)
    {
        int limit = Math.Clamp(take, 1, 200);
        string term = (query ?? string.Empty).Trim();
        string partnerKey = PartnerWorkspace.Normalize(partner);

        await using ArsanGazDbContext db = new();
        IQueryable<Customer> erp = db.Customers.AsNoTracking().Where(c => c.IsActive);

        if (partnerKey.Length > 0)
        {
            erp = erp.Where(c => c.PartnerKey == partnerKey);
        }
        else if (generalOnly)
        {
            erp = erp.Where(c => c.PartnerKey == null || c.PartnerKey == "");
        }

        if (term.Length > 0)
        {
            // DB tarafında filtre — 5000 kaydı belleğe çekip taramak telefonda kilitleniyordu.
            if (ArsanDatabase.IsPostgres)
            {
                string pattern = "%" + EscapeLike(term) + "%";
                erp = erp.Where(c =>
                    EF.Functions.ILike(c.CompanyName, pattern)
                    || EF.Functions.ILike(c.Code, pattern)
                    || (c.City != null && EF.Functions.ILike(c.City, pattern))
                    || (c.Phone != null && EF.Functions.ILike(c.Phone, pattern))
                    || (c.District != null && EF.Functions.ILike(c.District, pattern)));
            }
            else
            {
                string lower = term.ToLowerInvariant();
                erp = erp.Where(c =>
                    c.CompanyName.ToLower().Contains(lower)
                    || c.Code.ToLower().Contains(lower)
                    || (c.City != null && c.City.ToLower().Contains(lower))
                    || (c.Phone != null && c.Phone.ToLower().Contains(lower)));
            }
        }

        // Önce ada göre (Türkçe); tüp sayısı sonradan sıralamada kullanılır.
        List<Customer> customers = await erp
            .OrderBy(c => c.CompanyName)
            .Take(limit)
            .ToListAsync();

        // CRM adayları: yalnız hepsi aramada, ERP az sonuç verdiyse (ağır tam liste yok).
        if (partnerKey.Length == 0 && !generalOnly && term.Length > 0 && customers.Count < Math.Min(15, limit))
        {
            foreach (Customer crm in await CrmMatchesAsync(term, limit - customers.Count))
            {
                customers.Add(crm);
            }
        }

        HashSet<int> ids = customers.Where(c => c.Id > 0).Select(c => c.Id).ToHashSet();
        Dictionary<int, int> counts = ids.Count == 0
            ? []
            : await db.Cylinders.AsNoTracking()
                .Where(y => y.IsActive && y.Status == CylinderStatus.Customer
                    && y.CurrentCustomerId != null && ids.Contains(y.CurrentCustomerId.Value))
                .GroupBy(y => y.CurrentCustomerId!.Value)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count);

        return customers
            .OrderByDescending(c => counts.GetValueOrDefault(c.Id) > 0)
            .ThenBy(c => c.Id < 0)
            .ThenBy(c => c.CompanyName, StringComparer.Create(Turkish, true))
            .Take(limit)
            .Select(c => (object)new
            {
                c.Id,
                c.Code,
                Name = c.CompanyName,
                c.City,
                c.Phone,
                IsActive = true,
                Cylinders = counts.GetValueOrDefault(c.Id),
                FromCrm = c.Id < 0,
                PartnerKey = c.PartnerKey
            })
            .ToList();
    }

    private static async Task<List<Customer>> CrmMatchesAsync(string term, int take)
    {
        if (take <= 0)
        {
            return [];
        }

        try
        {
            await using ArsanGazDbContext erp = new();
            HashSet<int> linked = (await erp.Customers.AsNoTracking()
                    .Where(c => c.Code.StartsWith(CrudService.CrmCustomerCodePrefix))
                    .Select(c => c.Code)
                    .ToListAsync())
                .Select(code => CrudService.CrmIdFromCode(code) ?? -1)
                .Where(id => id > 0)
                .ToHashSet();

            await using CrmDbContext crm = new();
            string lower = term.ToLowerInvariant();
            List<CrmCompany> firms = await crm.CrmCompanies.AsNoTracking()
                .Where(x => x.CompanyName != null && x.CompanyName.ToLower().Contains(lower))
                .OrderBy(x => x.CompanyName)
                .Take(take + linked.Count)
                .ToListAsync();

            return firms
                .Where(x => !linked.Contains(x.Id) && !string.IsNullOrWhiteSpace(x.CompanyName))
                .Take(take)
                .Select(x => new Customer
                {
                    Id = -x.Id,
                    Code = CrudService.CrmCustomerCodePrefix + x.Id,
                    CompanyName = x.CompanyName.Trim(),
                    Phone = x.Phone,
                    Email = x.Email,
                    City = x.City,
                    Address = x.Address
                })
                .ToList();
        }
        catch (Exception exception) when (exception is System.Data.Common.DbException or InvalidOperationException)
        {
            return [];
        }
    }

    // Shared by POST /api/musteri (phone) and /panel/api/musteri (browser). 409 carries the existing record so the
    // client can offer to pick it instead.
    public static async Task<IResult> CreateAsync(CustomerCreateRequest request, string source)
    {
        try
        {
            Customer customer = await new CrudService().CreateCustomerAsync(new Customer
            {
                CompanyName = request.Name ?? string.Empty,
                ContactName = request.ContactName,
                Phone = request.Phone,
                Email = request.Email,
                City = request.City,
                District = request.District,
                Address = request.Address,
                TaxOffice = request.TaxOffice,
                TaxNumber = request.TaxNumber
            }, request.AllowDuplicate);
            MobileLog.Write($"Yeni müşteri oluşturuldu: {customer.Code} {customer.CompanyName} ({source})");
            return Results.Ok(new { message = $"{customer.CompanyName} müşteri olarak kaydedildi (kod {customer.Code}).", customer = Item(customer) });
        }
        catch (CustomerDuplicateException duplicate)
        {
            return Results.Json(new { error = duplicate.Message, duplicate = true, existing = Item(duplicate.Existing) }, statusCode: StatusCodes.Status409Conflict);
        }
        catch (ArgumentException exception)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
        catch (DbUpdateException)
        {
            return Results.BadRequest(new { error = "Müşteri kaydedilemedi; kod başka bir kayıtta kullanılıyor olabilir." });
        }
    }

    private static object Item(Customer c) => new
    {
        c.Id,
        c.Code,
        Name = c.CompanyName,
        c.City,
        c.Phone,
        IsActive = true,
        Cylinders = 0,
        FromCrm = c.Id < 0
    };

    // "depoda" or "<customer> müşterisinde" for the registration confirmation.
    public static async Task<string> PlaceTextAsync(Cylinder cylinder)
    {
        if (cylinder.CurrentCustomerId is not int customerId)
        {
            return "depoda";
        }

        await using ArsanGazDbContext db = new();
        string? name = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.CompanyName).FirstOrDefaultAsync();
        return $"{name} müşterisinde";
    }

    private static string EscapeLike(string term) =>
        term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal);

    private static bool Contains(string? value, string term) =>
        value is not null && term.Length > 0
            && Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;
}
