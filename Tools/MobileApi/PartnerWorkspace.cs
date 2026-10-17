using System.Globalization;
using ArsanGazERP.Models;

namespace ArsanGazERP.MobileApi;

// Partner partitions (Yalçın vb.): aynı DB, aynı sefer/stok süreci; genel listelerde gizlenir.
internal static class PartnerWorkspace
{
    internal const string Yalcin = "yalcin";

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    internal static string Normalize(string? key)
    {
        string value = (key ?? string.Empty).Trim().ToLowerInvariant();
        if (value.Length == 0)
        {
            return string.Empty;
        }

        // Turkish ı/İ → ascii-friendly keys
        value = value.Replace('ı', 'i').Replace('İ', 'i');
        return value switch
        {
            "yalcin" or "yalçın" => Yalcin,
            _ => value
        };
    }

    internal static bool IsYalcin(string? key) => Normalize(key) == Yalcin;

    internal static bool IsPartnerCustomer(Customer customer) => !string.IsNullOrWhiteSpace(customer.PartnerKey);

    internal static bool Matches(Customer customer, string? partnerFilter)
    {
        string want = Normalize(partnerFilter);
        string have = Normalize(customer.PartnerKey);
        return want.Length == 0 ? have.Length == 0 : have == want;
    }

    internal static string DisplayName(string key) => Normalize(key) switch
    {
        Yalcin => "Yalçın",
        _ => key
    };
}
