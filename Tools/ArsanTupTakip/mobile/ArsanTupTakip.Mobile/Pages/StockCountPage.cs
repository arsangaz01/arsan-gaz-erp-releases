using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Main depot count: every scanned cylinder is counted once under its gas type and size; the panel compares the count
// with the cylinders the records show in the warehouse (Sayım Raporları).
public sealed class StockCountPage : ContentPage
{
    private static readonly Color Good = Color.FromArgb("#15803D");
    private static readonly Color Warn = Color.FromArgb("#B45309");
    private static readonly Color Bad = Color.FromArgb("#B91C1C");

    private readonly Label _status = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı), ör. ay sonu sayımı", MaxLength = 500 };
    private readonly Entry _code = new() { Placeholder = "Barkod / seri no (el terminali veya elle)", ReturnType = ReturnType.Send };
    private readonly Label _last = new() { FontSize = 15, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _totals = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly VerticalStackLayout _groups = new() { Spacing = 8 };
    private readonly Label _recent = Ui.Note(string.Empty);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly View _startSection;
    private readonly View _scanSection;
    private readonly View _resultSection;
    private StockCountInfo? _count;
    private string? _lastCode;
    private bool _sending;

    public StockCountPage()
    {
        Title = "Depo sayımı";
        _code.Completed += async (_, _) => await ManualAsync();

        _startSection = Ui.Section("Sayım başlat",
            Ui.Note("Açık sayım yok. Başlatınca depodaki tüpleri okutun; her okutulan tüp gaz türü ve boyutuna göre 1 sayılır. Aynı sayıma birden çok telefon aynı anda okutabilir."),
            _note,
            Ui.Primary("Depo sayımını başlat", async (_, _) => await StartAsync()));
        _scanSection = Ui.Section("Okut",
            _status,
            Ui.Primary("📷 Kamerayla okut", async (_, _) => await CameraAsync()),
            _code,
            Ui.ActionRow(
                Ui.Secondary("Son okutmayı geri al", async (_, _) => await UndoAsync()),
                Ui.Secondary("Yenile", async (_, _) => await LoadAsync())),
            _last,
            _totals,
            Ui.Secondary("✅ Sayımı bitir", async (_, _) => await CloseAsync()));
        _resultSection = Ui.Section("Gaz türü ve boyuta göre (sayılan / kayıtta depoda)", _groups);

        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    _busy,
                    _startSection,
                    _scanSection,
                    _resultSection,
                    Ui.Section("Son okutulanlar", _recent)
                }
            }
        };
        Show(null);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private Task LoadAsync() => Ui.RunAsync(this, _busy, async () => Show((await ApiClient.Instance.ActiveStockCountAsync()).Count));

    private Task StartAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        StockCountResponse response = await ApiClient.Instance.StartStockCountAsync(_note.Text);
        _note.Text = string.Empty;
        Show(response.Count);
        SetLast(response.Message, Good);
        _code.Focus();
    });

    private async Task CameraAsync()
    {
        if (_count is null)
        {
            return;
        }

        try
        {
            await ScanPage.ScanLiveAsync(Navigation, SendAsync, "Depodaki tüpleri sırayla okutun; her tüp 1 sayılır. Bitince \"Bitti\"ye basın.");
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task ManualAsync()
    {
        string code = (_code.Text ?? string.Empty).Trim();
        if (code.Length == 0 || _count is null || _sending)
        {
            return;
        }

        _code.Text = string.Empty;
        try
        {
            await SendAsync(code);
        }
        catch (SessionExpiredException)
        {
        }
        catch (Exception exception)
        {
            SetLast($"❌ {code}: {exception.Message}", Bad);
        }

        _code.Focus();
    }

    // Shared by the camera and the manual box; returns the line the camera screen shows.
    private async Task<string> SendAsync(string code)
    {
        if (_count is null)
        {
            throw new InvalidOperationException("Açık sayım yok.");
        }

        _sending = true;
        try
        {
            StockCountScanResponse response = await ApiClient.Instance.ScanStockCountAsync(_count.Id, code);
            if (response.Outcome != "duplicate")
            {
                _lastCode = code;
            }

            Show(response.Count);
            SetLast(response.Message, response.Outcome switch { "matched" => Good, "duplicate" => Ui.Muted, "extra" => Warn, _ => Bad });
            return $"{response.Message}\nToplam {response.Count.Counted} tüp";
        }
        finally
        {
            _sending = false;
        }
    }

    private async Task UndoAsync()
    {
        if (_count is not StockCountInfo count)
        {
            return;
        }

        string? code = await DisplayPromptAsync("Geri al", "Sayımdan çıkarılacak tüpün barkodu veya seri no:", "Çıkar", "Vazgeç", initialValue: _lastCode ?? string.Empty);
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            StockCountResponse response = await ApiClient.Instance.UndoStockCountAsync(count.Id, code.Trim());
            _lastCode = null;
            Show(response.Count);
            SetLast(response.Message, Ui.Muted);
        });
    }

    private async Task CloseAsync()
    {
        if (_count is not StockCountInfo count)
        {
            return;
        }

        string question = count.Counted + count.Unknown == 0
            ? "Hiç tüp okutulmadı. Sayım kaydedilmeden kapatılsın mı?"
            : $"{count.Counted} tüp okutuldu. Şu an kayıtta depoda {count.Expected} tüp var: {count.Missing} eksik, {count.Extra} fazla görünüyor.\n\nSayım bitirilsin mi? Bitince bu sayıma okutma yapılamaz; rapor panelde Sayım Raporları sekmesinde kalır.";
        if (!await DisplayAlertAsync("Sayımı bitir", question, "Bitir", "Vazgeç"))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            StockCountResponse response = await ApiClient.Instance.CloseStockCountAsync(count.Id);
            Show(null);
            await DisplayAlertAsync("Sayım bitti", response.Message, "Tamam");
        });
    }

    private void SetLast(string text, Color color)
    {
        _last.Text = text;
        _last.TextColor = color;
    }

    private void Show(StockCountInfo? count)
    {
        _count = count is { Open: true } ? count : null;
        _startSection.IsVisible = _count is null;
        _scanSection.IsVisible = _resultSection.IsVisible = _count is not null;
        if (_count is null)
        {
            _last.Text = _totals.Text = string.Empty;
            _groups.Children.Clear();
            _recent.Text = "Açık sayım yok.";
            return;
        }

        StockCountInfo c = _count;
        _status.Text = $"Sayım #{c.Id} · {c.Location} · başlatan {c.StartedBy} · {c.Started}";
        _totals.Text = $"Okutulan {c.Counted} · eşleşen {c.Matched} · fazla {c.Extra} · tanınmayan {c.Unknown}\nKayıtta depoda {c.Expected} · henüz okutulmayan {c.Missing}";
        _groups.Children.Clear();
        foreach (StockCountGroupInfo g in c.Groups)
        {
            _groups.Children.Add(GroupRow(g));
        }

        if (c.Groups.Count == 0)
        {
            _groups.Children.Add(Ui.Note("Kayıtta depoda tüp yok ve henüz okutma yapılmadı."));
        }

        List<string> lines = c.Recent.Select(x => $"{x.At?.Split(' ').LastOrDefault()} · {x.SerialNumber} · {x.Group}" + (x.Place == "Depoda" ? string.Empty : $" · ⚠ {x.Place}")).ToList();
        lines.AddRange(c.UnknownCodes.TakeLast(5).Select(u => $"{u.At.Split(' ').LastOrDefault()} · ❓ {u.Code} (tanınmayan)"));
        _recent.Text = lines.Count == 0 ? "Henüz okutma yok." : string.Join("\n", lines);
    }

    private static View GroupRow(StockCountGroupInfo g)
    {
        Color color = g.Counted == g.Expected && g.Extra == 0 ? Good : g.Counted < g.Expected ? Warn : Bad;
        Grid row = new()
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        row.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = g.Title, FontSize = 15, FontFamily = "OpenSansSemibold" },
                Ui.Note($"eşleşen {g.Matched} · fazla {g.Extra} · okutulmayan {g.Missing} · dolu {g.Full} · boş {g.Empty}")
            }
        }, 0, 0);
        row.Add(new Label
        {
            Text = $"{g.Counted} / {g.Expected}",
            FontSize = 22,
            FontFamily = "OpenSansSemibold",
            TextColor = color,
            VerticalTextAlignment = TextAlignment.Center
        }, 1, 0);
        return row;
    }
}
