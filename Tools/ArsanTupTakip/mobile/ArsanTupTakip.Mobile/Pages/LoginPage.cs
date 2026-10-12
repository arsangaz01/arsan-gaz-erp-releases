using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Sign in with the same user name and password as the web panel; a login is valid for 24 hours.
public sealed class LoginPage : ContentPage
{
    private readonly Entry _user;
    private readonly Entry _password;
    private readonly Entry _server;
    private readonly Label _message = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap, TextColor = Colors.Red, IsVisible = false };
    private readonly Label _update = Ui.Note(string.Empty);
    private readonly Border _serverCard;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public LoginPage(string? message = null)
    {
        Title = "Giriş";
        Ui.ApplyPageChrome(this);
        _user = Ui.TextField("Kullanıcı adı");
        _user.IsSpellCheckEnabled = false;
        _user.IsTextPredictionEnabled = false;
        _user.ReturnType = ReturnType.Next;
        _password = Ui.TextField("Şifre");
        _password.IsPassword = true;
        _password.ReturnType = ReturnType.Done;
        _user.Completed += (_, _) => _password.Focus();
        _password.Completed += async (_, _) => await LoginAsync();
        _server = Ui.TextField("https://sunucu-adresi", Keyboard.Url);
        _user.Text = Preferences.Default.Get(LastUserKey, string.Empty);
        if (!string.IsNullOrWhiteSpace(message))
        {
            _message.Text = message;
            _message.IsVisible = true;
        }

        _serverCard = Ui.Section(
            "Sunucu",
            Ui.Note("Normalde değiştirmeniz gerekmez. Yöneticiniz farklı bir adres verdiyse buraya yazın."),
            _server);
        _serverCard.IsVisible = false;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 24, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Title("ArsanTüpTakip"),
                    Ui.Section(
                        "Giriş",
                        Ui.Note("Web paneldeki kullanıcı adı ve şifrenizle giriş yapın. Giriş bu telefonda 24 saat geçerlidir."),
                        _user,
                        _password,
                        _message,
                        Ui.Primary("Giriş yap", async (_, _) => await LoginAsync())),
                    _busy,
                    _serverCard,
                    Ui.Section(
                        "Güncelleme",
                        _update,
                        Ui.ActionRow(
                            Ui.Secondary("Güncelle", async (_, _) => await UpdateCheck.InstallAsync(this, _update, _busy)),
                            Ui.Secondary("Sunucu adresi", (_, _) => _serverCard.IsVisible = !_serverCard.IsVisible)))
                }
            }
        };
    }

    private const string LastUserKey = "last_user_name";

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (ApiClient.Instance.ServerUrl.Length == 0)
        {
            ApiClient.Instance.ServerUrl = ServerUrlCoordinator.DefaultServerUrl;
        }

        _server.Text = ApiClient.Instance.ServerUrl;
        await UpdateCheck.RefreshAsync(this, _update, _busy, showAlerts: false);
    }

    private async Task LoginAsync()
    {
        string user = (_user.Text ?? string.Empty).Trim();
        string password = _password.Text ?? string.Empty;
        if (user.Length == 0 || password.Length == 0)
        {
            ShowMessage("Kullanıcı adı ve şifre girin.");
            return;
        }

        if (!string.IsNullOrWhiteSpace(_server.Text))
        {
            ApiClient.Instance.ServerUrl = _server.Text;
        }

        _message.IsVisible = false;
        _busy.IsRunning = _busy.IsVisible = true;
        try
        {
            await ApiClient.Instance.LoginAsync(user, password);
            Preferences.Default.Set(LastUserKey, user);
            _password.Text = string.Empty;
            App.ShowMain();
        }
        catch (ApiException exception)
        {
            ShowMessage(exception.Message);
        }
        catch (Exception exception)
        {
            ShowMessage("Giriş yapılamadı: " + exception.Message);
        }
        finally
        {
            _busy.IsRunning = _busy.IsVisible = false;
        }
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        _message.IsVisible = true;
    }
}
