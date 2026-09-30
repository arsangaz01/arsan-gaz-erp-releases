using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class MojibakeRepairService
{
    private static readonly char[] MojibakeMarkers = { 'Ã', 'Ä', 'Å', 'Æ', 'Â', 'â', 'ð', '�' };

    public async Task<int> RepairAsync()
    {
        await using ArsanGazDbContext db = new();
        var prospects = await db.Prospects.ToListAsync();
        int changedRecords = 0;

        foreach (var prospect in prospects)
        {
            bool changed = false;
            changed |= AssignIfChanged(prospect.CompanyName, value => prospect.CompanyName = value);
            changed |= AssignIfChanged(prospect.City, value => prospect.City = value);
            changed |= AssignIfChanged(prospect.SectorQuery, value => prospect.SectorQuery = value);
            changed |= AssignIfChanged(prospect.Address, value => prospect.Address = value);
            changed |= AssignIfChanged(prospect.Categories, value => prospect.Categories = value);
            changed |= AssignIfChanged(prospect.RecommendedProducts, value => prospect.RecommendedProducts = value);
            changed |= AssignIfChanged(prospect.Status, value => prospect.Status = value);
            if (changed) changedRecords++;
        }

        if (changedRecords > 0) await db.SaveChangesAsync();
        return changedRecords;
    }

    public static string? Repair(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        string current = value;
        for (int pass = 0; pass < 3 && LooksBroken(current); pass++)
        {
            string candidate = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(current));
            if (candidate.Contains('\uFFFD') || candidate == current) break;
            current = candidate;
        }

        return current
            .Replace("Sağlık kuruluŸu", "Sağlık kuruluşu", StringComparison.Ordinal)
            .Replace("Sağlık kuruluÅŸu", "Sağlık kuruluşu", StringComparison.Ordinal)
            .Replace("SaÄŸlÄ±k kuruluÅŸu", "Sağlık kuruluşu", StringComparison.Ordinal)
            .Replace("MÃ¼ÅŸteri", "Müşteri", StringComparison.Ordinal)
            .Replace("Åž", "Ş", StringComparison.Ordinal)
            .Replace("ÅŸ", "ş", StringComparison.Ordinal)
            .Replace("Ä°", "İ", StringComparison.Ordinal)
            .Replace("Ä±", "ı", StringComparison.Ordinal)
            .Replace("ÄŸ", "ğ", StringComparison.Ordinal)
            .Replace("Ã¼", "ü", StringComparison.Ordinal)
            .Replace("Ã¶", "ö", StringComparison.Ordinal)
            .Replace("Ã§", "ç", StringComparison.Ordinal)
            .Replace("Ãœ", "Ü", StringComparison.Ordinal)
            .Replace("Ã–", "Ö", StringComparison.Ordinal)
            .Replace("Ã‡", "Ç", StringComparison.Ordinal);
    }

    private static bool LooksBroken(string value) => value.IndexOfAny(MojibakeMarkers) >= 0;

    private static bool AssignIfChanged(string? original, Action<string> setter)
    {
        string? repaired = Repair(original);
        if (repaired is null || repaired == original) return false;
        setter(repaired);
        return true;
    }
}
