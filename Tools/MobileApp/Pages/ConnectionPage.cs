using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class ConnectionPage : ContentPage
{
    private readonly Entry _server;
    private readonly Label _account = new() { FontSize = 14 };
    private readonly Label _state = new() { FontSize = 14 };
    private readonly Label _hub = new() { FontSize = 13, TextColor = Ui.Muted };
    private readonly Label _update = new() { FontSize = 14 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public ConnectionPage()
    {
        Title = "Bağlantı";
        Ui.ApplyPageChrome(this);
        _server = Ui.TextField("https://arsantuptakip.com", Keyboard.Url);
        _state.FontSize = 14;
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Section(
                        "Oturum",
                        _account,
                        Ui.ActionRow(
                            Ui.Secondary("Şifre değiştir", async (_, _) => await ChangePasswordAsync()),
                            Ui.Secondary("Çıkış yap", async (_, _) => await LogoutAsync()))),
                    Ui.Section(
                        "Sistem durumu",
                        Ui.Note("Telefon, bilgisayardaki ERP + Watcher + Telegram + PC ajan köprüsü ile aynı hatta çalışır."),
                        _hub,
                        Ui.Secondary("Durumu yenile", async (_, _) => await RefreshHubAsync())),
                    Ui.Section(
                        "Güncelleme",
                        _update,
                        Ui.Primary("Güncelle", async (_, _) => await UpdateNowAsync()),
                        Ui.ActionRow(
                            Ui.Secondary("Güncellemeyi denetle", async (_, _) => await CheckUpdateAsync()),
                            Ui.Secondary("APK indir (tarayıcı)", async (_, _) => await OpenDownloadAsync()))),
                    Ui.Section(
                        "Sunucu",
                        Ui.Note("Giriş yaptıktan sonra adres sunucudan otomatik güncellenir."),
                        _server,
                        Ui.ActionRow(
                            Ui.Primary("Telegram'dan bağlan", async (_, _) => await ConnectFromTelegramAsync()),
                            Ui.Secondary("PC'den adres al", async (_, _) => await RefreshServerFromPcAsync())),
                        Ui.Secondary("Kaydet ve test et", async (_, _) => await SaveAndTestAsync())),
                    _busy,
                    _state
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _server.Text = ApiClient.Instance.ServerUrl;
        await TrySilentBootstrapAsync();
        RefreshAccount();
        await RefreshHubAsync();
        await CheckUpdateAsync(showAlerts: false);
    }

    private async Task TrySilentBootstrapAsync()
    {
        try
        {
            if (ApiClient.Instance.ServerUrl.Length == 0)
            {
                await ApiClient.Instance.TryApplyTelegramClipboardUrlAsync();
                _server.Text = ApiClient.Instance.ServerUrl;
            }

            if (await ApiClient.Instance.IsSignedInAsync())
            {
                await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
                _server.Text = ApiClient.Instance.ServerUrl;
                await ApiClient.Instance.RefreshSessionAsync();
            }
        }
        catch (ApiException)
        {
        }
    }

    private async Task RefreshHubAsync()
    {
        if (ApiClient.Instance.ServerUrl.Length == 0 || !await ApiClient.Instance.IsSignedInAsync())
        {
            _hub.Text = "Durum için önce sunucuya bağlanıp giriş yapın.";
            return;
        }

        try
        {
            HubStatus status = await ApiClient.Instance.GetHubAsync();
            _hub.Text =
                $"ERP: {(status.ErpRunning ? "açık" : "kapalı")} · Watcher: {(status.WatcherRunning ? "açık" : "kapalı")}\n" +
                $"Telegram: {(status.TelegramConfigured ? "yapılandırılmış" : "eksik")} · PC ajan köprüsü: {(status.CursorBridgeActive ? "aktif" : "pasif")}\n" +
                status.IntegrationHint;
        }
        catch (ApiException exception)
        {
            _hub.Text = exception.Message;
        }
    }

    private async Task CheckUpdateAsync(bool showAlerts = true)
    {
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            _update.Text = "Güncelleme denetimi için sunucu adresi gerekli.";
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
            _update.Text = newer
                ? $"Yeni sürüm: {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}). Telefondaki: {local}{buildHint}."
                : $"Telefon sürümü güncel ({local}{buildHint}).";
            if (newer && !info.ApkReady)
            {
                _update.Text += " Sunucuda APK dosyası henüz yok.";
            }
            else if (newer && !showAlerts && !_updatePrompted)
            {
                int dismissed = Preferences.Default.Get(DismissedUpdateBuildKey, 0);
                if (info.PublishedMobileBuild <= dismissed)
                {
                    _update.Text += " Bağlantı sekmesinden \"Güncelle\" ile kurabilirsiniz.";
                }
                else
                {
                    _updatePrompted = true;
                    if (await DisplayAlertAsync("Güncelleme", $"Yeni sürüm {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}) hazır. Şimdi güncellensin mi?", "Güncelle", "Sonra"))
                    {
                        await UpdateNowAsync();
                    }
                    else
                    {
                        Preferences.Default.Set(DismissedUpdateBuildKey, info.PublishedMobileBuild);
                    }
                }
            }
        }
        catch (ApiException exception)
        {
            _update.Text = exception.Message;
        }
    }

    private const string DismissedUpdateBuildKey = "dismissed_mobile_update_build";

    private bool _updatePrompted;

    private async Task UpdateNowAsync()
    {
        string server = ApiClient.Instance.ServerUrl;
        if (server.Length == 0)
        {
            await DisplayAlertAsync("Güncelleme", "Önce sunucu adresini ayarlayın.", "Tamam");
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            Progress<string> progress = new(text => _update.Text = text);
            try
            {
                await ApkUpdater.DownloadAndInstallAsync(server, progress);
                _update.Text = "Kurulum ekranında \"Güncelle\"ye basın; uygulama yeni sürümle açılır, oturum korunur.";
            }
            catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
            {
                throw new ApiException("Güncelleme indirilemedi: " + exception.Message);
            }
        });
    }

    private async Task OpenDownloadAsync()
    {
        string server = ApiClient.Instance.ServerUrl;
        if (server.Length == 0)
        {
            await DisplayAlertAsync("İndir", "Önce sunucu adresini ayarlayın.", "Tamam");
            return;
        }

        await Launcher.Default.OpenAsync(new Uri(server + "/indir"));
    }

    private async Task ConnectFromTelegramAsync()
    {
        if (await ApiClient.Instance.TryApplyTelegramClipboardUrlAsync())
        {
            _server.Text = ApiClient.Instance.ServerUrl;
            await SaveAndTestAsync();
            return;
        }

        await DisplayAlertAsync(
            "Telegram",
            "Panoda https:// adresi yok. Telegram'daki mobil ERP mesajından adresi kopyalayıp tekrar deneyin.",
            "Tamam");
    }

    private Task RefreshServerFromPcAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        if (!await ApiClient.Instance.IsSignedInAsync())
        {
            throw new ApiException("Önce giriş yapın.");
        }

        await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
        _server.Text = ApiClient.Instance.ServerUrl;
        _state.Text = "Sunucu adresi güncellendi.";
    });

    private void RefreshAccount()
    {
        UserSession? session = ApiClient.Instance.Session;
        _account.Text = session is null
            ? "Giriş yapılmamış."
            : $"{session.Caption}\nCihaz: {session.Device}" +
              (session.ExpiresUtc is { } expires ? $"\nOturum bitişi: {expires.ToLocalTime():dd.MM.yyyy HH:mm} (24 saat)" : string.Empty);
    }

    private Task SaveAndTestAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        _server.Text = ApiClient.Instance.ServerUrl;
        string version = await ApiClient.Instance.HelloAsync();
        await ApiClient.Instance.RefreshSessionAsync();
        RefreshAccount();
        _state.Text = $"Sunucuya bağlanıldı (API {version}). Oturum geçerli.";
        await RefreshHubAsync();
    });

    private async Task ChangePasswordAsync()
    {
        await Navigation.PushAsync(new PasswordPage(forced: false, done: async () =>
        {
            await Navigation.PopAsync();
            RefreshAccount();
            await DisplayAlertAsync("Şifre", "Şifre değişti. Diğer telefonlardaki oturumlarınız kapatıldı.", "Tamam");
        }));
    }

    private async Task LogoutAsync()
    {
        int pending = OfflineMoves.Pending.Count;
        string question = pending > 0
            ? $"{pending} çevrimdışı hareket henüz gönderilmedi. Çıkış yaparsanız bir sonraki girişte gönderilir. Yine de çıkılsın mı?"
            : "Bu telefondaki oturum kapatılsın mı?";
        if (!await DisplayAlertAsync("Çıkış", question, "Çıkış yap", "Vazgeç"))
        {
            return;
        }

        await ApiClient.Instance.LogoutAsync();
        (Application.Current as App)?.ShowLogin(null);
    }

