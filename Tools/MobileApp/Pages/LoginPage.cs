using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Sign-in with the same user name and password as the web panel (arsantuptakip.com/panel).
public sealed class LoginPage : ContentPage
{
    private readonly Entry _server;
    private readonly Entry _user;
    private readonly Entry _password;
    private readonly Label _message = new() { FontSize = 14, TextColor = Color.FromArgb("#B91C1C") };
    private readonly Label _update = new() { FontSize = 13, TextColor = Ui.Muted };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly Action<UserSession> _signedIn;

    public LoginPage(Action<UserSession> signedIn, string? message = null)
    {
        _signedIn = signedIn;
        Title = "Giriş";
        Ui.ApplyPageChrome(this);
        _server = Ui.TextField("Sunucu adresi (ör. https://arsantuptakip.com)", Keyboard.Url);
        _user = Ui.TextField("Kullanıcı adı", Keyboard.Plain, maxLength: 100);
        _user.IsSpellCheckEnabled = false;
        _user.IsTextPredictionEnabled = false;
        _password = Ui.TextField("Şifre", maxLength: 200);
        _password.IsPassword = true;
        _password.ReturnCommand = new Command(async () => await LoginAsync());
        _message.Text = message ?? string.Empty;
        _message.IsVisible = !string.IsNullOrEmpty(message);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 24, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Title("Arsan Gaz · Tüp Takip"),
                    Ui.Note("Web paneldeki kullanıcı adı ve şifrenizle giriş yapın. Oturum 24 saat açık kalır; sonra yeniden giriş istenir."),
                    _message,
                    Ui.Section("Giriş", _user, _password, Ui.Primary("Giriş yap", async (_, _) => await LoginAsync())),
                    _busy,
                    Ui.Section(
                        "Sunucu",
                        _server,
                        Ui.ActionRow(
                            Ui.Secondary("Telegram'dan adres al", async (_, _) => await PasteServerAsync()),
                            Ui.Secondary("Bağlantıyı test et", async (_, _) => await TestAsync()))),
                    Ui.Section("Güncelleme", _update, Ui.Secondary("Uygulamayı güncelle", async (_, _) => await UpdateAsync()))
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await ServerUrlCoordinator.BootstrapAsync();
        }
        catch (ApiException)
        {
        }

        _server.Text = ApiClient.Instance.ServerUrl;
        _user.Text ??= Preferences.Default.Get(LastUserKey, string.Empty);
        (string.IsNullOrEmpty(_user.Text) ? _user : _password).Focus();
        await CheckUpdateAsync();
    }

    private const string LastUserKey = "last_user_name";

    private Task LoginAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        _message.IsVisible = false;
        if (string.IsNullOrWhiteSpace(_user.Text) || string.IsNullOrEmpty(_password.Text))
        {
            throw new ApiException("Kullanıcı adı ve şifreyi girin.");
        }

        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        string device = $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim();
        try
        {
            UserSession session = await ApiClient.Instance.LoginAsync(_user.Text, _password.Text, device.Length == 0 ? "Telefon" : device);
            Preferences.Default.Set(LastUserKey, session.UserName);
            _password.Text = string.Empty;
            _signedIn(session);
        }
        catch (ApiException exception) when (exception is not ServerUnreachableException)
        {
            _message.Text = exception.Message;
            _message.IsVisible = true;
            _password.Text = string.Empty;
        }
    });

    private async Task PasteServerAsync()
    {
        if (await ApiClient.Instance.TryApplyTelegramClipboardUrlAsync())
        {
            _server.Text = ApiClient.Instance.ServerUrl;
            await DisplayAlertAsync("Sunucu", "Adres alındı: " + ApiClient.Instance.ServerUrl, "Tamam");
            return;
        }

        await DisplayAlertAsync("Sunucu", "Panoda geçerli bir sunucu adresi yok. Telegram'daki bağlantı mesajını kopyalayıp tekrar deneyin.", "Tamam");
    }

    private Task TestAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        _server.Text = ApiClient.Instance.ServerUrl;
        string version = await ApiClient.Instance.HelloAsync();
        await DisplayAlertAsync("Sunucu", $"Sunucuya bağlanıldı (API {version}).", "Tamam");
    });

    private async Task CheckUpdateAsync()
    {
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            _update.Text = string.Empty;
            return;
        }

        try
        {
            UpdateInfo info = await ApiClient.Instance.GetUpdateInfoAsync();
            int.TryParse(AppInfo.Current.BuildString, out int localBuild);
            _update.Text = localBuild > 0 && info.PublishedMobileBuild > localBuild
                ? $"Yeni sürüm hazır: {info.PublishedMobileVersion} (build {info.PublishedMobileBuild})."
                : $"Uygulama güncel ({AppInfo.Current.VersionString}).";
        }
        catch (ApiException)
        {
            _update.Text = string.Empty;
        }
    }

    private Task UpdateAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        ApiClient.Instance.ServerUrl = _server.Text ?? string.Empty;
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            throw new ApiException("Önce sunucu adresini girin.");
        }

        Progress<string> progress = new(text => _update.Text = text);
        try
        {
            string? downloadUrl = null;
            try
            {
                downloadUrl = (await ApiClient.Instance.GetUpdateInfoAsync()).DownloadUrl;
            }
            catch (ApiException)
            {
            }

            await ApkUpdater.DownloadAndInstallAsync(ApiClient.Instance.ServerUrl, progress, downloadUrl);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new ApiException("Güncelleme indirilemedi: " + exception.Message);
        }
    });
}
