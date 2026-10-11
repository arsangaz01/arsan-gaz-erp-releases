namespace ArsanGazMobile.Services;

public static class Ui
{
    public static readonly Color Accent = Color.FromArgb("#C2410C");
    public static readonly Color AccentDark = Color.FromArgb("#9A3412");
    public static readonly Color Muted = Color.FromArgb("#6B7280");
    public static readonly Color SurfaceLight = Colors.White;
    public static readonly Color SurfaceDark = Color.FromArgb("#262626");
    public static readonly Color PageLight = Color.FromArgb("#F5F5F4");
    public static readonly Color PageDark = Color.FromArgb("#171717");

    public static void ApplyPageChrome(ContentPage page)
    {
        page.SetAppThemeColor(Page.BackgroundColorProperty, PageLight, PageDark);
    }

    public static Label Title(string text) => new()
    {
        Text = text,
        FontSize = 18,
        FontFamily = "OpenSansSemibold",
        Margin = new Thickness(0, 4, 0, 0)
    };

    public static Label SectionCaption(string text) => new()
    {
        Text = text,
        FontSize = 12,
        FontFamily = "OpenSansSemibold",
        TextColor = Accent,
        CharacterSpacing = 0.5,
        Margin = new Thickness(2, 0, 0, 4)
    };

    public static Border Card(View content)
    {
        Border card = new()
        {
            Padding = new Thickness(16, 14),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            Content = content
        };
        card.SetAppThemeColor(VisualElement.BackgroundColorProperty, SurfaceLight, SurfaceDark);
        card.SetAppTheme<Brush>(Border.StrokeProperty,
            new SolidColorBrush(Color.FromArgb("#E7E5E4")), new SolidColorBrush(Color.FromArgb("#3F3F46")));
        return card;
    }

    public static Border Section(string caption, params View[] children)
    {
        VerticalStackLayout stack = new() { Spacing = 10, Children = { SectionCaption(caption) } };
        foreach (View child in children)
        {
            stack.Children.Add(child);
        }

        return Card(stack);
    }

    public static Label Note(string text) => new()
    {
        Text = text,
        FontSize = 13,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = Muted
    };

    public static Entry TextField(string placeholder, Keyboard? keyboard = null, int maxLength = int.MaxValue)
    {
        Entry entry = new()
        {
            Placeholder = placeholder,
            Keyboard = keyboard ?? Keyboard.Default,
            MaxLength = maxLength,
            FontSize = 15,
            Margin = new Thickness(0, 2, 0, 0)
        };
        entry.SetAppThemeColor(Entry.BackgroundColorProperty, Colors.White, Color.FromArgb("#404040"));
        return entry;
    }

    public static Button Primary(string text, EventHandler onClick)
    {
        Button button = new()
        {
            Text = text,
            BackgroundColor = Accent,
            TextColor = Colors.White,
            BorderWidth = 0,
            CornerRadius = 12,
            Padding = new Thickness(18, 14),
            MinimumHeightRequest = 48,
            FontSize = 15,
            FontFamily = "OpenSansSemibold"
        };
        button.Clicked += onClick;
        button.Pressed += (_, _) => button.BackgroundColor = AccentDark;
        button.Released += (_, _) => button.BackgroundColor = Accent;
        return button;
    }

    public static Button Secondary(string text, EventHandler onClick)
    {
        Button button = new()
        {
            Text = text,
            BackgroundColor = Colors.Transparent,
            TextColor = Accent,
            BorderColor = Accent,
            BorderWidth = 1.5,
            CornerRadius = 12,
            Padding = new Thickness(16, 12),
            MinimumHeightRequest = 44,
            FontSize = 14,
            FontFamily = "OpenSansSemibold"
        };
        button.SetAppThemeColor(Button.BackgroundColorProperty, Color.FromArgb("#FFF7ED"), Color.FromArgb("#292524"));
        button.Clicked += onClick;
        return button;
    }

    public static Button Compact(string text, EventHandler onClick)
    {
        Button button = new()
        {
            Text = text,
            FontSize = 13,
            Padding = new Thickness(12, 8),
            CornerRadius = 10,
            MinimumHeightRequest = 40,
            BorderWidth = 0,
            BackgroundColor = Color.FromArgb("#FFEDD5"),
            TextColor = AccentDark,
            FontFamily = "OpenSansSemibold"
        };
        button.Clicked += onClick;
        return button;
    }

    public static Button IconAction(string symbol, EventHandler onClick, bool accent = false)
    {
        Button button = new()
        {
            Text = symbol,
            FontSize = 20,
            WidthRequest = 48,
            HeightRequest = 48,
            CornerRadius = 14,
            Padding = 0,
            BorderWidth = 0,
            VerticalOptions = LayoutOptions.Center,
            HorizontalOptions = LayoutOptions.Center
        };
        if (accent)
        {
            button.BackgroundColor = Accent;
            button.TextColor = Colors.White;
        }
        else
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty, Color.FromArgb("#F5F5F4"), Color.FromArgb("#3F3F46"));
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#44403C"), Colors.White);
        }

        button.Clicked += onClick;
        return button;
    }

    // Equal-width columns so two long labels still fit side by side on a narrow phone.
    public static View ActionRow(params View[] buttons)
    {
        Grid row = new() { ColumnSpacing = 8 };
        for (int i = 0; i < buttons.Length; i++)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
            if (buttons[i] is Button button)
            {
                button.LineBreakMode = LineBreakMode.WordWrap;
                button.Padding = new Thickness(8, 10);
            }

            row.Add(buttons[i], i, 0);
        }

        return row;
    }

    public static Border ComposerShell(View inner)
    {
        Border shell = new()
        {
            Padding = new Thickness(10, 8),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Margin = new Thickness(12, 4, 12, 12),
            Content = inner
        };
        shell.SetAppThemeColor(VisualElement.BackgroundColorProperty, Colors.White, SurfaceDark);
        shell.SetAppTheme<Brush>(Border.StrokeProperty,
            new SolidColorBrush(Color.FromArgb("#D6D3D1")), new SolidColorBrush(Color.FromArgb("#52525B")));
        return shell;
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
