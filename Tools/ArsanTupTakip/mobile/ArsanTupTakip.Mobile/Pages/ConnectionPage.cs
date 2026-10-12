using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

public sealed class ConnectionPage : ContentPage
{
    private readonly Entry _server;
    private readonly Entry _code;
    private readonly Label _state = new() { FontSize = 14 };
    private readonly Label _update = new() { FontSize = 14 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public ConnectionPage()
    {
        Title = "Bağlantı";
        Ui.ApplyPageChrome(this);
        _server = Ui.TextField("https://sunucu-adresi", Keyboard.Url);
        _code = Ui.TextField("6 haneli kod", Keyboard.Numeric, maxLength: 6);
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
                        "Güncelleme",
                        _update,
                        Ui.Primary("Güncelle", async (_, _) => await UpdateNowAsync()),
                        Ui.ActionRow(
                            Ui.Secondary("Güncellemeyi denetle", async (_, _) => await CheckUpdateAsync()),
                            Ui.Secondary("APK indir (tarayıcı)", async (_, _) => await OpenDownloadAsync()))),
                    Ui.Section(
                        "Sunucu",
                        Ui.Note("ArsanTüpTakip sunucusunun adresini yöneticinizden alın. Eşleştikten sonra adres sunucudan otomatik güncellenir."),
                        _server,
                        Ui.ActionRow(
                            Ui.Primary("Kaydet ve test et", async (_, _) => await SaveAndTestAsync()),
                            Ui.Secondary("Sunucudan adres al", async (_, _) => await RefreshServerFromPcAsync()))),
                    _busy,
                    _state,
                    Ui.Section(
                        "Telefonu eşleştir",
                        Ui.Note("Yönetici web panelinde Yönetim > Cihazlar bölümünden 6 haneli kod oluşturur. Kodu girip Eşleştir'e basın (10 dakika geçerli)."),
                        Ui.Secondary("Kod nasıl alınır?", async (_, _) => await RequestCodeAsync()),
                        _code,
                        Ui.Primary("Eşleştir", async (_, _) => await PairAsync()),
                        Ui.Secondary("Eşleşmeyi kaldır", async (_, _) => await UnpairAsync()))
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
        await CheckUpdateAsync(showAlerts: false);
    }

    private async Task TrySilentBootstrapAsync()
    {
        try
        {
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
                _update.Text = "Kurulum ekranında \"Güncelle\"ye basın; uygulama yeni sürümle açılır, eşleşme korunur.";
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

    private Task RefreshServerFromPcAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        if (!await ApiClient.Instance.IsPairedAsync())
        {
            throw new ApiException("Önce telefonu eşleştirin.");
        }

        await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
        _server.Text = ApiClient.Instance.ServerUrl;
        _state.Text = "Sunucu adresi güncellendi.";
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
        await DisplayAlertAsync("Eşleştirme", "Telefon eşleşti. Tüp, Sefer ve Araç sekmelerini kullanabilirsiniz.", "Tamam");
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
