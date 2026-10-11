using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

// Customer picker data shared by the mobile app and the web panel: ERP customers plus CRM companies
// (negative Id until first use; the server creates the ERP customer when one is chosen).
internal static class CustomerChoices
{
    public static async Task<List<object>> SearchAsync(string? query, int take = 50)
    {
        List<Customer> all = await new CrudService().CustomerChoicesAsync();
        await using ArsanGazDbContext db = new();
        Dictionary<int, int> counts = await db.Cylinders.AsNoTracking()
            .Where(y => y.IsActive && y.Status == CylinderStatus.Customer && y.CurrentCustomerId != null)
            .GroupBy(y => y.CurrentCustomerId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count);
        string term = (query ?? string.Empty).Trim();
        return all
            .Where(c => term.Length == 0 || Contains(c.CompanyName, term) || Contains(c.Code, term) || Contains(c.City, term) || Contains(c.Phone, term))
            .OrderByDescending(c => counts.GetValueOrDefault(c.Id) > 0)
            .ThenBy(c => c.Id < 0)
            .Take(Math.Clamp(take, 1, 200))
            .Select(c => (object)new
            {
                c.Id,
                c.Code,
                Name = c.CompanyName,
                c.City,
                c.Phone,
                IsActive = true,
                Cylinders = counts.GetValueOrDefault(c.Id),
                FromCrm = c.Id < 0
            })
            .ToList();
    }

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

    private static bool Contains(string? value, string term) =>
        value is not null && value.Contains(term, StringComparison.CurrentCultureIgnoreCase);
}
