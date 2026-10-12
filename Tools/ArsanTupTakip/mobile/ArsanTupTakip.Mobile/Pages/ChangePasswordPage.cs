using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Same password as the web panel. When the administrator requires a new password, the app opens here until it is changed.
public sealed class ChangePasswordPage : ContentPage
{
    private readonly bool _required;
    private readonly Entry _current;
    private readonly Entry _new;
    private readonly Entry _repeat;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public ChangePasswordPage(bool required)
    {
        _required = required;
        Title = "Şifre değiştir";
        Ui.ApplyPageChrome(this);
        _current = Password("Mevcut şifre");
        _new = Password("Yeni şifre (en az 8 karakter)");
        _repeat = Password("Yeni şifre (tekrar)");
        VerticalStackLayout stack = new()
        {
            Padding = new Thickness(16, 24, 16, 24),
            Spacing = 14,
            Children =
            {
                Ui.Section(
                    "Şifre değiştir",
                    Ui.Note(required
                        ? "Yönetici yeni şifre belirlemenizi istedi. Devam etmek için şifrenizi değiştirin; yeni şifre web panelde de geçerli olur."
                        : "Yeni şifre web panelde de geçerli olur."),
                    _current,
                    _new,
                    _repeat,
                    Ui.Primary("Şifreyi değiştir", async (_, _) => await SaveAsync())),
                _busy
            }
        };
        if (required)
        {
            stack.Children.Add(Ui.Secondary("Çıkış yap", async (_, _) => await App.LogoutAsync()));
        }

        Content = new ScrollView { Content = stack };
    }

    private static Entry Password(string placeholder)
    {
        Entry entry = Ui.TextField(placeholder);
        entry.IsPassword = true;
        return entry;
    }

    private Task SaveAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        string message = await ApiClient.Instance.ChangePasswordAsync(_current.Text ?? string.Empty, _new.Text ?? string.Empty, _repeat.Text ?? string.Empty);
        _current.Text = _new.Text = _repeat.Text = string.Empty;
        await DisplayAlertAsync("Şifre", message, "Tamam");
        if (_required)
        {
            App.ShowMain();
        }
        else
        {
            await Navigation.PopAsync();
        }
    });
}
