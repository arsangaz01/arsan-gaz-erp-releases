using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Barcodes and details of the cylinders of one gas type in a vehicle or trip load.
public sealed class CylinderGroupPage : ContentPage
{
    private readonly SearchBar _search = new() { Placeholder = "Barkod, seri no veya müşteri ara" };
    private readonly CollectionView _list;
    private readonly Label _summary;
    private readonly GasGroup _group;

    public CylinderGroupPage(string title, GasGroup group)
    {
        Title = title;
        _group = group;
        Ui.ApplyPageChrome(this);
        _summary = Ui.Note($"{group.GasType} · {group.Summary}");
        _list = new CollectionView
        {
            SelectionMode = SelectionMode.None,
            EmptyView = Ui.Note("Bu türde tüp yok."),
            ItemTemplate = new DataTemplate(() =>
            {
                Label name = new() { FontSize = 16, FontFamily = "OpenSansSemibold" };
                name.SetBinding(Label.TextProperty, nameof(LoadCylinder.Title));
                Label detail = Ui.Note(string.Empty);
                detail.SetBinding(Label.TextProperty, nameof(LoadCylinder.Subtitle));
                Border card = Ui.Card(new VerticalStackLayout { Spacing = 3, Children = { name, detail } });
                return new ContentView { Padding = new Thickness(0, 0, 0, 8), Content = card };
            }),
            ItemsSource = group.Cylinders
        };
        _search.TextChanged += (_, _) => ApplyFilter();

        Grid root = new()
        {
            Padding = new Thickness(16, 12, 16, 0),
            RowSpacing = 8,
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star)]
        };
        root.Add(_search, 0, 0);
        root.Add(_summary, 0, 1);
        root.Add(_list, 0, 2);
        Content = root;
    }

    private void ApplyFilter()
    {
        string term = (_search.Text ?? string.Empty).Trim();
        _list.ItemsSource = term.Length == 0
            ? _group.Cylinders
            : _group.Cylinders.Where(c => new[] { c.Barcode, c.SerialNumber, c.Customer, c.Plate, c.State }
                .Any(v => v is not null && v.Contains(term, StringComparison.CurrentCultureIgnoreCase))).ToList();
    }

    // One tappable card per gas type: count, name and full/empty summary; tapping opens the cylinder list.
    public static View GroupList(INavigation navigation, IReadOnlyList<GasGroup> groups, string empty, string titlePrefix)
    {
        VerticalStackLayout stack = new() { Spacing = 8 };
        if (groups.Count == 0)
        {
            stack.Children.Add(Ui.Note(empty));
            return stack;
        }

        foreach (GasGroup group in groups)
        {
            Grid grid = new()
            {
                ColumnSpacing = 12,
                ColumnDefinitions = [new ColumnDefinition(new GridLength(64)), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
            };
            grid.Add(new Label
            {
                Text = group.Count.ToString(),
                FontSize = 26,
                FontFamily = "OpenSansSemibold",
                TextColor = group.Alarm > 0 ? Colors.Red : Ui.Accent,
                VerticalTextAlignment = TextAlignment.Center,
                HorizontalTextAlignment = TextAlignment.Center
            }, 0, 0);
            Label summary = Ui.Note(group.Summary);
            if (group.Alarm > 0)
            {
                summary.TextColor = Colors.Red;
            }

            grid.Add(new VerticalStackLayout
            {
                VerticalOptions = LayoutOptions.Center,
                Children = { new Label { Text = group.GasType, FontSize = 17, FontFamily = "OpenSansSemibold" }, summary }
            }, 1, 0);
            grid.Add(new Label { Text = "›", FontSize = 26, TextColor = Colors.Gray, VerticalTextAlignment = TextAlignment.Center }, 2, 0);

            Border card = Ui.Card(grid);
            TapGestureRecognizer tap = new();
            GasGroup captured = group;
            tap.Tapped += async (_, _) => await navigation.PushAsync(new CylinderGroupPage($"{titlePrefix} · {captured.GasType}", captured));
            card.GestureRecognizers.Add(tap);
            stack.Children.Add(card);
        }

        return stack;
    }
}
