namespace ArsanGazMobile.Services;

// Update check and APK install shared by the home and account screens.
public static class UpdateCheck
{
    private const string DismissedUpdateBuildKey = "dismissed_mobile_update_build";

    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);

    private static bool _prompted;
    private static DateTime _lastCheckUtc = DateTime.MinValue;
    private static UpdateInfo? _available;

    // Home screen check: asks the server at most every 30 minutes and returns the newer build whose APK is ready, else null.
    public static async Task<UpdateInfo?> GetAvailableAsync()
    {
        if (ApiClient.Instance.ServerUrl.Length == 0 || DateTime.UtcNow - _lastCheckUtc < CheckInterval)
        {
            return _available;
        }

        try
        {
            UpdateInfo info = await ApiClient.Instance.GetUpdateInfoAsync();
            _lastCheckUtc = DateTime.UtcNow;
            _available = info.ApkReady && IsNewer(info) ? info : null;
        }
        catch (ApiException)
        {
        }

        return _available;
    }

    public static bool IsNewer(UpdateInfo info)
    {
        int.TryParse(AppInfo.Current.BuildString, out int localBuild);
        // On Android BuildString is the versionCode; Version.Build is not populated from it.
        return localBuild > 0
            ? info.PublishedMobileBuild > localBuild
            : string.Compare(info.PublishedMobileVersion, AppInfo.Current.VersionString, StringComparison.Ordinal) > 0;
    }

    public static bool IsDismissed(UpdateInfo info) =>
        info.PublishedMobileBuild <= Preferences.Default.Get(DismissedUpdateBuildKey, 0);

    // Asks once per app run, and never again for a build the user put off with "Sonra".
    public static async Task PromptOnceAsync(Page page, UpdateInfo info, Label status, ActivityIndicator busy)
    {
        if (_prompted || IsDismissed(info))
        {
            return;
        }

        _prompted = true;
        if (await page.DisplayAlertAsync("Güncelleme", $"Yeni sürüm {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}) hazır. Şimdi güncellensin mi?", "Güncelle", "Sonra"))
        {
            await InstallAsync(page, status, busy);
        }
        else
        {
            Preferences.Default.Set(DismissedUpdateBuildKey, info.PublishedMobileBuild);
        }
    }

    public static async Task InstallAsync(Page page, Label status, ActivityIndicator busy)
    {
        string server = ApiClient.Instance.ServerUrl;
        if (server.Length == 0)
        {
            await page.DisplayAlertAsync("Güncelleme", "Önce sunucu adresini ayarlayın.", "Tamam");
            return;
        }

        await Ui.RunAsync(page, busy, async () =>
        {
            Progress<string> progress = new(text => status.Text = text);
            try
            {
                await ApkUpdater.DownloadAndInstallAsync(server, progress);
                status.Text = "Kurulum ekranında \"Güncelle\"ye basın; uygulama yeni sürümle açılır, oturum korunur.";
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                throw new ApiException("Güncelleme indirilemedi: " + exception.Message);
            }
        });
    }
}
