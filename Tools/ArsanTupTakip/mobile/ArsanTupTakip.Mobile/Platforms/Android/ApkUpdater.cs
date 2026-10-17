using System.Net;
using System.Net.Http.Headers;
using Android.App;
using Android.Content;
using Android.OS;
namespace ArsanTupTakip.Mobile.Services;

public static class ApkUpdater
{
    private const string Authority = "com.arsantuptakip.mobil.updates";
    private const int MaxAttempts = 5;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(15) };

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
        string target = Path.Combine(directory, "ArsanTupTakip-Mobil.apk");
        string partial = target + ".part";
        File.Delete(partial);

        // A locked screen can suspend the connection on some phones, so the screen stays on while downloading.
        MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = true);
        try
        {
            await DownloadWithResumeAsync(ResolveApkUrl(serverUrl, downloadUrl), partial, progress);
        }
        finally
        {
            MainThread.BeginInvokeOnMainThread(() => DeviceDisplay.Current.KeepScreenOn = false);
        }

        File.Move(partial, target, overwrite: true);
        progress.Report("İndirildi. Kurulum ekranı açılıyor…");

        Android.Net.Uri uri = AndroidX.Core.Content.FileProvider.GetUriForFile(activity, Authority, new Java.IO.File(target))!;
        Intent install = new(Intent.ActionView);
        install.SetDataAndType(uri, "application/vnd.android.package-archive");
        install.AddFlags(ActivityFlags.GrantReadUriPermission | ActivityFlags.NewTask);
        activity.StartActivity(install);
    }

    // Mobile connections drop mid-download (the Android handler then throws Java exceptions, not IOException); each retry
    // continues from the bytes already written with a Range request, and starts over if the server sends the whole file.
    private static async Task DownloadWithResumeAsync(string url, string partial, IProgress<string> progress)
    {
        long? total = null;
        for (int attempt = 1; ; attempt++)
        {
            long received = File.Exists(partial) ? new FileInfo(partial).Length : 0;
            try
            {
                total = await DownloadOnceAsync(url, partial, received, total, attempt, progress);
                return;
            }
            catch (Exception exception) when (IsTransient(exception) && attempt < MaxAttempts)
            {
                progress.Report($"Bağlantı kesildi, yeniden deneniyor ({attempt}/{MaxAttempts - 1})…");
                await Task.Delay(TimeSpan.FromSeconds(3 * attempt));
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                throw new IOException("İndirme bağlantısı tekrar tekrar kesildi. Wi-Fi veya daha iyi çeken bir yerde tekrar Güncelle'ye basın.", exception);
            }
        }
    }

    private static async Task<long?> DownloadOnceAsync(string url, string partial, long offset, long? knownTotal, int attempt, IProgress<string> progress)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, url);
        if (offset > 0)
        {
            request.Headers.Range = new RangeHeaderValue(offset, null);
        }

        using HttpResponseMessage response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && knownTotal is long done && offset == done)
        {
            return knownTotal;
        }

        response.EnsureSuccessStatusCode();
        bool resumed = offset > 0 && response.StatusCode == HttpStatusCode.PartialContent
            && response.Content.Headers.ContentRange?.From == offset;
        long? total = resumed
            ? response.Content.Headers.ContentRange?.Length
            : response.Content.Headers.ContentLength;
        if (knownTotal is long expected && total is long actual && expected != actual)
        {
            // The published APK changed between attempts: the bytes written so far belong to the old file.
            resumed = false;
            total = response.StatusCode == HttpStatusCode.PartialContent ? null : response.Content.Headers.ContentLength;
            if (total is null)
            {
                File.Delete(partial);
                throw new IOException("Sunucudaki güncelleme dosyası indirme sırasında değişti; tekrar Güncelle'ye basın.");
            }
        }

        long received = resumed ? offset : 0;
        await using Stream source = await response.Content.ReadAsStreamAsync();
        await using FileStream destination = new(partial, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write);
        byte[] buffer = new byte[81920];
        int lastPercent = -1;
        int read;
        string retry = attempt > 1 ? " (devam)" : string.Empty;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read));
            received += read;
            int percent = total is > 0 ? (int)(received * 100 / total.Value) : -1;
            if (percent != lastPercent)
            {
                lastPercent = percent;
                progress.Report(percent >= 0
                    ? $"İndiriliyor{retry}… %{percent} ({received / 1048576.0:0.0} MB)"
                    : $"İndiriliyor{retry}… {received / 1048576.0:0.0} MB");
            }
        }

        if (total is > 0 && received != total)
        {
            throw new IOException("İndirme yarım kaldı.");
        }

        return total;
    }

    private static bool IsTransient(Exception exception) =>
        exception is HttpRequestException or IOException or TaskCanceledException or Java.Lang.Exception
        && exception is not HttpRequestException { StatusCode: HttpStatusCode.NotFound };
}
