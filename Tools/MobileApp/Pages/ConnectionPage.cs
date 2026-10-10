using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class ConnectionPage : ContentPage
{
    private readonly Entry _server = new() { Placeholder = "https://....trycloudflare.com", Keyboard = Keyboard.Url };
    private readonly Entry _code = new() { Placeholder = "6 haneli kod", Keyboard = Keyboard.Numeric, MaxLength = 6 };
    private readonly Label _state = new() { FontSize = 14 };
    private readonly Label _hub = new() { FontSize = 13, TextColor = Ui.Muted };
    private readonly Label _update = new() { FontSize = 14 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public ConnectionPage()
    {
        Title = "Bağlantı";
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children =
                {
                    Ui.Title("Sistem durumu"),
                    Ui.Note("Telefon, bilgisayardaki ERP + Watcher + Telegram + PC ajan köprüsü ile aynı hatta çalışır."),
                    _hub,
                    Ui.Secondary("Durumu yenile", async (_, _) => await RefreshHubAsync()),
                    Ui.Title("Güncelleme"),
                    _update,
                    Ui.Secondary("Güncellemeyi denetle", async (_, _) => await CheckUpdateAsync()),
                    Ui.Secondary("APK indir (tarayıcı)", async (_, _) => await OpenDownloadAsync()),
                    Ui.Title("Sunucu"),
                    Ui.Note("Adres Telegram'a otomatik gider. Panoda adres varsa tek dokunuşla bağlanın; eşleştikten sonra adres PC'den otomatik güncellenir."),
                    _server,
                    new HorizontalStackLayout
                    {
                        Spacing = 8,
                        Children =
                        {
                            Ui.Primary("Telegram'dan bağlan", async (_, _) => await ConnectFromTelegramAsync()),
                            Ui.Secondary("PC'den adres al", async (_, _) => await RefreshServerFromPcAsync())
                        }
                    },
                    Ui.Secondary("Kaydet ve test et", async (_, _) => await SaveAndTestAsync()),
                    _busy,
                    _state,
                    Ui.Title("Telefonu eşleştir"),
                    Ui.Note("Kod iste → Telegram → kodu gir → Eşleştir. Bir kez yeterli."),
                    Ui.Secondary("Kod iste (Telegram)", async (_, _) => await RequestCodeAsync()),
                    _code,
                    Ui.Primary("Eşleştir", async (_, _) => await PairAsync()),
                    Ui.Secondary("Eşleşmeyi kaldır", async (_, _) => await UnpairAsync())
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _server.Text = ApiClient.Instance.ServerUrl;
        await TrySilentBootstrapAsync();
        await RefreshStateAsync();
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

            if (await ApiClient.Instance.IsPairedAsync())
            {
                await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
                _server.Text = ApiClient.Instance.ServerUrl;
            }
        }
        catch (ApiException)
        {
        }
    }

    private async Task RefreshHubAsync()
    {
        if (ApiClient.Instance.ServerUrl.Length == 0 || !await ApiClient.Instance.IsPairedAsync())
        {
            _hub.Text = "Durum için önce sunucuya bağlanıp telefonu eşleştirin.";
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
            int localBuild = AppInfo.Current.Version.Build;
            bool newer = info.PublishedMobileBuild > localBuild
                || string.Compare(info.PublishedMobileVersion, local, StringComparison.Ordinal) > 0;
            _update.Text = newer
                ? $"Yeni sürüm var: {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}). Telefon: {local}."
                : $"Telefon sürümü güncel ({local}). Sunucu APK: {info.PublishedMobileVersion}.";
            if (showAlerts && newer && !info.ApkReady)
            {
                await DisplayAlertAsync("Güncelleme", "Sunucuda APK dosyası henüz yok; bilgisayarda mobil paket yayımlanmalı.", "Tamam");
            }
        }
        catch (ApiException exception)
        {
            _update.Text = exception.Message;
        }
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
        if (!await ApiClient.Instance.IsPairedAsync())
        {
            throw new ApiException("Önce telefonu eşleştirin.");
        }

        await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
        _server.Text = ApiClient.Instance.ServerUrl;
        _state.Text = "Sunucu adresi bilgisayardan güncellendi.";
    });

    private async Task RefreshStateAsync()
    {
        bool paired = await ApiClient.Instance.IsPairedAsync();
        _state.Text = paired ? "Bu telefon eşleşmiş." : "Bu telefon henüz eşleşmedi.";
    }

    private Task SaveAndTestAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        _server.Text = ApiClient.Instance.ServerUrl;
        string version = await ApiClient.Instance.HelloAsync();
        bool paired = await ApiClient.Instance.IsPairedAsync();
        if (paired)
        {
            await ApiClient.Instance.TextAsync("/api/ozet");
        }

        _state.Text = $"Sunucuya bağlanıldı (API {version})." + (paired ? " Eşleşme geçerli." : " Şimdi eşleştirin.");
        await RefreshHubAsync();
    });

    private Task RequestCodeAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        await DisplayAlertAsync("Eşleştirme", await ApiClient.Instance.RequestPairingCodeAsync(), "Tamam");
    });

    private Task PairAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        string name = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
        await ApiClient.Instance.PairAsync(_code.Text ?? string.Empty, name);
        _code.Text = string.Empty;
        await RefreshStateAsync();
        await RefreshHubAsync();
        await DisplayAlertAsync("Eşleştirme", "Telefon eşleşti. Ajan sekmesi ERP ajanlarına, Telegram PC tarafına bağlıdır.", "Tamam");
    });

    private async Task UnpairAsync()
    {
        if (await DisplayAlertAsync("Eşleşme", "Bu telefonun eşleşmesi kaldırılsın mı?", "Kaldır", "Vazgeç"))
        {
            ApiClient.Instance.Unpair();
            await RefreshStateAsync();
        }
    }
}
