using System.Text.RegularExpressions;

namespace ArsanGazERP.MobileApi;

internal static partial class MobilePublicUrl
{
    internal static string StablePublicUrlFile => Path.Combine(MobileApiPaths.Root, "stable-public-url.txt");

    [GeneratedRegex(@"^https://[^\s/]+(?:\.[^\s/]+)+/?$", RegexOptions.IgnoreCase)]
    private static partial Regex HttpsUrl();

    internal static string? ReadStable()
    {
        if (!File.Exists(StablePublicUrlFile))
        {
            return null;
        }

        foreach (string line in File.ReadAllLines(StablePublicUrlFile))
        {
            string candidate = line.Trim();
            if (candidate.Length == 0 || candidate.StartsWith('#'))
            {
                continue;
            }

            return Normalize(candidate);
        }

        return null;
    }

    internal static string? ReadPreferred()
    {
        string? stable = ReadStable();
        if (stable is not null)
        {
            return stable;
        }

        if (File.Exists(MobileApiPaths.TunnelUrlFile))
        {
            string fromFile = File.ReadAllText(MobileApiPaths.TunnelUrlFile).Trim();
            if (fromFile.Length > 0)
            {
                return Normalize(fromFile);
            }
        }

        return string.IsNullOrWhiteSpace(TunnelService.CurrentUrl) ? null : Normalize(TunnelService.CurrentUrl);
    }

    internal static bool IsStableConfigured => ReadStable() is not null;

    internal static string BuildAppDeepLink(string publicUrl) =>
        "arsangazerp://server?u=" + Uri.EscapeDataString(Normalize(publicUrl));

    internal static string Normalize(string url)
    {
        string trimmed = url.Trim().TrimEnd('/');
        if (!HttpsUrl().IsMatch(trimmed))
        {
            throw new ArgumentException("Geçerli bir https adresi gerekli.");
        }

        return trimmed;
    }
}
