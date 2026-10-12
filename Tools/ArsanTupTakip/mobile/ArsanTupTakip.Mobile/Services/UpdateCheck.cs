namespace ArsanTupTakip.Mobile.Services;

// Update check and APK install shared by the login and account screens; works without a login.
public static class UpdateCheck
{
    private const string DismissedUpdateBuildKey = "dismissed_mobile_update_build";

    private static bool _prompted;

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
            // On Android BuildString is the versionCode; Version.Build is not populated from it.
            bool newer = localBuild > 0
                ? info.PublishedMobileBuild > localBuild
                : string.Compare(info.PublishedMobileVersion, local, StringComparison.Ordinal) > 0;
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
