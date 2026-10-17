using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// One gas/size row of the depot count: the barcodes recorded in the warehouse that are still unscanned, the matched ones and
// the scanned cylinders that are not in the depot stock.
public sealed class StockCountGroupPage : ContentPage
{
    private const int MaxRows = 300;
    private static readonly Color Good = Color.FromArgb("#15803D");
    private static readonly Color Warn = Color.FromArgb("#B45309");
    private static readonly Color Bad = Color.FromArgb("#B91C1C");

    private readonly int _countId;
    private readonly string _gasType;
    private readonly string _capacity;
    private readonly Entry _search = new() { Placeholder = "Barkod / seri no ara", ReturnType = ReturnType.Search };
    private readonly Label _summary = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly VerticalStackLayout _lists = new() { Spacing = 14 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private StockCountGroupDetailInfo? _detail;

    public StockCountGroupPage(int countId, string title, string gasType, string capacity)
    {
        Title = title;
        _countId = countId;
        _gasType = gasType;
        _capacity = capacity;
        _search.TextChanged += (_, _) => Render();
        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Section(title, _summary, _search, Ui.Secondary("Yenile", async (_, _) => await LoadAsync())),
                    _busy,
                    _lists
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        _busy.IsRunning = _busy.IsVisible = true;
        try
        {
            _detail = await ApiClient.Instance.StockCountGroupAsync(_countId, _gasType, _capacity);
            Render();
        }
        catch (ApiException loginProblem) when (loginProblem.HasFlag("loginRequired") || loginProblem.HasFlag("mustChangePassword"))
        {
        }
        catch (Exception exception)
        {
            _summary.Text = "Liste alınamadı: " + exception.Message;
            _summary.TextColor = Bad;
        }
        finally
        {
            _busy.IsRunning = _busy.IsVisible = false;
        }
    }

    private void Render()
    {
        if (_detail is not StockCountGroupDetailInfo d)
        {
            return;
        }

        int expected = d.Matched.Count + d.Missing.Count;
        _summary.Text = $"Kayıtta depoda {expected} · eşleşen {d.Matched.Count} · henüz okutulmayan {d.Missing.Count}"
            + (d.Extra.Count > 0 ? $" · stokta olmayan (hata) {d.Extra.Count}" : string.Empty);
        _summary.TextColor = d.Extra.Count > 0 ? Bad : d.Missing.Count > 0 ? Warn : Good;

        string query = (_search.Text ?? string.Empty).Trim();
        _lists.Children.Clear();
        _lists.Children.Add(List("Henüz okutulmayan (kayıtta depoda)", d.Missing, query, Warn, "Bu gruptaki tüm tüpler okutuldu."));
        _lists.Children.Add(List("Eşleşen (okutuldu, depo stokunda)", d.Matched, query, Good, "Henüz eşleşen tüp yok."));
        if (d.Extra.Count > 0)
        {
            _lists.Children.Add(List("❌ Stokta olmayan (okutuldu ama kayıtta başka yerde)", d.Extra, query, Bad, string.Empty));
        }
    }

    private static View List(string title, List<StockCountGroupCylinder> all, string query, Color color, string empty)
    {
        List<StockCountGroupCylinder> rows = query.Length == 0
            ? all
            : all.Where(c => c.Barcode.Contains(query, StringComparison.OrdinalIgnoreCase) || c.SerialNumber.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        VerticalStackLayout body = new() { Spacing = 8 };
        foreach (StockCountGroupCylinder c in rows.Take(MaxRows))
        {
            body.Children.Add(Row(c, color));
        }

        if (rows.Count == 0)
        {
            body.Children.Add(Ui.Note(query.Length == 0 ? empty : "Aramaya uyan tüp yok."));
        }
        else if (rows.Count > MaxRows)
        {
            body.Children.Add(Ui.Note($"İlk {MaxRows} tüp gösteriliyor; diğerleri için barkodla arayın."));
        }

        string header = query.Length == 0 ? $"{title} · {all.Count}" : $"{title} · {rows.Count}/{all.Count}";
        return Ui.Section(header, body);
    }

    private static View Row(StockCountGroupCylinder c, Color color)
    {
        List<string> info = [c.Fullness, c.Place];
        if (c.At is not null)
        {
            info.Add($"okutma {c.At}" + (string.IsNullOrWhiteSpace(c.ScannedBy) ? string.Empty : $" · {c.ScannedBy}"));
        }

        VerticalStackLayout row = new()
        {
            Spacing = 1,
            Children =
            {
                new Label { Text = c.Barcode, FontSize = 16, FontFamily = "OpenSansSemibold", TextColor = color },
                Ui.Note((c.Barcode == c.SerialNumber ? string.Empty : $"Seri no {c.SerialNumber} · ") + $"{c.GasType} {c.Capacity}"),
                Ui.Note(string.Join(" · ", info.Where(x => !string.IsNullOrWhiteSpace(x) && x != "-")))
            }
        };
        return row;
    }
}
