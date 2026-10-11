using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class SummaryPage : ContentPage
{
    private readonly Label _text = new() { FontSize = 15, LineHeight = 1.3 };
    private readonly Label _heading = Ui.Title("Genel özet");
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public SummaryPage()
    {
        Title = "Özet";
        Grid buttons = new()
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)],
            ColumnSpacing = 8,
            RowSpacing = 8
        };
        buttons.Add(Ui.Secondary("Genel özet", async (_, _) => await ShowAsync("Genel özet", "/api/ozet")), 0, 0);
        buttons.Add(Ui.Secondary("Sipariş tahmini", async (_, _) => await ShowAsync("Sipariş tahmini", "/api/tahmin")), 1, 0);
        buttons.Add(Ui.Secondary("Test tarihi yaklaşan", async (_, _) => await ShowAsync("Test tarihi 30 gün içinde", "/api/test-tarihleri?gun=30")), 0, 1);
        buttons.Add(Ui.Secondary("Son hareketler", async (_, _) => await ShowAsync("Son 7 gün hareketleri", "/api/hareketler?gun=7")), 1, 1);
        buttons.Add(Ui.Secondary("Müşterideki tüpler", async (_, _) => await ShowAsync("Müşterideki tüpler", "/api/musterideki-tupler")), 0, 2);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children = { buttons, _busy, _heading, Ui.Card(_text) }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (string.IsNullOrEmpty(_text.Text) && await ApiClient.Instance.IsPairedAsync())
        {
            await ShowAsync("Genel özet", "/api/ozet");
        }
    }

    private Task ShowAsync(string heading, string path) => Ui.RunAsync(this, _busy, async () =>
    {
        _heading.Text = heading;
        _text.Text = "Yükleniyor...";
        try
        {
            _text.Text = (await ApiClient.Instance.TextAsync(path)).Replace("\r\n", "\n");
        }
        catch
        {
            _text.Text = string.Empty;
            throw;
        }
    });
}
