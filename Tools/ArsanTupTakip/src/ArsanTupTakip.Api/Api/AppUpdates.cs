using System.Reflection;
using System.Text.Json;

namespace ArsanTupTakip.Api;

internal sealed record MobileUpdateInfo(string ApiVersion, string PublishedMobileVersion, int PublishedMobileBuild, bool ApkReady, long? ApkSizeBytes, string DownloadPath);

// The deploy script copies ArsanTupTakip-Mobil.apk and its .meta.json ({"versionName","versionCode"}) into the data folder.
// The announced version must match the downloadable APK, otherwise phones would loop on "update available".
internal static class AppUpdates
{
    internal const string ApkFileName = "ArsanTupTakip-Mobil.apk";

    // Separate app for Android 4.4 handhelds (Tools/ArsanTupTakip/handheld), copied in the same way with its own .meta.json.
    internal const string HandheldApkFileName = "ArsanTupTakip-ElTerminali.apk";

    internal static string ApkFile => Path.Combine(AppPaths.Root, ApkFileName);

    internal static string HandheldApkFile => Path.Combine(AppPaths.Root, HandheldApkFileName);

    internal static string ApiVersion { get; } =
        typeof(AppUpdates).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppUpdates).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    internal static MobileUpdateInfo Get() => Get(ApkFile, "/indir");

    internal static MobileUpdateInfo GetHandheld() => Get(HandheldApkFile, "/indir/el-terminali");

    private static MobileUpdateInfo Get(string apkFile, string downloadPath)
    {
        long? size = File.Exists(apkFile) ? new FileInfo(apkFile).Length : null;
        (string name, int code) = size is > 0 ? ReadMeta(apkFile + ".meta.json") ?? ("0.0.0", 0) : ("0.0.0", 0);
        return new MobileUpdateInfo(ApiVersion, name, code, size is > 0 && code > 0, size, downloadPath);
    }

    private static (string Name, int Code)? ReadMeta(string metaFile)
    {
        try
        {
            if (!File.Exists(metaFile))
            {
                return null;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metaFile));
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
