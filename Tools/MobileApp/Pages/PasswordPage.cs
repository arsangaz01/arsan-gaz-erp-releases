using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Own password change. Forced (no way back) when the administrator gave a temporary password, as on the web panel.
public sealed class PasswordPage : ContentPage
{
    private readonly Entry _current;
    private readonly Entry _next;
    private readonly Entry _repeat;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly Func<Task> _done;
    private readonly bool _forced;

    public PasswordPage(bool forced, Func<Task> done, Func<Task>? logout = null)
    {
        _forced = forced;
        _done = done;
        Title = "Şifre değiştir";
        Ui.ApplyPageChrome(this);
        _current = Password(forced ? "Mevcut (geçici) şifre" : "Mevcut şifre");
        _next = Password("Yeni şifre (en az 8 karakter, harf ve rakam)");
        _repeat = Password("Yeni şifre (tekrar)");

        VerticalStackLayout stack = new()
        {
            Padding = new Thickness(16, 24, 16, 24),
            Spacing = 14,
            Children =
            {
                Ui.Title("Şifre değiştir"),
                Ui.Note(forced
                    ? "Yönetici hesabınız için geçici bir şifre belirledi. Devam etmeden önce kendi şifrenizi oluşturun. Yeni şifre web panelde de geçerli olur."
                    : "Yeni şifre web panelde de geçerli olur. Diğer telefon ve tarayıcılardaki oturumlarınız kapanır."),
                Ui.Section("Şifre", _current, _next, _repeat, Ui.Primary("Şifreyi değiştir", async (_, _) => await SaveAsync())),
                _busy
            }
        };
        if (logout is not null)
        {
            stack.Children.Add(Ui.Secondary("Çıkış yap", async (_, _) => await logout()));
        }

        Content = new ScrollView { Content = stack };
    }

    public bool IsForced => _forced;

    protected override bool OnBackButtonPressed() => _forced || base.OnBackButtonPressed();

    private static Entry Password(string placeholder)
    {
        Entry entry = Ui.TextField(placeholder, maxLength: 200);
        entry.IsPassword = true;
        return entry;
    }

    private Task SaveAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        if (_next.Text != _repeat.Text)
        {
            throw new ApiException("Yeni şifre ile tekrarı aynı değil.");
        }

        string message = await ApiClient.Instance.ChangePasswordAsync(_current.Text ?? string.Empty, _next.Text ?? string.Empty, _repeat.Text ?? string.Empty);
        _current.Text = _next.Text = _repeat.Text = string.Empty;
        await DisplayAlertAsync("Şifre", message, "Tamam");
        await _done();
    });
}
