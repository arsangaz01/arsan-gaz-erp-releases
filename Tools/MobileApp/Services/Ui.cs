namespace ArsanGazMobile.Services;

public static class Ui
{
    public static readonly Color Accent = Color.FromArgb("#C2410C");
    public static readonly Color Muted = Color.FromArgb("#6B7280");

    public static Label Title(string text) => new()
    {
        Text = text,
        FontSize = 20,
        FontFamily = "OpenSansSemibold",
        Margin = new Thickness(0, 8, 0, 2)
    };

    public static Border Card(View content)
    {
        Border card = new()
        {
            Padding = new Thickness(14, 12),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            Content = content
        };
        card.SetAppThemeColor(VisualElement.BackgroundColorProperty, Color.FromArgb("#FAFAF9"), Color.FromArgb("#262626"));
        card.SetAppTheme<Brush>(Border.StrokeProperty,
            new SolidColorBrush(Color.FromArgb("#E7E5E4")), new SolidColorBrush(Color.FromArgb("#3F3F46")));
        return card;
    }

    public static Label Note(string text) => new()
    {
        Text = text,
        FontSize = 13,
        TextColor = Muted
    };

    public static Button Primary(string text, EventHandler onClick)
    {
        Button button = new() { Text = text, BackgroundColor = Accent, TextColor = Colors.White, BorderWidth = 0 };
        button.Clicked += onClick;
        return button;
    }

    public static Button Secondary(string text, EventHandler onClick)
    {
        Button button = new() { Text = text };
        button.Clicked += onClick;
        return button;
    }

    public static async Task RunAsync(Page page, ActivityIndicator? busy, Func<Task> action)
    {
        if (busy is not null)
        {
            busy.IsRunning = busy.IsVisible = true;
        }

        try
        {
            await action();
        }
        catch (ApiException exception)
        {
            await page.DisplayAlertAsync("Arsan Gaz ERP", exception.Message, "Tamam");
        }
        catch (Exception exception)
        {
            await page.DisplayAlertAsync("Hata", exception.Message, "Tamam");
        }
        finally
        {
            if (busy is not null)
            {
                busy.IsRunning = busy.IsVisible = false;
            }
        }
    }

    public static async Task OpenPhoneAsync(Page page, string? phone)
    {
        string digits = new((phone ?? string.Empty).Where(c => char.IsDigit(c) || c == '+').ToArray());
        if (digits.Length < 7)
        {
            await page.DisplayAlertAsync("Telefon", "Kayıtlı telefon numarası yok.", "Tamam");
            return;
        }

        PhoneDialer.Default.Open(digits);
    }

    public static async Task OpenWhatsAppAsync(Page page, string? phone)
    {
        string digits = new((phone ?? string.Empty).Where(char.IsDigit).ToArray());
        if (digits.StartsWith('0'))
        {
            digits = "90" + digits[1..];
        }
        else if (digits.Length == 10)
        {
            digits = "90" + digits;
        }

        if (digits.Length < 11)
        {
            await page.DisplayAlertAsync("WhatsApp", "Kayıtlı geçerli bir telefon numarası yok.", "Tamam");
            return;
        }

        await Launcher.Default.OpenAsync(new Uri("https://wa.me/" + digits));
    }
}
