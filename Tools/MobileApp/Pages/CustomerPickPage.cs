using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Full-screen customer search: results as cards, one tap chooses.
public sealed class CustomerPickPage : ContentPage
{
    private readonly TaskCompletionSource<CustomerItem?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Entry _search = Ui.TextField("Müşteri adı, kodu veya şehri");
    private readonly Label _info = new() { FontSize = 14, FontFamily = "OpenSansSemibold", TextColor = Ui.AccentDark, LineBreakMode = LineBreakMode.WordWrap };
    private readonly VerticalStackLayout _list = new() { Spacing = 10 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    private CustomerItem? _picked;
    private bool _done;

    private CustomerPickPage(string title, string? query)
    {
        Title = title;
        Ui.ApplyPageChrome(this);
        _search.ReturnType = ReturnType.Search;
        _search.FontSize = 18;
        _search.Text = query;
        _search.Completed += async (_, _) => await SearchAsync();
        _info.Text = "🔎 Müşterinin adını, kodunu veya şehrini yazın (en az 2 harf).";

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 12,
                Children =
                {
                    Ui.Card(new VerticalStackLayout
                    {
                        Spacing = 10,
                        Children = { _search, Ui.Primary("🔎  ARA", async (_, _) => await SearchAsync()) }
                    }),
                    _info,
                    _busy,
                    _list
                }
            }
        };
    }

    public static async Task<CustomerItem?> OpenAsync(INavigation navigation, string title, string? query = null)
    {
        CustomerPickPage page = new(title, query);
        await navigation.PushAsync(page);
        return await page._result.Task;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        _search.Focus();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_done)
        {
            _done = true;
            _result.TrySetResult(_picked);
        }
    }

    private async Task SearchAsync()
    {
        string query = (_search.Text ?? string.Empty).Trim();
        if (query.Length < Ui.LiveSearchMinLength)
        {
            _info.Text = "🔎 En az 2 harf yazın.";
            _info.TextColor = Ui.Warn;
            return;
        }

        _busy.IsVisible = true;
        try
        {
            List<CustomerItem> found = await ApiClient.Instance.CustomersAsync(query);
            if (_done || Handler is null)
            {
                return;
            }

            _list.Children.Clear();
            foreach (CustomerItem customer in found)
            {
                _list.Children.Add(CustomerCard(customer));
            }

            _info.Text = found.Count == 0
                ? $"❌ \"{query}\" ile eşleşen müşteri yok."
                : $"✅ {found.Count} müşteri bulundu — seçmek için dokunun.";
            _info.TextColor = found.Count == 0 ? Ui.Bad : Ui.Good;
        }
        catch (ObjectDisposedException)
        {
            // Back pressed while search was in flight.
        }
        catch (Exception exception) when (Handler is not null && !_done)
        {
            _info.Text = "⚠️ Arama yapılamadı: " + exception.Message;
            _info.TextColor = Ui.Bad;
        }
        finally
        {
            if (Handler is not null && !_done)
            {
                _busy.IsVisible = false;
            }
        }
    }

    private View CustomerCard(CustomerItem customer)
    {
        Label initial = new()
        {
            Text = customer.Name.Trim().Length > 0 ? customer.Name.Trim()[..1].ToUpperInvariant() : "?",
            FontSize = 22,
            FontFamily = "OpenSansSemibold",
            TextColor = Colors.White,
            HorizontalTextAlignment = TextAlignment.Center,
            VerticalTextAlignment = TextAlignment.Center
        };
        Border badge = new()
        {
            WidthRequest = 46,
            HeightRequest = 46,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 23 },
            BackgroundColor = customer.IsActive ? Ui.Accent : Ui.Muted,
            VerticalOptions = LayoutOptions.Center,
            Content = initial
        };

        HorizontalStackLayout chips = new() { Spacing = 6 };
        if (!string.IsNullOrWhiteSpace(customer.Code))
        {
            chips.Children.Add(Chip("#" + customer.Code, Ui.Indigo));
        }

        if (!string.IsNullOrWhiteSpace(customer.City))
        {
            chips.Children.Add(Chip("📍 " + customer.City, Ui.AccentDark));
        }

        chips.Children.Add(Chip($"🛢️ {customer.Cylinders} tüp", customer.Cylinders > 0 ? Ui.Good : Ui.Muted));

        Grid grid = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        grid.Add(badge, 0, 0);
        grid.Add(new VerticalStackLayout
        {
            Spacing = 6,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = customer.Name, FontSize = 17, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap },
                chips
            }
        }, 1, 0);
        grid.Add(new Label { Text = "›", FontSize = 28, TextColor = Ui.Accent, VerticalTextAlignment = TextAlignment.Center }, 2, 0);

        Border card = Ui.Card(grid);
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            if (_done)
            {
                return;
            }

            _done = true;
            _picked = customer;
            _result.TrySetResult(customer);
            await Navigation.PopAsync();
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private static Border Chip(string text, Color color) => new()
    {
        Padding = new Thickness(8, 3),
        StrokeThickness = 0,
        StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 10 },
        BackgroundColor = color.WithAlpha(0.14f),
        Content = new Label { Text = text, FontSize = 12, FontFamily = "OpenSansSemibold", TextColor = color }
    };
}
