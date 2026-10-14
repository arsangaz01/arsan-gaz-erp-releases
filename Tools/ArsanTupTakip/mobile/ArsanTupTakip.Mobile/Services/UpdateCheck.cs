namespace ArsanTupTakip.Mobile.Services;

// Update check and APK install shared by the login, account and home screens; works without a login.
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

    // "Senkronize et": asks the server now (ignores the 30-minute wait). Available is the newer build whose APK is ready.
    public static async Task<(UpdateInfo? Available, string Text)> CheckNowAsync()
    {
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            return (null, "Güncelleme denetimi için sunucu adresi gerekli.");
        }

        string local = AppInfo.Current.VersionString;
        int.TryParse(AppInfo.Current.BuildString, out int localBuild);
        string mine = local + (localBuild > 0 ? $", build {localBuild}" : string.Empty);
        try
        {
            UpdateInfo info = await ApiClient.Instance.GetUpdateInfoAsync();
            _lastCheckUtc = DateTime.UtcNow;
            bool newer = IsNewer(info);
            _available = newer && info.ApkReady ? info : null;
            return !newer
                ? (null, $"Uygulama güncel ({mine}).")
                : info.ApkReady
                    ? (info, $"Yeni sürüm {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}) hazır. Telefondaki: {mine}.")
                    : (null, $"Yeni sürüm {info.PublishedMobileVersion} yayınlanmış ama sunucuda APK dosyası henüz yok. Telefondaki: {mine}.");
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
        {
            return (null, "Güncelleme denetlenemedi: " + exception.Message);
        }
    }

    // Asks once per app run, and never again for a build the user put off with "Sonra".
    public static async Task PromptOnceAsync(Page page, UpdateInfo info, Label status, ActivityIndicator busy)
    {
        if (_prompted || info.PublishedMobileBuild <= Preferences.Default.Get(DismissedUpdateBuildKey, 0))
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

    private static bool IsNewer(UpdateInfo info)
    {
        int.TryParse(AppInfo.Current.BuildString, out int localBuild);
        // On Android BuildString is the versionCode; Version.Build is not populated from it.
        return localBuild > 0
            ? info.PublishedMobileBuild > localBuild
            : string.Compare(info.PublishedMobileVersion, AppInfo.Current.VersionString, StringComparison.Ordinal) > 0;
    }

    public static async Task RefreshAsync(Page page, Label status, ActivityIndicator busy, bool showAlerts)
    {
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            status.Text = "Güncelleme denetimi için sunucu adresi gerekli.";
            return;
        }

        try
        {
            UpdateInfo info = await ApiClient.Instance.GetUpdateInfoAsync();
            string local = AppInfo.Current.VersionString;
            int.TryParse(AppInfo.Current.BuildString, out int localBuild);
            bool newer = IsNewer(info);
            string buildHint = localBuild > 0 ? $" (build {localBuild})" : string.Empty;
            status.Text = newer
                ? $"Yeni sürüm: {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}). Telefondaki: {local}{buildHint}."
                : $"Telefon sürümü güncel ({local}{buildHint}).";
            if (newer && !info.ApkReady)
            {
                status.Text += " Sunucuda APK dosyası henüz yok.";
            }
            else if (newer && !showAlerts && !_prompted)
            {
                if (info.PublishedMobileBuild <= Preferences.Default.Get(DismissedUpdateBuildKey, 0))
                {
                    status.Text += " \"Güncelle\" ile kurabilirsiniz.";
                    return;
                }

                await PromptOnceAsync(page, info, status, busy);
            }
        }
        catch (ApiException exception)
        {
            status.Text = exception.Message;
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
                status.Text = "Kurulum ekranında \"Güncelle\"ye basın; uygulama yeni sürümle açılır, girişiniz korunur.";
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                throw new ApiException("Güncelleme indirilemedi: " + exception.Message);
            }
        });
    }

    public static async Task OpenDownloadAsync(Page page)
    {
        string server = ApiClient.Instance.ServerUrl;
        if (server.Length == 0)
        {
            await page.DisplayAlertAsync("İndir", "Önce sunucu adresini ayarlayın.", "Tamam");
            return;
        }

        await Launcher.Default.OpenAsync(new Uri(server + "/indir"));
    }
}
