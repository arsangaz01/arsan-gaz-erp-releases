using System.Net;

namespace ArsanGazERP.MobileApi;

/// <summary>
/// Bu IP'lerden yapılan başarılı panel girişleri için Telegram bilgilendirmesi gönderilmez.
/// Dosya: %LOCALAPPDATA%\ArsanGazERP\MobileApi\trusted-login-ips.txt (satır başına bir adres, # yorum).
/// </summary>
internal static class WebPanelTrustedIps
{
    /// <summary>Sahip ofisi / sabit çıkış (Telegram panel uyarısı istenmiyor).</summary>
    internal const string OwnerOfficeIp = "95.173.235.70";

    internal static string FilePath => Path.Combine(MobileApiPaths.Root, "trusted-login-ips.txt");

    /// <summary>Dosya yoksa oluşturur; sahip IP listede yoksa ekler.</summary>
    internal static void EnsureConfigured()
    {
        MobileApiPaths.Ensure();
        if (!File.Exists(FilePath))
        {
            File.WriteAllLines(FilePath,
            [
                "# Bu IP'lerden basarili panel girislerinde Telegram uyarisi gitmez.",
                OwnerOfficeIp
            ]);
            return;
        }

        if (!IsTrusted(OwnerOfficeIp))
        {
            File.AppendAllText(FilePath, Environment.NewLine + OwnerOfficeIp);
        }
    }

    internal static bool IsTrusted(string? ip)
    {
        if (string.IsNullOrWhiteSpace(ip) || ip == "?")
        {
            return false;
        }

        string normalized = Normalize(ip);
        if (normalized is "127.0.0.1" or "::1" or "0.0.0.1")
        {
            return true;
        }

        foreach (string line in ReadLines())
        {
            if (string.Equals(Normalize(line), normalized, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static IEnumerable<string> ReadLines()
    {
        if (!File.Exists(FilePath))
        {
            return [];
        }

        try
        {
            return File.ReadAllLines(FilePath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'));
        }
        catch (IOException)
        {
            return [];
        }
    }

    private static string Normalize(string ip)
    {
        string trimmed = ip.Trim();
        if (IPAddress.TryParse(trimmed, out IPAddress? address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                return address.MapToIPv4().ToString();
            }

            return address.ToString();
        }

        return trimmed;
    }
}
