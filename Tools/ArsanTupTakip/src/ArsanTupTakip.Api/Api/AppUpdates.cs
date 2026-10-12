using System.Reflection;
using System.Text.Json;

namespace ArsanTupTakip.Api;

internal sealed record MobileUpdateInfo(string ApiVersion, string PublishedMobileVersion, int PublishedMobileBuild, bool ApkReady, long? ApkSizeBytes, string DownloadPath);

// The deploy script copies ArsanTupTakip-Mobil.apk and its .meta.json ({"versionName","versionCode"}) into the data folder.
// The announced version must match the downloadable APK, otherwise phones would loop on "update available".
internal static class AppUpdates
{
    internal const string ApkFileName = "ArsanTupTakip-Mobil.apk";

    internal static string ApkFile => Path.Combine(AppPaths.Root, ApkFileName);

    private static string MetaFile => ApkFile + ".meta.json";

    internal static string ApiVersion { get; } =
        typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppUpdates).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    internal static MobileUpdateInfo Get()
    {
        long? size = File.Exists(ApkFile) ? new FileInfo(ApkFile).Length : null;
        (string name, int code) = size is > 0 ? ReadMeta() ?? ("0.0.0", 0) : ("0.0.0", 0);
        return new MobileUpdateInfo(ApiVersion, name, code, size is > 0 && code > 0, size, "/indir");
    }

    private static (string Name, int Code)? ReadMeta()
    {
        try
        {
            if (!File.Exists(MetaFile))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(MetaFile));
            JsonElement root = document.RootElement;
            string name = root.TryGetProperty("versionName", out JsonElement n) ? n.GetString() ?? string.Empty : string.Empty;
            int code = root.TryGetProperty("versionCode", out JsonElement c)
                ? c.ValueKind == JsonValueKind.Number ? c.GetInt32() : int.TryParse(c.GetString(), out int parsed) ? parsed : 0
                : 0;
            return name.Length > 0 && code > 0 ? (name, code) : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or FormatException)
        {
            AppLog.Write("APK meta okunamadı: " + exception.Message);
            return null;
        }
    }

    internal static string? PublicUrl()
    {
        string? url = Environment.GetEnvironmentVariable("TUPTAKIP_PUBLIC_URL");
        return string.IsNullOrWhiteSpace(url) ? null : url.Trim().TrimEnd('/');
    }
}
