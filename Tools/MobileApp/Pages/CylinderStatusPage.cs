using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class CylinderStatusPage : ContentPage
{
    private readonly VerticalStackLayout _cards = new() { Spacing = 10 };
    private readonly Label _updated = Ui.Note(string.Empty);
    private readonly RefreshView _refresh;

    public CylinderStatusPage()
    {
        Title = "Tüp durumu";
        Ui.ApplyPageChrome(this);
        _refresh = new RefreshView
        {
            RefreshColor = Ui.Accent,
            Content = new ScrollView
            {
                Content = new VerticalStackLayout
                {
                    Padding = new Thickness(16, 12, 16, 24),
                    Spacing = 12,
                    Children = { Ui.Note("Karta dokunarak o durumdaki tüpleri listeleyin. Yenilemek için aşağı çekin."), _cards, _updated }
                }
            }
        };
        _refresh.Command = new Command(async () => await LoadAsync());
        Content = _refresh;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _refresh.IsRefreshing = true;
        try
        {
            CylinderOverview overview = await ApiClient.Instance.CylinderOverviewAsync();
            Render(overview);
            _updated.Text = $"Güncellendi: {DateTime.Now:HH:mm}";
        }
        catch (ApiException exception)
        {
            await DisplayAlertAsync("Tüp durumu", exception.Message, "Tamam");
        }
        finally
        {
            _refresh.IsRefreshing = false;
        }
    }

    private void Render(CylinderOverview overview)
    {
        _cards.Children.Clear();
        int total = overview.Statuses.Sum(s => s.Total);
        _cards.Children.Add(Ui.Title($"Toplam {total} tüp"));

        if (overview.LongAtCustomer > 0)
        {
            _cards.Children.Add(Tappable(
                Card($"⚠ {overview.LongAtCustomer}", $"{overview.LongAtCustomerDays}+ gündür müşteride", "Dönmeyen tüpleri görmek için dokunun", Color.FromArgb("#B45309")),
                () => new CylinderListPage($"{overview.LongAtCustomerDays}+ gündür müşteride", null, null, longAtCustomer: true)));
        }

        foreach (CylinderStatusCount status in overview.Statuses.Where(s => s.Total > 0 || s.Status != 6))
        {
            string split = status.Full + status.Empty > 0 ? $"dolu {status.Full} · boş {status.Empty}" : "dolu/boş bilgisi yok";
            _cards.Children.Add(Tappable(
                Card(status.Total.ToString(), status.Name, split, Ui.Accent),
                () => new CylinderListPage(status.Name, status.Status, null, longAtCustomer: false)));
        }

        if (overview.Vehicles.Count > 0)
        {
            _cards.Children.Add(Ui.SectionCaption("Araçlardaki tüpler"));
            foreach (CylinderPlateCount vehicle in overview.Vehicles)
            {
                _cards.Children.Add(Tappable(
                    Card(vehicle.Count.ToString(), "🚚 " + vehicle.Plate, "Araçtaki tüpleri görmek için dokunun", Ui.Accent),
                    () => new CylinderListPage(vehicle.Plate, CylinderPage.VehicleStatus, vehicle.Plate, longAtCustomer: false)));
            }
        }
    }

    private static Border Card(string count, string title, string subtitle, Color accent)
    {
        Grid grid = new()
        {
            ColumnSpacing = 14,
            ColumnDefinitions = [new ColumnDefinition(new GridLength(72)), new ColumnDefinition(GridLength.Star)]
        };
        grid.Add(new Label
        {
            Text = count,
            FontSize = 28,
            FontFamily = "OpenSansSemibold",
            TextColor = accent,
            VerticalTextAlignment = TextAlignment.Center,
            HorizontalTextAlignment = TextAlignment.Center
        }, 0, 0);
        grid.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = title, FontSize = 17, FontFamily = "OpenSansSemibold" },
                Ui.Note(subtitle)
            }
        }, 1, 0);
        grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));
        grid.Add(new Label { Text = "›", FontSize = 26, TextColor = Colors.Gray, VerticalTextAlignment = TextAlignment.Center }, 2, 0);
        return Ui.Card(grid);
    }

    private Border Tappable(Border card, Func<Page> open)
    {
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) => await Navigation.PushAsync(open());
        card.GestureRecognizers.Add(tap);
        return card;
    }
}
