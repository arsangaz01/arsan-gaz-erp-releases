namespace ArsanGazMobile.Services;

// iOS: sideload APK yok. Güncelleme TestFlight / App Store veya tarayıcıdaki indir linki ile.
public static class ApkUpdater
{
    public static string ResolveApkUrl(string serverUrl, string? downloadUrl)
    {
        if (!string.IsNullOrWhiteSpace(downloadUrl)
            && (downloadUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                || downloadUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)))
        {
            return downloadUrl.Trim();
        }

        string path = string.IsNullOrWhiteSpace(downloadUrl) ? "/indir" : downloadUrl.Trim();
        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        return serverUrl.TrimEnd('/') + path;
    }

    public static async Task DownloadAndInstallAsync(string serverUrl, IProgress<string> progress, string? downloadUrl = null)
    {
        string url = ResolveApkUrl(serverUrl, downloadUrl);
        progress.Report("iOS'ta uygulama mağazası / TestFlight ile güncellenir. İndirme sayfası açılıyor…");
        await Launcher.Default.OpenAsync(new Uri(url));
        throw new InvalidOperationException(
            "iPhone'da APK kurulumu yok. Açılan sayfadan veya TestFlight'tan yeni sürümü yükleyin.");
    }
}
