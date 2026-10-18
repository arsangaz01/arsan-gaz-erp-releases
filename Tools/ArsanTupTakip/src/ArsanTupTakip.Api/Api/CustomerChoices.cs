using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

// Customer picker data shared by the mobile app and the web panel.
internal static class CustomerChoices
{
    // generalOnly: Yalçın vb. partner müşterileri gizler (genel Müşteriler sekmesi).
    // partner: yalnız o partner anahtarı (ör. yalcin). İkisi boşsa sefer/teslim seçici gibi hepsi gelir.
    public static async Task<List<object>> SearchAsync(string? query, bool includePhone, int take = 50, string? partner = null, bool generalOnly = false)
    {
        string partnerKey = PartnerWorkspace.Normalize(partner);
        List<Customer> all = await new CrudService().CustomerChoicesAsync();
        if (partnerKey.Length > 0)
        {
            all = all.Where(c => PartnerWorkspace.Matches(c, partnerKey)).ToList();
        }
        else if (generalOnly)
        {
            all = all.Where(c => !PartnerWorkspace.IsPartnerCustomer(c)).ToList();
        }

        await using TupTakipDbContext db = new();
        Dictionary<int, int> counts = await db.Cylinders.AsNoTracking()
            .Where(y => y.IsActive && y.Status == CylinderStatus.Customer && y.CurrentCustomerId != null)
            .GroupBy(y => y.CurrentCustomerId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        string term = (query ?? string.Empty).Trim();
        return all
            .Where(c => term.Length == 0 || Contains(c.CompanyName, term) || Contains(c.Code, term) || Contains(c.City, term) || (includePhone && Contains(c.Phone, term)))
            .OrderByDescending(c => counts.GetValueOrDefault(c.Id) > 0)
            .Take(Math.Clamp(take, 1, 200))
            .Select(c => (object)new
            {
                c.Id,
                c.Code,
                Name = c.CompanyName,
                c.City,
                Phone = includePhone ? c.Phone : null,
                IsActive = true,
                Cylinders = counts.GetValueOrDefault(c.Id)
            })
            .ToList();
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
            AppLog.Write($"Yeni müşteri oluşturuldu: {customer.Code} {customer.CompanyName} ({source})");
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
        Cylinders = 0
    };

    // "depoda" or "<customer> müşterisinde" for the registration confirmation.
    public static async Task<string> PlaceTextAsync(Cylinder cylinder)
    {
        if (cylinder.CurrentCustomerId is not int customerId)
        {
            return "depoda";
        }

        await using TupTakipDbContext db = new();
        string? name = await db.Customers.AsNoTracking().Where(c => c.Id == customerId).Select(c => c.CompanyName).FirstOrDefaultAsync();
        return $"{name} müşterisinde";
    }

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
