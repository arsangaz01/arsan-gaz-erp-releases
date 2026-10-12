using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Signed-in user, session expiry, password, logout, update and server address.
public sealed class AccountPage : ContentPage
{
    private readonly Label _info = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _permissions = Ui.Note(string.Empty);
    private readonly Label _update = Ui.Note(string.Empty);
    private readonly Label _state = Ui.Note(string.Empty);
    private readonly Entry _server;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public AccountPage()
    {
        Title = "Hesap";
        Ui.ApplyPageChrome(this);
        _server = Ui.TextField("https://sunucu-adresi", Keyboard.Url);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Section(
                        "Hesap",
                        _info,
                        _permissions,
                        Ui.ActionRow(
                            Ui.Secondary("Şifre değiştir", async (_, _) => await Navigation.PushAsync(new ChangePasswordPage(required: false))),
                            Ui.Secondary("Yetkileri yenile", async (_, _) => await RefreshAccountAsync())),
                        Ui.Primary("Çıkış yap", async (_, _) => await LogoutAsync())),
                    Ui.Section(
                        "Güncelleme",
                        _update,
                        Ui.Primary("Güncelle", async (_, _) => await UpdateCheck.InstallAsync(this, _update, _busy)),
                        Ui.ActionRow(
                            Ui.Secondary("Güncellemeyi denetle", async (_, _) => await UpdateCheck.RefreshAsync(this, _update, _busy, showAlerts: true)),
                            Ui.Secondary("APK indir (tarayıcı)", async (_, _) => await UpdateCheck.OpenDownloadAsync(this)))),
                    Ui.Section(
                        "Sunucu",
                        Ui.Note("Adres sunucudan otomatik güncellenir; yalnızca yöneticiniz isterse değiştirin."),
                        _server,
                        Ui.ActionRow(
                            Ui.Primary("Kaydet ve test et", async (_, _) => await SaveAndTestAsync()),
                            Ui.Secondary("Sunucudan adres al", async (_, _) => await RefreshServerAsync())),
                        _state),
                    _busy
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _server.Text = ApiClient.Instance.ServerUrl;
        ShowAccount();
        await UpdateCheck.RefreshAsync(this, _update, _busy, showAlerts: false);
    }

    private void ShowAccount()
    {
        if (ApiClient.Instance.Account is not AccountInfo account)
        {
            _info.Text = "Oturum yok.";
            _permissions.Text = string.Empty;
            return;
        }

        _info.Text = string.Join(Environment.NewLine,
            $"Kullanıcı: {account.DisplayName} ({account.User})",
            $"Rol: {account.RoleText}",
            $"Oturum bitişi: {account.ExpiresUtc.ToLocalTime():dd.MM.yyyy HH:mm} (girişten 24 saat sonra)");
        _permissions.Text = $"{account.Permissions.Count} yetki tanımlı. Yetkileri yönetici web panelden değiştirir; değişiklik için \"Yetkileri yenile\"ye basın.";
    }

    private Task RefreshAccountAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        List<string> before = [.. ApiClient.Instance.Account?.Permissions ?? []];
        AccountInfo account = await ApiClient.Instance.RefreshAccountAsync();
        ShowAccount();
        if (!before.Order().SequenceEqual(account.Permissions.Order()) || account.MustChangePassword)
        {
            App.ShowMain();
        }
        else
        {
            await DisplayAlertAsync("Yetkiler", "Yetkileriniz güncel.", "Tamam");
        }
    });

    private async Task LogoutAsync()
    {
        if (await DisplayAlertAsync("Çıkış", "Bu telefondaki oturum kapatılsın mı? Tekrar kullanmak için kullanıcı adı ve şifre gerekir.", "Çıkış yap", "Vazgeç"))
        {
            await App.LogoutAsync();
        }
    }

    private Task SaveAndTestAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        _server.Text = ApiClient.Instance.ServerUrl;
        string version = await ApiClient.Instance.HelloAsync();
        await ApiClient.Instance.RefreshAccountAsync();
        _state.Text = $"Sunucuya bağlanıldı (API {version}). Oturum geçerli.";
        ShowAccount();
    });

    private Task RefreshServerAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        await ApiClient.Instance.TryRefreshServerUrlFromPcAsync();
        _server.Text = ApiClient.Instance.ServerUrl;
        _state.Text = "Sunucu adresi güncellendi.";
    });
}
