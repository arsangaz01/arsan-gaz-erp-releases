using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

public sealed class SummaryPage : ContentPage
{
    private readonly ContentView _body = new();
    private bool _loaded;
    private readonly Label _heading = Ui.Title("Genel özet");
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public SummaryPage()
    {
        Title = "Özet";
        Grid buttons = new()
        {
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
            ColumnSpacing = 8,
            RowSpacing = 8
        };
        (string Text, string Heading, string Path, string Permission)[] reports =
        [
            ("📊 Genel özet", "📊 Genel özet", "/api/ozet", Perm.Summary),
            ("📅 Sipariş tahmini", "📅 Sipariş tahmini", "/api/tahmin", Perm.Reports),
            ("🧪 Test tarihi yaklaşan", "🧪 Test tarihi 30 gün içinde", "/api/test-tarihleri?gun=30", Perm.Reports),
            ("🔄 Son hareketler", "🔄 Son 7 gün hareketleri", "/api/hareketler?gun=7", Perm.MovementHistory),
            ("👥 Müşterideki tüpler", "👥 Müşterideki tüpler", "/api/musterideki-tupler", Perm.Reports)
        ];
        int index = 0;
        foreach (var report in reports.Where(r => ApiClient.Instance.Can(r.Permission)))
        {
            if (index % 2 == 0)
            {
                buttons.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            buttons.Add(Ui.Secondary(report.Text, async (_, _) => await ShowAsync(report.Heading, report.Path)), index % 2, index / 2);
            index++;
        }

        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Section("Rapor seçin", buttons),
                    _busy,
                    _heading,
                    _body
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded && ApiClient.Instance.Can(Perm.Summary) && await ApiClient.Instance.HasSessionAsync())
        {
            await ShowAsync("📊 Genel özet", "/api/ozet");
        }
    }

    private Task ShowAsync(string heading, string path) => Ui.RunAsync(this, _busy, async () =>
    {
        _heading.Text = heading;
        _body.Content = Ui.Note("Yükleniyor...");
        try
        {
            _body.Content = ReportView.Build(await ApiClient.Instance.TextAsync(path));
            _loaded = true;
        }
        catch
        {
            _body.Content = null;
            throw;
        }
    });
}
