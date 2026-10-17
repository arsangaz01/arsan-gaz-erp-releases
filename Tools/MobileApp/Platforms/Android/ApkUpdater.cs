using Android.App;
using Android.Content;
using Android.OS;
namespace ArsanGazMobile.Services;

public static class ApkUpdater
{
    private const string Authority = "com.arsangaz.erpmobile.updates";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

    // Prefer absolute downloadUrl from /api/guncelleme so ERP never hits TupTakip paths (or the reverse).
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
        Activity activity = Platform.CurrentActivity ?? throw new InvalidOperationException("Uygulama penceresi hazır değil.");
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O && !activity.PackageManager!.CanRequestPackageInstalls())
        {
            Intent settings = new(Android.Provider.Settings.ActionManageUnknownAppSources,
                Android.Net.Uri.Parse("package:" + activity.PackageName));
            activity.StartActivity(settings);
            throw new InvalidOperationException(
                "Bir kereliğine izin gerekiyor: açılan ekranda \"Bu kaynaktan izin ver\"i açın, geri dönüp tekrar Güncelle'ye basın.");
        }

        string directory = Path.Combine(FileSystem.CacheDirectory, "updates");
        Directory.CreateDirectory(directory);
        string target = Path.Combine(directory, "ArsanGazERP-Mobil.apk");
        string partial = target + ".part";
        string apkUrl = ResolveApkUrl(serverUrl, downloadUrl);

        using (HttpResponseMessage response = await Http.GetAsync(apkUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            response.EnsureSuccessStatusCode();
            long? total = response.Content.Headers.ContentLength;
            await using Stream source = await response.Content.ReadAsStreamAsync();
            await using (FileStream destination = File.Create(partial))
            {
                byte[] buffer = new byte[81920];
                long received = 0;
                int lastPercent = -1;
                int read;
                while ((read = await source.ReadAsync(buffer)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read));
                    received += read;
                    int percent = total is > 0 ? (int)(received * 100 / total.Value) : -1;
                    if (percent != lastPercent)
                    {
                        lastPercent = percent;
                        progress.Report(percent >= 0
                            ? $"İndiriliyor… %{percent} ({received / 1048576.0:0.0} MB)"
                            : $"İndiriliyor… {received / 1048576.0:0.0} MB");
                    }
                }

                if (total is > 0 && received != total)
                {
                    throw new IOException("İndirme yarım kaldı; tekrar deneyin.");
                }
            }
        }

        File.Move(partial, target, overwrite: true);
        progress.Report("İndirildi. Kurulum ekranı açılıyor…");

        Android.Net.Uri uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, Authority, new Java.IO.File(target))!;
        Intent install = new(Intent.ActionView);
        install.SetDataAndType(uri, "application/vnd.android.package-archive");
        install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(install);
    }
}
