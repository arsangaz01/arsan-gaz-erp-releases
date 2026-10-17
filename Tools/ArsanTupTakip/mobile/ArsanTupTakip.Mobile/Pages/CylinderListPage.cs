using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

public sealed class CylinderListPage : ContentPage
{
    private readonly int? _status;
    private readonly string? _plate;
    private readonly bool _longAtCustomer;
    private readonly SearchBar _search = new() { Placeholder = "Seri no, gaz, müşteri veya plaka ara" };
    private readonly CollectionView _list;
    private readonly Label _summary = Ui.Note(string.Empty);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private List<CylinderListItem> _items = [];

    public CylinderListPage(string title, int? status, string? plate, bool longAtCustomer)
    {
        Title = title;
        _status = status;
        _plate = plate;
        _longAtCustomer = longAtCustomer;
        _list = new CollectionView
        {
            SelectionMode = SelectionMode.Single,
            EmptyView = Ui.Note("Bu listede tüp yok. Satıra dokununca tüp detayı açılır."),
            ItemTemplate = new DataTemplate(() =>
            {
                Label name = new() { FontSize = 16, FontFamily = "OpenSansSemibold" };
                name.SetBinding(Label.TextProperty, nameof(CylinderListItem.Title));
                Label detail = Ui.Note(string.Empty);
                detail.SetBinding(Label.TextProperty, nameof(CylinderListItem.Subtitle));
                Border card = Ui.Card(new VerticalStackLayout { Spacing = 3, Children = { name, detail } });
                return new ContentView { Padding = new Thickness(0, 0, 0, 8), Content = card };
            })
        };
        _list.SelectionChanged += OnCylinderSelected;
        _search.TextChanged += (_, _) => ApplyFilter();
        Ui.ApplyPageChrome(this);

        Grid root = new()
        {
            Padding = new Thickness(16, 12, 16, 0),
            RowSpacing = 8,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)]
        };
        root.Add(_search, 0, 0);
        root.Add(_summary, 0, 1);
        root.Add(_busy, 0, 2);
        root.Add(_list, 0, 3);
        Content = root;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await Ui.RunAsync(this, _busy, async () =>
        {
            _items = await ApiClient.Instance.CylinderListAsync(_status, _plate, _longAtCustomer);
            ApplyFilter();
        });
    }

    private void ApplyFilter()
    {
        string term = (_search.Text ?? string.Empty).Trim();
        List<CylinderListItem> shown = term.Length == 0
            ? _items
            : _items.Where(item => Matches(item, term)).ToList();
        _list.ItemsSource = shown;
        _summary.Text = _items.Count >= 300
            ? $"İlk 300 tüp gösteriliyor · {shown.Count} eşleşme"
            : $"{shown.Count} tüp" + (_longAtCustomer || _status == CylinderPage.CustomerStatus ? " · en uzun süredir duranlar üstte" : string.Empty);
    }

    private static bool Matches(CylinderListItem item, string term) =>
        new[] { item.SerialNumber, item.Barcode, item.GasType, item.Customer, item.Plate }
            .Any(value => value is not null && value.Contains(term, StringComparison.CurrentCultureIgnoreCase));

    private async void OnCylinderSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_list.SelectedItem is not CylinderListItem item)
        {
            return;
        }

        _list.SelectedItem = null;
        await Navigation.PushAsync(new CylinderInfoPage(item.SerialNumber));
    }
}
