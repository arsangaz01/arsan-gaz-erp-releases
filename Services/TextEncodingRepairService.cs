using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class TextEncodingRepairService
{
    private static readonly string[] MojibakeMarkers =
    {
        "Ãƒ", "Ã„", "Ã…", "Ã‚", "Ã¢â‚¬", "Ã¯Â¿Â½", "Ã°Å¸"
    };

    public async Task<int> RepairDatabaseAsync()
    {
        await using ArsanGazDbContext db = new();
        var prospects = await db.Prospects.ToListAsync();
        int changed = 0;

        foreach (var item in prospects)
        {
            bool rowChanged = false;
            {
                var __encodingValue0 = item.CompanyName;
                rowChanged |= Repair(ref __encodingValue0);
                item.CompanyName = __encodingValue0;
            }
            {
                var __encodingValue1 = item.City;
                rowChanged |= Repair(ref __encodingValue1);
                item.City = __encodingValue1;
            }
            {
                var __encodingValue2 = item.SectorQuery;
                rowChanged |= Repair(ref __encodingValue2);
                item.SectorQuery = __encodingValue2;
            }
            rowChanged |= RepairNullable(item.Address, value => item.Address = value);
            rowChanged |= RepairNullable(item.Categories, value => item.Categories = value);
            {
                var __encodingValue3 = item.RecommendedProducts;
                rowChanged |= Repair(ref __encodingValue3);
                item.RecommendedProducts = __encodingValue3;
            }
            {
                var __encodingValue4 = item.Status;
                rowChanged |= Repair(ref __encodingValue4);
                item.Status = __encodingValue4;
            }
            if (rowChanged) changed++;
        }

        if (changed > 0)
        {
            await db.SaveChangesAsync();
        }

        return changed;
    }

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value ?? string.Empty;
        string current = value;
        for (int attempt = 0; attempt < 2 && LooksBroken(current); attempt++)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(current);
            string repaired = Encoding.UTF8.GetString(bytes);
            if (repaired.Contains('\uFFFD') || repaired == current) break;
            current = repaired;
        }
        return current;
    }

    private static bool Repair(ref string value)
    {
        string corrected = Normalize(value);
        if (corrected == value) return false;
        value = corrected;
        return true;
    }

    private static bool RepairNullable(string? value, Action<string?> setter)
    {
        if (value is null) return false;
        string corrected = Normalize(value);
        if (corrected == value) return false;
        setter(corrected);
        return true;
    }

    private static bool LooksBroken(string value) => MojibakeMarkers.Any(value.Contains);
}