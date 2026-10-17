using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Dolum: doldurulan tüpler parti/lot numarasıyla okutulur, depoda dolu olur; kim ne zaman doldurdu kaydedilir.
public sealed class FillPage : ContentPage
{
    private readonly Entry _lot = new() { Placeholder = "Parti / lot no (ör. OKS-2610-01)", MaxLength = 60 };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı)", MaxLength = 300 };
    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no", ReturnType = ReturnType.Done };
    private readonly Label _scannedText = new() { FontSize = 14 };
    private readonly Label _result = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Border _resultCard;
    private readonly Label _recent = new() { FontSize = 13, LineBreakMode = LineBreakMode.WordWrap, TextColor = Ui.Muted };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<string> _scanned = [];

    public FillPage()
    {
        Title = "Dolum";
        Ui.ApplyPageChrome(this);
        _resultCard = Ui.Card(_result);
        _resultCard.IsVisible = false;
        _code.Completed += (_, _) => AddManual();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Doldurduğunuz tüpleri okutun ve \"Dolumu kaydet\"e basın: tüpler depoda dolu olur, parti/lot numarası ve sizin adınız kaydedilir. Müşteride görünen tüp doldurulamaz. Periyodik testi geçmiş tüp için ayrıca onay istenir."),
                    Ui.Section("Dolum bilgisi", _lot, _note),
                    Ui.Section(
                        "Okutma",
                        Ui.ActionRow(
                            Ui.Primary("📷 Okut", async (_, _) => await ScanAsync(false)),
                            Ui.Primary("📷 Toplu okut", async (_, _) => await ScanAsync(true))),
                        _code,
                        Ui.ActionRow(
                            Ui.Secondary("Kodu ekle", (_, _) => AddManual()),
                            Ui.Secondary("Listeyi temizle", (_, _) => { _scanned.Clear(); RefreshScanned(); })),
                        _scannedText,
                        Ui.Primary("Dolumu kaydet", async (_, _) => await SaveAsync(false))),
                    _busy,
                    _resultCard,
                    Ui.Section("Bugünkü dolumlar", _recent, Ui.Secondary("Yenile", async (_, _) => await RefreshRecentAsync()))
                }
            }
        };

        RefreshScanned();
        _ = RefreshRecentAsync();
    }

    private void RefreshScanned() =>
        _scannedText.Text = _scanned.Count == 0 ? "Henüz tüp okutulmadı." : $"{_scanned.Count} tüp:\n{string.Join(", ", _scanned)}";

    private void AddManual()
    {
        string code = (_code.Text ?? string.Empty).Trim();
        if (code.Length > 0)
        {
            Add([code]);
            _code.Text = string.Empty;
        }
    }

    private void Add(IEnumerable<string> codes)
    {
        foreach (string code in codes.Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            if (!_scanned.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                _scanned.Add(code);
            }
        }

        RefreshScanned();
    }

    private async Task ScanAsync(bool many)
    {
        try
        {
            Add(many ? await ScanPage.ScanManyAsync(Navigation, _scanned) : await ScanPage.ScanOneOrManyAsync(Navigation, _scanned));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task SaveAsync(bool allowOverdueTest)
    {
        if (_scanned.Count == 0)
        {
            await DisplayAlertAsync("Dolum", "En az bir tüp okutun.", "Tamam");
            return;
        }

        _busy.IsVisible = true;
        try
        {
            FillResponse result = await ApiClient.Instance.FillAsync(_scanned, _lot.Text, _note.Text, allowOverdueTest);
            List<string> failed = result.Lines.Where(l => !l.Ok).Select(l => l.Code).ToList();
            _result.Text = string.Join(Environment.NewLine, [result.Message, .. result.Lines.Select(l => (l.Ok ? "✔ " : "✖ ") + l.Message)]);
            _result.TextColor = failed.Count > 0 ? Ui.Bad : Ui.Muted;
            _resultCard.IsVisible = true;
            _scanned.Clear();
            _scanned.AddRange(failed);
            RefreshScanned();
            _busy.IsVisible = false;
            if (result.NeedsTestConfirmation.Count > 0
                && await DisplayAlertAsync("Testi geçmiş tüp",
                    $"{string.Join(", ", result.NeedsTestConfirmation)} tüplerinin periyodik test tarihi geçmiş. Yine de doldurulsun mu? Bu onay kayda geçer.",
                    "Evet, doldur", "Hayır"))
            {
                _scanned.Clear();
                _scanned.AddRange(result.NeedsTestConfirmation);
                RefreshScanned();
                await SaveAsync(true);
                return;
            }

            await RefreshRecentAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Dolum", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task RefreshRecentAsync()
    {
        try
        {
            List<FillItem> rows = await ApiClient.Instance.RecentFillsAsync();
            _recent.Text = rows.Count == 0
                ? "Bugün dolum kaydı yok."
                : $"{rows.Count} tüp:\n" + string.Join(Environment.NewLine, rows.Take(60).Select(r =>
                    $"{r.FilledAt} · {r.SerialNumber} ({r.GasType})" + (string.IsNullOrEmpty(r.LotNumber) ? string.Empty : $" · lot {r.LotNumber}") + (r.TestOverdue ? " · ⚠ test" : string.Empty)));
        }
        catch (Exception exception)
        {
            _recent.Text = "Liste alınamadı: " + exception.Message;
        }
    }
}
