using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

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
            SelectionMode = SelectionMode.None,
            EmptyView = Ui.Note("Bu listede tüp yok."),
            ItemTemplate = new DataTemplate(() =>
            {
                Label name = new() { FontSize = 16, FontFamily = "OpenSansSemibold" };
                name.SetBinding(Label.TextProperty, nameof(CylinderListItem.Title));
                Label detail = Ui.Note(string.Empty);
                detail.SetBinding(Label.TextProperty, nameof(CylinderListItem.Subtitle));
                return new VerticalStackLayout { Padding = new Thickness(4, 8), Spacing = 2, Children = { name, detail } };
            })
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        Grid root = new()
        {
            Padding = 16,
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
}
