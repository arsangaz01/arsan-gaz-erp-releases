using System.Text.Json;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Main depot count: every scanned cylinder is counted once under its gas type and size; the panel compares the count
// with the cylinders the records show in the warehouse (Sayım Raporları). Without a connection the scans wait on the
// phone (OfflineCountScans) and "Senkronize et" sends them.
public sealed class StockCountPage : ContentPage
{
    private const string SavedCountKey = "stock_count_open";
    private const string XlsxMime = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";
    private static readonly Color Good = Color.FromArgb("#15803D");
    private static readonly Color Warn = Color.FromArgb("#B45309");
    private static readonly Color Bad = Color.FromArgb("#B91C1C");

    private readonly Label _status = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı), ör. ay sonu sayımı", MaxLength = 500 };
    private readonly Entry _code = new() { Placeholder = "Barkod / seri no (el terminali veya elle)", ReturnType = ReturnType.Send };
    private readonly Label _last = new() { FontSize = 15, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _pending = new() { FontSize = 14, TextColor = Color.FromArgb("#B45309"), LineBreakMode = LineBreakMode.WordWrap };
    private readonly Button _pendingButton;
    private readonly Label _totals = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly VerticalStackLayout _groups = new() { Spacing = 8 };
    private readonly Label _recent = Ui.Note(string.Empty);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly Label _openLine = Ui.Note(string.Empty);
    private readonly Button _fullButton;
    private readonly Button _emptyButton;
    private readonly View _startSection;
    private readonly View _scanSection;
    private readonly View _resultSection;
    private StockCountInfo? _count;
    private List<StockCountInfo> _open = [];
    private string? _lastCode;
    private bool _sending;

    private static bool CanExport => ApiClient.Instance.CanAny(Perm.Reports, Perm.Export);

    public StockCountPage()
    {
        Title = "Depo sayımı";
        _code.Completed += async (_, _) => await ManualAsync();
        _pendingButton = Ui.Secondary("🔄 Bekleyenleri sunucuya gönder", async (_, _) => await FlushAsync(quiet: false));

        _fullButton = Ui.Primary("🟢  Dolu sayımı başlat", async (_, _) => await StartAsync(ApiClient.StockCountFull));
        _emptyButton = Ui.Primary("⚪  Boş sayımı başlat", async (_, _) => await StartAsync(ApiClient.StockCountEmpty));
        _startSection = Ui.Section("Sayım",
            Ui.Note("Dolu ve boş tüpler ayrı sayılır. Her düğme kendi kaydını açar (veya açıksa ona devam eder) ve okutma ekranı hemen gelir. Aynı sayıma birden çok telefon okutabilir."),
            _note,
            _openLine,
            _fullButton,
            _emptyButton);
        _scanSection = Ui.Section("Okutma",
            _status,
            Ui.Note("\"Kayıtta depoda\" yalnızca bu sayımın türüne (dolu veya boş) göre hesaplanır; sefer ve stok hareketleriyle anlık güncellenir."),
            Ui.Primary("▶ Okutmaya devam", async (_, _) => await CameraAsync()),
            Ui.Secondary("↩ Diğer sayıma geç", async (_, _) => { Show(null); await LoadAsync(); }),
            _code,
            Ui.ActionRow(
                Ui.Secondary("Son okutmayı geri al", async (_, _) => await UndoAsync()),
                Ui.Secondary("Yenile", async (_, _) => await LoadAsync())),
            _last,
            _pending,
            _pendingButton,
            _totals,
            Ui.ActionRow(
                Ui.Secondary("📊 Excel", async (_, _) => await ExportAsync(_count?.Id, excel: true)),
                Ui.Secondary("📄 PDF", async (_, _) => await ExportAsync(_count?.Id, excel: false))).ShowIf(CanExport),
            Ui.Primary("✅ Sayımı bitir", async (_, _) => await CloseAsync()));
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
                    Ui.Section("Son okutulanlar", _recent),
                    Ui.Section("Raporlar",
                        Ui.Note("Bitmiş ve devam eden sayımların raporunu Excel veya PDF olarak indirip paylaşın (WhatsApp, e-posta, Drive...). Aynı raporlar web panelde Sayım Raporları sekmesindedir."),
                        Ui.Secondary("📄 Sayım raporları (Excel / PDF)", async (_, _) => await HistoryAsync())).ShowIf(CanExport)
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

    private async Task LoadAsync()
    {
        _busy.IsRunning = _busy.IsVisible = true;
        try
        {
            StockCountActiveResponse active = await ApiClient.Instance.ActiveStockCountAsync();
            _open = active.Counts ?? (active.Count is null ? [] : [active.Count]);
            RefreshStartButtons();
            StockCountInfo? keep = _count is StockCountInfo current
                ? _open.FirstOrDefault(c => c.Id == current.Id)
                : Saved() is StockCountInfo saved ? _open.FirstOrDefault(c => c.Id == saved.Id) : null;
            keep ??= _open.Count == 1 ? _open[0] : null;
            if (keep is not null)
            {
                Show(keep);
            }
            else if (_count is not null)
            {
                Show(null);
            }

            if (_count is not null && OfflineCountScans.PendingFor(_count.Id) > 0)
            {
                await FlushAsync(quiet: true);
            }
        }
        catch (ServerUnreachableException)
        {
            // Keep counting on the phone with the last known open count; the scans are sent later.
            Show(Saved());
            RefreshStartButtons();
            SetLast(_count is null
                ? "📴 Sunucuya ulaşılamıyor. Sayım başlatmak için bağlantı gerekir."
                : "📴 Sunucuya ulaşılamıyor; okutmalar telefonda biriktirilir, bağlantı gelince Senkronize edin.", Warn);
        }
        catch (SessionExpiredException)
        {
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Depo sayımı", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsRunning = _busy.IsVisible = false;
        }
    }

    private async Task StartAsync(string location)
    {
        await Ui.RunAsync(this, _busy, async () =>
        {
            StockCountResponse response = await ApiClient.Instance.StartStockCountAsync(_note.Text, location);
            _note.Text = string.Empty;
            if (response.Count is StockCountInfo started)
            {
                _open = [.. _open.Where(c => c.Id != started.Id), started];
            }

            Show(response.Count);
            RefreshStartButtons();
            SetLast(response.Message, Good);
        });
        if (_count is not null)
        {
            await CameraAsync();
        }
    }

    private void RefreshStartButtons()
    {
        StockCountInfo? full = _open.FirstOrDefault(c => c.Location == ApiClient.StockCountFull);
        StockCountInfo? empty = _open.FirstOrDefault(c => c.Location == ApiClient.StockCountEmpty);
        StockCountInfo? legacy = _open.FirstOrDefault(c => c.Location is not (ApiClient.StockCountFull or ApiClient.StockCountEmpty));
        _fullButton.Text = full is null ? "🟢  Dolu sayımı başlat" : $"🟢  Dolu sayımına devam (#{full.Id})";
        _emptyButton.Text = empty is null ? "⚪  Boş sayımı başlat" : $"⚪  Boş sayımına devam (#{empty.Id})";
        List<string> lines = [];
        if (full is not null)
        {
            lines.Add($"Açık dolu sayımı #{full.Id}: {full.Counted} okutulan / kayıtta {full.Expected}");
        }

        if (empty is not null)
        {
            lines.Add($"Açık boş sayımı #{empty.Id}: {empty.Counted} okutulan / kayıtta {empty.Expected}");
        }

        if (legacy is not null)
        {
            lines.Add($"Eski genel sayım #{legacy.Id} ({legacy.Location}) hâlâ açık; bitirmek için panelden veya bu ekrandan devam edin.");
        }

        _openLine.Text = lines.Count == 0 ? "Açık sayım yok. Dolu ve boş için ayrı ayrı başlatın." : string.Join("\n", lines);
    }

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
            if ((await SendAsync(code)).StartsWith('❌'))
            {
                ErrorBuzz();
            }
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
        if (_count is not StockCountInfo count)
        {
            throw new InvalidOperationException("Açık sayım yok.");
        }

        _sending = true;
        try
        {
            StockCountScanResponse response = await ApiClient.Instance.ScanStockCountAsync(count.Id, code);
            if (response.Outcome != "duplicate")
            {
                _lastCode = code;
            }

            Show(response.Count);
            SetLast(response.Message, response.Outcome switch { "matched" => Good, "duplicate" => Ui.Muted, _ => Bad });
            if (OfflineCountScans.PendingFor(count.Id) > 0)
            {
                _ = FlushAsync(quiet: true);
            }

            return $"{response.Message}\nToplam {response.Count.Counted} tüp";
        }
        catch (ServerUnreachableException)
        {
            OfflineCountScans.Add(count.Id, code);
            ShowPending();
            string text = $"📴 {code} telefonda kaydedildi; bağlantı gelince gönderilir.";
            SetLast(text, Warn);
            return text + $"\nBekleyen {OfflineCountScans.PendingFor(count.Id)} okutma";
        }
        finally
        {
            _sending = false;
        }
    }

    private async Task<bool> FlushAsync(bool quiet)
    {
        (int sent, StockCountInfo? last, List<string> rejected) = await OfflineCountScans.FlushAsync();
        if (last is not null)
        {
            Show(last);
        }

        ShowPending();
        int left = _count is null ? 0 : OfflineCountScans.PendingFor(_count.Id);
        if (!quiet || rejected.Count > 0)
        {
            string text = $"{sent} okutma sunucuya gönderildi." + (left > 0 ? $" {left} okutma hâlâ bağlantı bekliyor." : string.Empty)
                + (rejected.Count > 0 ? "\n\nKabul edilmeyenler:\n" + string.Join("\n", rejected.Take(10)) : string.Empty);
            await DisplayAlertAsync("Senkronizasyon", text, "Tamam");
        }

        return left == 0;
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

        if (OfflineCountScans.PendingFor(count.Id) > 0 && !await FlushAsync(quiet: true))
        {
            await DisplayAlertAsync("Sayımı bitir",
                $"Telefonda sunucuya gönderilemeyen {OfflineCountScans.PendingFor(count.Id)} okutma var. Bağlantı gelince \"Bekleyenleri sunucuya gönder\" veya ana sayfadaki \"Senkronize et\" ile gönderin, sonra sayımı bitirin.", "Tamam");
            return;
        }

        count = _count ?? count;
        string question = count.Counted + count.Unknown == 0
            ? "Hiç tüp okutulmadı. Sayım kaydedilmeden kapatılsın mı?"
            : $"{count.Counted} tüp okutuldu. Şu an kayıtta depoda {count.Expected} tüp var: {count.Missing} eksik, {count.Extra} fazla görünüyor.\n\nSayım bitirilsin mi? Bitince bu sayıma okutma yapılamaz; rapor panelde Sayım Raporları sekmesinde kalır.";
        if (!await DisplayAlertAsync("Sayımı bitir", question, "Bitir", "Vazgeç"))
        {
            return;
        }

        StockCountResponse? response = null;
        await Ui.RunAsync(this, _busy, async () =>
        {
            response = await ApiClient.Instance.CloseStockCountAsync(count.Id);
            Show(null);
        });
        if (response is null)
        {
            return;
        }

        if (response.Count is null)
        {
            await DisplayAlertAsync("Sayım kapatıldı", response.Message, "Tamam");
            return;
        }

        await ChooseExportAsync(count.Id, "Sayım bitti", response.Message + "\n\nRaporu indirmek ister misiniz?");
    }

    private async Task HistoryAsync()
    {
        List<StockCountListInfo> counts = [];
        await Ui.RunAsync(this, _busy, async () => counts = await ApiClient.Instance.StockCountsAsync());
        if (counts.Count == 0)
        {
            await DisplayAlertAsync("Sayım raporları", "Henüz depo sayımı yok.", "Tamam");
            return;
        }

        Dictionary<string, StockCountListInfo> options = counts.Take(20).ToDictionary(
            c => $"#{c.Id} · {c.Started}{(c.Open ? " (devam ediyor)" : string.Empty)} · {c.Counted} okutulan · {c.Missing} eksik · {c.Extra} fazla");
        string? chosen = await DisplayActionSheetAsync("Hangi sayım?", "Vazgeç", null, [.. options.Keys]);
        if (chosen is not null && options.TryGetValue(chosen, out StockCountListInfo? pick))
        {
            await ChooseExportAsync(pick.Id, $"Sayım #{pick.Id}", null);
        }
    }

    private async Task ChooseExportAsync(int id, string title, string? message)
    {
        if (message is not null)
        {
            await DisplayAlertAsync(title, message, "Tamam");
        }

        string? format = await DisplayActionSheetAsync($"{title} · rapor biçimi", "Vazgeç", null, "📊 Excel (.xlsx)", "📄 PDF");
        if (format is not null && format != "Vazgeç")
        {
            await ExportAsync(id, excel: format.Contains("Excel", StringComparison.Ordinal));
        }
    }

    // Downloads the report and opens the Android share sheet (Excel/PDF viewer, WhatsApp, e-posta, Drive...).
    private async Task ExportAsync(int? id, bool excel)
    {
        if (id is not int countId)
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            string kind = excel ? "excel" : "pdf";
            (byte[] bytes, string name) = await ApiClient.Instance.DownloadAsync($"/api/depo-sayim/{countId}/{kind}", $"depo-sayimi-{countId}.{(excel ? "xlsx" : "pdf")}");
            string path = Path.Combine(FileSystem.CacheDirectory, name);
            await File.WriteAllBytesAsync(path, bytes);
            await Share.Default.RequestAsync(new ShareFileRequest
            {
                Title = name,
                File = new ShareFile(path, excel ? XlsxMime : "application/pdf")
            });
        });
    }

    private static void ErrorBuzz()
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(600));
        }
        catch (Exception exception) when (exception is FeatureNotSupportedException or PermissionException)
        {
        }
    }

    private void SetLast(string text, Color color)
    {
        _last.Text = text;
        _last.TextColor = color;
    }

    private void ShowPending()
    {
        int pending = _count is null ? 0 : OfflineCountScans.PendingFor(_count.Id);
        _pending.Text = pending > 0 ? $"📴 Telefonda sunucuya gönderilmeyi bekleyen {pending} okutma var (toplamlara henüz eklenmedi)." : string.Empty;
        _pending.IsVisible = _pendingButton.IsVisible = pending > 0;
    }

    private static StockCountInfo? Saved()
    {
        string json = Preferences.Default.Get(SavedCountKey, string.Empty);
        try
        {
            return json.Length == 0 ? null : JsonSerializer.Deserialize<StockCountInfo>(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Show(StockCountInfo? count)
    {
        _count = count is { Open: true } ? count : null;
        if (_count is null)
        {
            Preferences.Default.Remove(SavedCountKey);
        }
        else
        {
            Preferences.Default.Set(SavedCountKey, JsonSerializer.Serialize(_count with { Recent = [], UnknownCodes = [] }));
        }

        _startSection.IsVisible = _count is null;
        _scanSection.IsVisible = _resultSection.IsVisible = _count is not null;
        ShowPending();
        if (_count is null)
        {
            _last.Text = _totals.Text = string.Empty;
            _groups.Children.Clear();
            _recent.Text = "Açık sayım yok.";
            return;
        }

        StockCountInfo c = _count;
        _status.Text = $"Sayım #{c.Id} · {c.Location} · başlatan {c.StartedBy} · {c.Started}";
        _totals.Text = $"Eşleşen {c.Matched} / kayıtta depoda {c.Expected} · henüz okutulmayan {c.Missing}"
            + (c.Extra + c.Unknown > 0 ? $"\n❌ Hatalı okutma: stokta olmayan {c.Extra} · tanınmayan barkod {c.Unknown}" : string.Empty);
        if (c.Extra + c.Unknown > 0)
        {
            _totals.TextColor = Bad;
        }
        else
        {
            _totals.ClearValue(Label.TextColorProperty);
        }
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

    // Big number = matched / in the warehouse by the records; cylinders that are not in the depot stock are errors, not stock.
    private View GroupRow(StockCountGroupInfo g)
    {
        Color color = g.Extra > 0 ? Bad : g.Matched == g.Expected ? Good : Warn;
        Grid row = new()
        {
            ColumnSpacing = 10,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)]
        };
        Label detail = Ui.Note($"eşleşen {g.Matched} · okutulmayan {g.Missing} · dolu {g.Full} · boş {g.Empty}"
            + (g.Extra > 0 ? $" · ❌ stokta olmayan {g.Extra}" : string.Empty));
        row.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = g.Title, FontSize = 15, FontFamily = "OpenSansSemibold" },
                detail
            }
        }, 0, 0);
        row.Add(new Label
        {
            Text = $"{g.Matched} / {g.Expected}",
            FontSize = 22,
            FontFamily = "OpenSansSemibold",
            TextColor = color,
            VerticalTextAlignment = TextAlignment.Center
        }, 1, 0);
        row.Add(new Label { Text = "›", FontSize = 28, TextColor = Ui.Muted, VerticalTextAlignment = TextAlignment.Center }, 2, 0);

        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            if (_count is StockCountInfo count && Navigation.NavigationStack.LastOrDefault() == this)
            {
                await Navigation.PushAsync(new StockCountGroupPage(count.Id, g.Title, g.GasType, g.Capacity));
            }
        };
        row.GestureRecognizers.Add(tap);
        SemanticProperties.SetDescription(row, g.Title + " barkodları");
        return row;
    }
}
