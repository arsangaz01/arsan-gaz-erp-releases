namespace ArsanGazMobile.Services;

public static class Ui
{
    public static readonly Color Accent = Color.FromArgb("#0284C7");
    public static readonly Color AccentDark = Color.FromArgb("#1E3A8A");
    public static readonly Color Muted = Color.FromArgb("#6B7280");
    public static readonly Color Good = Color.FromArgb("#059669");
    public static readonly Color Warn = Color.FromArgb("#B45309");
    public static readonly Color Bad = Color.FromArgb("#DC2626");
    public static readonly Color Indigo = Color.FromArgb("#4F46E5");
    public const int LiveSearchMinLength = 2;
    public static readonly Color SurfaceLight = Colors.White;
    public static readonly Color SurfaceDark = Color.FromArgb("#262626");
    public static readonly Color PageLight = Color.FromArgb("#F3F4F6");
    public static readonly Color PageDark = Color.FromArgb("#171717");

    private static readonly Color SecondaryLight = Color.FromArgb("#EFF6FF");
    private static readonly Color SecondaryDark = Color.FromArgb("#1E293B");
    private static readonly Color SecondaryPressedLight = Color.FromArgb("#BFDBFE");
    private static readonly Color SecondaryPressedDark = Color.FromArgb("#334155");
    private static readonly Color CompactLight = Color.FromArgb("#E0F2FE");
    private static readonly Color CompactPressedLight = Color.FromArgb("#BAE6FD");
    private static readonly Color IconIdleLight = Color.FromArgb("#F3F4F6");
    private static readonly Color IconIdleDark = Color.FromArgb("#3F3F46");
    private static readonly Color IconPressedLight = Color.FromArgb("#D1D5DB");
    private static readonly Color IconPressedDark = Color.FromArgb("#52525B");

    public static void ApplyPageChrome(ContentPage page)
    {
        page.SetAppThemeColor(Page.BackgroundColorProperty, PageLight, PageDark);
        // Keep content below the status bar / notch (matches TupTakip field-app chrome).
        page.SafeAreaEdges = new SafeAreaEdges(SafeAreaRegions.Container);
    }

    public static Label Title(string text) => new()
    {
        Text = text,
        FontSize = 22,
        FontFamily = "OpenSansSemibold",
        Margin = new Thickness(0, 4, 0, 4)
    };

    public static Label SectionCaption(string text) => new()
    {
        Text = text,
        FontSize = 13,
        FontFamily = "OpenSansSemibold",
        TextColor = Accent,
        CharacterSpacing = 0.3,
        Margin = new Thickness(2, 0, 0, 6)
    };

    public static Border Card(View content)
    {
        Border card = new()
        {
            Padding = new Thickness(16, 14),
            StrokeThickness = 1,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Shadow = new Shadow
            {
                Brush = new SolidColorBrush(Colors.Black),
                Opacity = 0.08f,
                Radius = 10,
                Offset = new Point(0, 3)
            },
            Content = content
        };
        card.SetAppThemeColor(VisualElement.BackgroundColorProperty, SurfaceLight, SurfaceDark);
        card.SetAppTheme<Brush>(Border.StrokeProperty,
            new SolidColorBrush(Color.FromArgb("#E5E7EB")), new SolidColorBrush(Color.FromArgb("#3F3F46")));
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
        FontSize = 14,
        LineBreakMode = LineBreakMode.WordWrap,
        TextColor = Muted
    };

    public static View EmptyState(string title, string detail)
    {
        return Card(new VerticalStackLayout
        {
            Spacing = 8,
            Padding = new Thickness(8, 12),
            Children =
            {
                new Label
                {
                    Text = title,
                    FontSize = 17,
                    FontFamily = "OpenSansSemibold",
                    HorizontalTextAlignment = TextAlignment.Center
                },
                new Label
                {
                    Text = detail,
                    FontSize = 14,
                    TextColor = Muted,
                    HorizontalTextAlignment = TextAlignment.Center,
                    LineBreakMode = LineBreakMode.WordWrap
                }
            }
        });
    }

    public static Border MakeTappable(Border border, Func<Task> onTap)
    {
        bool busy = false;
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            if (busy)
            {
                return;
            }

            busy = true;
            try
            {
                await border.ScaleToAsync(0.97, 60, Easing.CubicOut);
                await onTap();
            }
            finally
            {
                await border.ScaleToAsync(1, 80, Easing.CubicOut);
                busy = false;
            }
        };
        border.GestureRecognizers.Add(tap);
        return border;
    }

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
            Padding = new Thickness(18, 15),
            MinimumHeightRequest = 52,
            FontSize = 16,
            FontFamily = "OpenSansSemibold"
        };
        button.Clicked += onClick;
        button.Pressed += (_, _) =>
        {
            button.BackgroundColor = AccentDark;
            button.Scale = 0.97;
        };
        button.Released += (_, _) =>
        {
            button.BackgroundColor = Accent;
            button.Scale = 1;
        };
        return button;
    }

    public static Button Secondary(string text, EventHandler onClick)
    {
        Button button = new()
        {
            Text = text,
            BackgroundColor = Colors.Transparent,
            TextColor = AccentDark,
            BorderColor = Accent,
            BorderWidth = 1.5,
            CornerRadius = 12,
            Padding = new Thickness(16, 13),
            MinimumHeightRequest = 48,
            FontSize = 15,
            FontFamily = "OpenSansSemibold"
        };
        button.SetAppThemeColor(Button.BackgroundColorProperty, SecondaryLight, SecondaryDark);
        button.Clicked += onClick;
        button.Pressed += (_, _) =>
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty, SecondaryPressedLight, SecondaryPressedDark);
            button.Scale = 0.97;
        };
        button.Released += (_, _) =>
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty, SecondaryLight, SecondaryDark);
            button.Scale = 1;
        };
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
            BackgroundColor = CompactLight,
            TextColor = AccentDark,
            FontFamily = "OpenSansSemibold"
        };
        button.Clicked += onClick;
        button.Pressed += (_, _) =>
        {
            button.BackgroundColor = CompactPressedLight;
            button.Scale = 0.97;
        };
        button.Released += (_, _) =>
        {
            button.BackgroundColor = CompactLight;
            button.Scale = 1;
        };
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
            button.SetAppThemeColor(Button.BackgroundColorProperty, IconIdleLight, IconIdleDark);
            button.SetAppThemeColor(Button.TextColorProperty, Color.FromArgb("#374151"), Colors.White);
        }

        button.Clicked += onClick;
        if (accent)
        {
            button.Pressed += (_, _) =>
            {
                button.BackgroundColor = AccentDark;
                button.Scale = 0.94;
            };
            button.Released += (_, _) =>
            {
                button.BackgroundColor = Accent;
                button.Scale = 1;
            };
        }
        else
        {
            button.Pressed += (_, _) =>
            {
                button.SetAppThemeColor(Button.BackgroundColorProperty, IconPressedLight, IconPressedDark);
                button.Scale = 0.94;
            };
            button.Released += (_, _) =>
            {
                button.SetAppThemeColor(Button.BackgroundColorProperty, IconIdleLight, IconIdleDark);
                button.Scale = 1;
            };
        }

        return button;
    }

    // Step buttons made with Secondary: the chosen one looks like a primary button.
    public static void Highlight(Button button, bool selected)
    {
        if (selected)
        {
            button.BackgroundColor = Accent;
            button.TextColor = Colors.White;
        }
        else
        {
            button.SetAppThemeColor(Button.BackgroundColorProperty, SecondaryLight, SecondaryDark);
            button.TextColor = AccentDark;
        }
    }

    public static Border StatTile(string icon, string value, string caption, Color color, string? detail = null, bool showChevron = false)
    {
        VerticalStackLayout body = new()
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = icon, FontSize = 22 },
                new Label { Text = value, FontSize = 30, FontFamily = "OpenSansSemibold", TextColor = color },
                new Label { Text = caption, FontSize = 14, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap }
            }
        };
        if (!string.IsNullOrWhiteSpace(detail))
        {
            body.Children.Add(Note(detail));
        }

        View content = body;
        if (showChevron)
        {
            Grid row = new()
            {
                ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                ColumnSpacing = 4
            };
            row.Add(body, 0, 0);
            row.Add(new Label
            {
                Text = "›",
                FontSize = 28,
                TextColor = color,
                VerticalTextAlignment = TextAlignment.Center,
                Opacity = 0.85
            }, 1, 0);
            content = row;
        }

        return new Border
        {
            Padding = new Thickness(14, 12),
            MinimumHeightRequest = 112,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 16 },
            BackgroundColor = color.WithAlpha(0.12f),
            Content = content
        };
    }

    public static Grid TileGrid(IReadOnlyList<View> tiles, int columns = 2)
    {
        Grid grid = new() { ColumnSpacing = 10, RowSpacing = 10 };
        for (int c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            if (i % columns == 0)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            grid.Add(tiles[i], i % columns, i / columns);
        }

        return grid;
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

    // Shows the view only if the signed-in user has the permission ("a|b" = any); the server checks it again.
    public static T Allowed<T>(T view, string permission) where T : VisualElement
    {
        view.IsVisible = ApiClient.Instance.Can(permission);
        return view;
    }

    // Hides a control the signed-in user has no permission for.
    public static T ShowIf<T>(this T view, bool visible) where T : VisualElement
    {
        view.IsVisible = visible;
        return view;
    }

    // True while the page is still attached; false after back/PopAsync (avoids ObjectDisposedException alerts).
    public static bool PageAlive(Page page) => page.Handler is not null && page.Window is not null;

    public static async Task RunAsync(Page page, ActivityIndicator? busy, Func<Task> action)
    {
        if (busy is not null && PageAlive(page))
        {
            busy.IsRunning = busy.IsVisible = true;
        }

        try
        {
            await action();
        }
        catch (ObjectDisposedException)
        {
            // User left the page while the request was in flight.
        }
        catch (ApiException exception) when (exception.HasFlag("loginRequired") || exception.HasFlag("mustChangePassword"))
        {
            // The sign-in or password screen takes over and shows the reason.
        }
        catch (ApiException exception) when (PageAlive(page))
        {
            await page.DisplayAlertAsync("Arsan Gaz ERP", exception.Message, "Tamam");
        }
        catch (Exception exception) when (PageAlive(page))
        {
            await page.DisplayAlertAsync("Hata", exception.Message, "Tamam");
        }
        catch (Exception)
        {
            // Page already gone; swallow so back navigation does not crash the app.
        }
        finally
        {
            if (busy is not null && PageAlive(page))
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
