using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Araç İşlemleri: registered vehicles with cylinders on board and active trip; tap a vehicle for its details.
public sealed class VehiclePage : ContentPage
{
    private readonly CollectionView _list;
    private readonly Label _summary = Ui.Note(string.Empty);
    private readonly Switch _showInactive = new() { OnColor = Ui.Accent };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public VehiclePage()
    {
        Title = "Araç İşlemleri";
        Ui.ApplyPageChrome(this);
        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            EmptyView = Ui.Note("Kayıtlı araç yok. \"+ Araç ekle\" ile ekleyin."),
            ItemTemplate = new DataTemplate(() =>
            {
                Label name = new() { FontSize = 17, FontFamily = "OpenSansSemibold" };
                name.SetBinding(Label.TextProperty, nameof(VehicleItem.Title));
                Label detail = Ui.Note(string.Empty);
                detail.SetBinding(Label.TextProperty, nameof(VehicleItem.Subtitle));
                Border card = Ui.Card(new VerticalStackLayout { Spacing = 3, Children = { name, detail } });
                return new ContentView { Padding = new Thickness(0, 0, 0, 8), Content = card };
            })
        };
        _list.SelectionChanged += async (_, e) =>
        {
            if (e.CurrentSelection.FirstOrDefault() is VehicleItem vehicle)
            {
                _list.SelectedItem = null;
                await Navigation.PushAsync(new VehicleDetailPage(vehicle.Id));
            }
        };
        _showInactive.Toggled += async (_, _) => await LoadAsync();

        Grid root = new()
        {
            Padding = new Thickness(16, 12, 16, 0),
            RowSpacing = 8,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)]
        };
        root.Add(Ui.ActionRow(
            Ui.Allowed(Ui.Primary("+ Araç ekle", async (_, _) => await Navigation.PushAsync(new VehicleEditPage(null))), "arac-yonetimi"),
            Ui.Secondary("Yenile", async (_, _) => await LoadAsync())), 0, 0);
        root.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { _showInactive, new Label { Text = "Pasif araçları da göster", VerticalTextAlignment = TextAlignment.Center, FontSize = 14 } }
        }, 0, 1);
        root.Add(_summary, 0, 2);
        root.Add(_busy, 0, 3);
        root.Add(_list, 0, 4);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Ui.RunAsync(this, _busy, async () =>
        {
            List<VehicleItem> vehicles = await ApiClient.Instance.VehiclesAsync(_showInactive.IsToggled);
            _list.ItemsSource = vehicles;
            int onTrip = vehicles.Count(v => v.ActiveTripId is not null);
            _summary.Text = $"{vehicles.Count} araç · {onTrip} seferde · araçlarda toplam {vehicles.Sum(v => v.CylindersOnBoard)} tüp. Ayrıntı için araca dokunun.";
        });
    }
}
