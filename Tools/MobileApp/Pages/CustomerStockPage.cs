using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// What one customer holds: count per gas type and size, days since delivery and every cylinder's barcode. Read-only.
public sealed class CustomerStockPage : ContentPage
{
    private readonly Label _customerInfo = new()
    {
        FontSize = 15,
        FontFamily = "OpenSansSemibold",
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "🔎 Önce müşteriyi arayıp seçin."
    };
    private readonly SearchBar _filter = new() { Placeholder = "Barkod, seri no veya gaz ara", IsVisible = false };
    private readonly VerticalStackLayout _summary = new() { Spacing = 8 };
    private readonly VerticalStackLayout _groups = new() { Spacing = 8 };
    private readonly VerticalStackLayout _cylinders = new() { Spacing = 8 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<(string Key, Border Card)> _groupCards = [];
    private CustomerStockResponse? _stock;
    private string? _picked;
    private readonly bool _pickOnOpen;
    private bool _openedPick;
    private bool _picking;

    public CustomerStockPage(CustomerItem? customer = null)
    {
        Title = customer is null ? "Müşteri Stoku" : customer.Name;
        Ui.ApplyPageChrome(this);
        _pickOnOpen = customer is null;
        _filter.TextChanged += (_, _) => ShowCylinders();

        VerticalStackLayout stack = new() { Padding = new Thickness(16, 12, 16, 24), Spacing = 14 };
        if (customer is null)
        {
            stack.Children.Add(Ui.Section("Müşteri",
                _customerInfo,
                Ui.Primary("🔎 Müşteri ara / seç", async (_, _) => await PickCustomerAsync())));
        }

        stack.Children.Add(_busy);
        stack.Children.Add(_summary);
        stack.Children.Add(_groups);
        stack.Children.Add(_filter);
        stack.Children.Add(_cylinders);
        Content = new ScrollView { Content = stack };

        if (customer is not null)
        {
            Loaded += async (_, _) =>
            {
                if (_stock is null)
                {
                    await LoadAsync(customer.Id);
                }
            };
        }
        else
        {
            Loaded += async (_, _) =>
            {
                if (_pickOnOpen && _stock is null && !_openedPick)
                {
                    _openedPick = true;
                    await PickCustomerAsync();
                }
            };
        }
    }

    private async Task PickCustomerAsync()
    {
        if (_picking)
        {
            return;
        }

        _picking = true;
        try
        {
            CustomerItem? picked = await CustomerPickPage.OpenAsync(Navigation, "Müşteri stoku");
            if (picked is null)
            {
                if (_stock is null && Navigation.NavigationStack.Count > 1 && Navigation.NavigationStack[^1] == this)
                {
                    await Navigation.PopAsync();
                }

                return;
            }

            Title = picked.Name;
            _customerInfo.Text = $"{picked.Name}\n{picked.Subtitle}";
            await LoadAsync(picked.Id);
        }
        finally
        {
            _picking = false;
        }
    }

    private Task LoadAsync(int customerId) => Ui.RunAsync(this, _busy, async () =>
    {
        _stock = await ApiClient.Instance.CustomerStockAsync(customerId);
        ShowStock();
    });

    private void ShowStock()
    {
        CustomerStockResponse s = _stock!;
        CustomerStockTotals t = s.Totals;
        string place = string.Join(" / ", new[] { s.City, s.District }.Where(v => !string.IsNullOrWhiteSpace(v)));
        VerticalStackLayout card = new()
        {
            Spacing = 8,
            Children =
            {
                new Label { Text = "👤 " + s.Name, FontSize = 18, FontFamily = "OpenSansSemibold" },
                Ui.Note(string.Join(" · ", new[] { s.Code, place, s.Phone }.Where(v => !string.IsNullOrWhiteSpace(v)))),
                Ui.Note($"🛢️ {t.Count} tüp · dolu {t.Full} · boş {t.Empty}" + (t.Unknown > 0 ? $" · bilinmiyor {t.Unknown}" : string.Empty)),
                Ui.Note(t.MaxDays is int max ? $"⏳ En uzun bekleyen: {max} gün" + (t.AvgDays is int avg ? $" (ortalama {avg})" : string.Empty) : "Teslim tarihi yok")
            }
        };
        if (t.Long > 0)
        {
            card.Children.Add(new Label { Text = $"⚠️ {t.Long} tüp {s.LongDays} günden uzun süredir müşteride", TextColor = Ui.Warn, FontFamily = "OpenSansSemibold", FontSize = 14 });
        }

        if (t.CustomerOwned > 0)
        {
            card.Children.Add(Ui.Note($"🏷️ {t.CustomerOwned} tüp müşteri malı"));
        }

        if (!s.IsActive)
        {
            card.Children.Add(new Label { Text = "⛔ Pasif müşteri kaydı", TextColor = Ui.Bad, FontSize = 14 });
        }

        _summary.Children.Clear();
        _summary.Children.Add(Ui.Card(card));

        _groups.Children.Clear();
        _groupCards.Clear();
        _picked = null;
        if (s.Groups.Count > 0)
        {
            _groups.Children.Add(Ui.SectionCaption("Gaz türü ve boyuta göre"));
            _groups.Children.Add(Ui.Note("Bir gruba dokunun: aşağıda yalnızca o grubun tüpleri listelenir. Tekrar dokununca tümü gelir."));
        }

        foreach (CustomerStockGroupItem g in s.Groups)
        {
            Grid row = new() { ColumnSpacing = 12, ColumnDefinitions = [new ColumnDefinition(new GridLength(56)), new ColumnDefinition(GridLength.Star)] };
            row.Add(new Label
            {
                Text = g.Count.ToString(),
                FontSize = 24,
                FontFamily = "OpenSansSemibold",
                TextColor = g.Long > 0 ? Ui.Warn : Ui.Accent,
                HorizontalTextAlignment = TextAlignment.Center,
                VerticalTextAlignment = TextAlignment.Center
            }, 0, 0);
            row.Add(new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                Children = { new Label { Text = g.Title, FontSize = 16, FontFamily = "OpenSansSemibold" }, Ui.Note(g.Subtitle) }
            }, 1, 0);
            Border groupCard = Ui.Card(row);
            TapGestureRecognizer tap = new();
            string key = GroupKey(g.GasType, g.Capacity);
            tap.Tapped += (_, _) =>
            {
                _picked = _picked == key ? null : key;
                MarkPicked();
                ShowCylinders();
            };
            groupCard.GestureRecognizers.Add(tap);
            _groupCards.Add((key, groupCard));
            _groups.Children.Add(groupCard);
        }

        _filter.Text = string.Empty;
        _filter.IsVisible = s.Cylinders.Count > 0;
        ShowCylinders();
    }

    private void ShowCylinders()
    {
        _cylinders.Children.Clear();
        if (_stock is null)
        {
            return;
        }

        string term = (_filter.Text ?? string.Empty).Trim();
        List<CustomerStockCylinderItem> rows = _stock.Cylinders
            .Where(c => _picked is null || GroupKey(c.GasType, c.Capacity) == _picked)
            .Where(c => term.Length == 0 || new[] { c.SerialNumber, c.Barcode, c.ManufacturerSerial, c.GasType }
                .Any(v => v is not null && v.Contains(term, StringComparison.CurrentCultureIgnoreCase)))
            .ToList();
        _cylinders.Children.Add(Ui.SectionCaption(term.Length == 0
            ? $"Tüpler ({rows.Count}) · gruba göre, en uzun bekleyen önce"
            : $"'{term}' için {rows.Count} tüp"));
        if (rows.Count == 0)
        {
            _cylinders.Children.Add(Ui.Note(_stock.Cylinders.Count == 0 ? "Bu müşteride kayıtlı tüp yok." : "Aramaya uyan tüp yok."));
            return;
        }

        List<string> order = _stock.Groups.Select(g => GroupKey(g.GasType, g.Capacity)).ToList();
        IEnumerable<IGrouping<string, CustomerStockCylinderItem>> sections = rows
            .GroupBy(c => GroupKey(c.GasType, c.Capacity))
            .OrderBy(g => order.IndexOf(g.Key) is int i and >= 0 ? i : int.MaxValue);
        foreach (IGrouping<string, CustomerStockCylinderItem> section in sections)
        {
            CustomerStockCylinderItem first = section.First();
            int full = section.Count(c => c.Fullness == "Dolu");
            int empty = section.Count(c => c.Fullness == "Boş");
            _cylinders.Children.Add(new Label
            {
                Text = $"{first.GasType.Trim()} {first.Capacity} · {section.Count()} adet · dolu {full} · boş {empty}",
                FontSize = 15,
                FontFamily = "OpenSansSemibold",
                TextColor = Ui.Accent,
                Margin = new Thickness(2, 10, 0, 0)
            });
            foreach (CustomerStockCylinderItem c in section)
            {
                _cylinders.Children.Add(CylinderCard(c));
            }
        }
    }

    private static string GroupKey(string gasType, string capacity) =>
        $"{gasType.Trim().ToUpper(new System.Globalization.CultureInfo("tr-TR"))}|{capacity}";

    private void MarkPicked()
    {
        foreach ((string key, Border card) in _groupCards)
        {
            if (key == _picked)
            {
                card.StrokeThickness = 3;
                card.Stroke = new SolidColorBrush(Ui.Accent);
            }
            else
            {
                card.StrokeThickness = 1;
                card.SetAppTheme<Brush>(Border.StrokeProperty,
                    new SolidColorBrush(Color.FromArgb("#E5E7EB")), new SolidColorBrush(Color.FromArgb("#3F3F46")));
            }
        }
    }

    private Border CylinderCard(CustomerStockCylinderItem c)
    {
        VerticalStackLayout body = new()
        {
            Spacing = 3,
            Children = { new Label { Text = c.Title, FontSize = 16, FontFamily = "OpenSansSemibold" }, Ui.Note(c.Subtitle) }
        };
        if (c.Codes.Length > 0)
        {
            body.Children.Add(Ui.Note(c.Codes));
        }

        if (c.Days >= _stock!.LongDays)
        {
            ((Label)body.Children[1]).TextColor = Ui.Warn;
        }

        Border card = Ui.Card(body);
        TapGestureRecognizer tap = new();
        string code = c.SerialNumber;
        tap.Tapped += async (_, _) => await Navigation.PushAsync(new CylinderInfoPage(code));
        card.GestureRecognizers.Add(tap);
        return card;
    }
}
