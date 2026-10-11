using System.Text.RegularExpressions;
using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class CylinderPage : ContentPage
{
    internal const int CustomerStatus = 3;
    internal const int VehicleStatus = 2;

    private static readonly (string Label, int Status)[] Actions =
    [
        ("MÃ¼ÅŸteriye teslim", CustomerStatus),
        ("Depoya iade", 1),
        ("Araca yÃ¼kle (taÅŸÄ±nÄ±yor)", VehicleStatus),
        ("Dolum", 4),
        ("Servis", 5)
    ];

    private static readonly (string Label, bool? Value)[] Fullness =
    [
        ("Dolu/boÅŸ: otomatik", null),
        ("Dolu", true),
        ("BoÅŸ", false)
    ];

    private static readonly Regex PlatePattern = new(@"^\d{2}\s*[A-ZÃ‡ÄÄ°Ã–ÅÃœ]{1,3}\s*\d{2,4}$");

    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no", ReturnType = ReturnType.Search };
    private readonly Entry _batchCode = new() { Placeholder = "Barkod veya seri no (elle ekle)", ReturnType = ReturnType.Done };
    private readonly Label _detail = new() { FontSize = 14 };
    private readonly Picker _action = new() { Title = "Hareket" };
    private readonly Entry _customerSearch = new() { Placeholder = "MÃ¼ÅŸteri ara (ad, kod, ÅŸehir)", ReturnType = ReturnType.Search };
    private readonly Picker _customer = new() { Title = "MÃ¼ÅŸteri seÃ§in" };
    private readonly Entry _plate = new() { Placeholder = "Plaka (Ã¶r. 01 ABC 123, boÅŸ bÄ±rakÄ±labilir)" };
    private readonly Picker _knownPlates = new() { Title = "KayÄ±tlÄ± araÃ§lar" };
    private readonly Picker _fullness = new() { Title = "Dolu / boÅŸ" };
    private readonly Entry _note = new() { Placeholder = "Not (isteÄŸe baÄŸlÄ±)" };
    private readonly Label _pendingText = new() { FontSize = 13, TextColor = Ui.Accent };
    private readonly Switch _batchMode = new();
    private readonly Label _batchText = new() { FontSize = 14 };
    private readonly Border _pendingPanel;
    private readonly Border _singlePanel;
    private readonly Border _batchPanel;
    private readonly Border _movePanel;
    private readonly VerticalStackLayout _customerPanel;
    private readonly VerticalStackLayout _vehiclePanel;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<string> _batch = [];
    private List<CustomerItem> _customers = [];
    private CylinderInfo? _current;
    private bool _platesLoaded;

    public CylinderPage()
    {
        Title = "TÃ¼p";
        Ui.ApplyPageChrome(this);
        foreach ((string label, _) in Actions)
        {
            _action.Items.Add(label);
        }

        foreach ((string label, _) in Fullness)
        {
            _fullness.Items.Add(label);
        }

        _fullness.SelectedIndex = 0;
        _action.SelectedIndexChanged += async (_, _) => await OnActionChangedAsync();
        _knownPlates.SelectedIndexChanged += (_, _) =>
        {
            if (_knownPlates.SelectedItem is string plate)
            {
                _plate.Text = plate;
            }
        };
        _code.Completed += async (_, _) => await OnCodeEnteredAsync();
        _batchCode.Completed += async (_, _) => await OnCodeEnteredAsync();
        _customerSearch.Completed += async (_, _) => await SearchCustomersAsync();
        _batchMode.Toggled += (_, _) => ApplyMode();

        _pendingPanel = Ui.Card(new VerticalStackLayout
        {
            Spacing = 8,
            Children = { _pendingText, Ui.Secondary("Bekleyenleri ÅŸimdi gÃ¶nder", async (_, _) => await FlushAsync(showResult: true)) }
        });
        _pendingPanel.IsVisible = false;
        _customerPanel = new VerticalStackLayout
        {
            Spacing = 6,
            IsVisible = false,
            Children = { _customerSearch, Ui.Secondary("MÃ¼ÅŸteri ara", async (_, _) => await SearchCustomersAsync()), _customer }
        };
        _vehiclePanel = new VerticalStackLayout
        {
            Spacing = 6,
            IsVisible = false,
            Children = { _knownPlates, _plate }
        };
        _movePanel = Ui.Section(
            "Hareket kaydet",
            _action,
            _customerPanel,
            _vehiclePanel,
            _fullness,
            _note,
            Ui.Primary("Kaydet", async (_, _) => await MoveAsync(confirm: true)));
        _movePanel.IsVisible = false;
        _singlePanel = Ui.Section(
            "Tek tÃ¼p",
            Ui.Primary("ğŸ“· Barkod / QR okut", async (_, _) => await ScanAsync()),
            _code,
            Ui.Secondary("Sorgula", async (_, _) => await LookupAsync(_code.Text, quick: false)),
            _detail);
        _batchPanel = Ui.Section(
            "Toplu okutma",
            Ui.Primary("ğŸ“· Art arda okut", async (_, _) => await ScanManyAsync()),
            _batchCode,
            Ui.ActionRow(
                Ui.Secondary("Kodu ekle", async (_, _) => await OnCodeEnteredAsync()),
                Ui.Secondary("Listeyi temizle", (_, _) => ClearBatch())),
            _batchText);
        _batchPanel.IsVisible = false;

        Grid modeRow = new()
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        modeRow.Add(new VerticalStackLayout
        {
            Spacing = 2,
            Children =
            {
                new Label { Text = "Toplu okutma", FontSize = 15, FontFamily = "OpenSansSemibold" },
                Ui.Note("Birden Ã§ok tÃ¼pe aynÄ± hareketi uygular")
            }
        }, 0, 0);
        modeRow.Add(_batchMode, 1, 0);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    _pendingPanel,
                    Ui.ActionRow(
                        Ui.Secondary("ğŸ“Š TÃ¼p durumu", async (_, _) => await Navigation.PushAsync(new CylinderStatusPage())),
                        Ui.Secondary("ğŸ“‹ MÃ¼ÅŸteride sayÄ±m", async (_, _) => await Navigation.PushAsync(new CylinderAuditPage()))),
                    Ui.Card(modeRow),
                    _singlePanel,
                    _batchPanel,
                    _busy,
                    _movePanel
                }
            }
        };

        RefreshBatch();
        OfflineMoves.Changed += () => MainThread.BeginInvokeOnMainThread(RefreshPending);
        Connectivity.Current.ConnectivityChanged += async (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                await FlushAsync(showResult: false);
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        RefreshPending();
        await FlushAsync(showResult: false);
    }

    private int? SelectedStatus => _action.SelectedIndex >= 0 ? Actions[_action.SelectedIndex].Status : null;

    private bool? SelectedFullness => _fullness.SelectedIndex > 0 ? Fullness[_fullness.SelectedIndex].Value : null;

    private bool BatchMode => _batchMode.IsToggled;

    private void ApplyMode()
    {
        _singlePanel.IsVisible = !BatchMode;
        _batchPanel.IsVisible = BatchMode;
        _current = null;
        _detail.Text = string.Empty;
        _movePanel.IsVisible = BatchMode && _batch.Count > 0;
    }

    private async Task OnActionChangedAsync()
    {
        _customerPanel.IsVisible = SelectedStatus == CustomerStatus;
        _vehiclePanel.IsVisible = SelectedStatus == VehicleStatus;
        if (SelectedStatus == VehicleStatus && !_platesLoaded)
        {
            try
            {
                List<string> plates = await ApiClient.Instance.VehiclePlatesAsync();
                _knownPlates.ItemsSource = plates;
                _knownPlates.IsVisible = plates.Count > 0;
                _platesLoaded = true;
            }
            catch (ApiException)
            {
                _knownPlates.IsVisible = false;
            }
        }
    }

    private void RefreshPending()
    {
        int count = OfflineMoves.Pending.Count;
        _pendingPanel.IsVisible = count > 0;
        _pendingText.Text = $"ğŸ“¶ GÃ¶nderilmeyi bekleyen {count} tÃ¼p hareketi var. BaÄŸlantÄ± gelince otomatik gÃ¶nderilir.";
    }

    private void RefreshBatch()
    {
        _batchText.Text = _batch.Count == 0
            ? "Liste boÅŸ. \"Art arda okut\" ile tÃ¼pleri sÄ±rayla okutun."
            : $"{_batch.Count} tÃ¼p seÃ§ildi:\n{string.Join(", ", _batch)}";
        if (BatchMode)
        {
            _movePanel.IsVisible = _batch.Count > 0;
        }
    }

    private void AddToBatch(IEnumerable<string> codes)
    {
        foreach (string code in codes.Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            if (!_batch.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                _batch.Add(code);
            }
        }

        RefreshBatch();
    }

    private void ClearBatch()
    {
        _batch.Clear();
        RefreshBatch();
    }

    private async Task OnCodeEnteredAsync()
    {
        if (BatchMode)
        {
            AddToBatch([_batchCode.Text ?? string.Empty]);
            _batchCode.Text = string.Empty;
            return;
        }

        await LookupAsync(_code.Text, quick: false);
    }

    private async Task FlushAsync(bool showResult)
    {
        if (OfflineMoves.Pending.Count == 0)
        {
            return;
        }

        (int sent, string? error) = await OfflineMoves.FlushAsync();
        if (error is not null)
        {
            await DisplayAlertAsync("Bekleyen hareket reddedildi", error, "Tamam");
        }
        else if (showResult || sent > 0)
        {
            int left = OfflineMoves.Pending.Count;
            await DisplayAlertAsync("Bekleyen hareketler",
                left == 0 ? $"{sent} hareket ERP'ye gÃ¶nderildi." : $"{sent} gÃ¶nderildi; {left} hareket baÄŸlantÄ± bekliyor.", "Tamam");
        }
    }

    private async Task ScanAsync()
    {
        try
        {
            string? value = await ScanPage.ScanAsync(Navigation);
            if (value is not null)
            {
                _code.Text = value;
                await LookupAsync(value, quick: true);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task ScanManyAsync()
    {
        try
        {
            AddToBatch(await ScanPage.ScanManyAsync(Navigation));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task LookupAsync(string? code, bool quick)
    {
        string value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return;
        }

        _current = null;
        _movePanel.IsVisible = false;
        _detail.Text = string.Empty;
        _action.SelectedIndex = -1;
        _note.Text = string.Empty;
        _fullness.SelectedIndex = 0;
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                _current = await ApiClient.Instance.CylinderAsync(value);
                _detail.Text = _current.Detail;
            }
            catch (ServerUnreachableException)
            {
                _current = new CylinderInfo(0, value, value, -1, "bilinmiyor", null, string.Empty);
                _detail.Text = $"ğŸ“¶ BaÄŸlantÄ± yok. TÃ¼p bilgisi alÄ±namadÄ± ({value}).\nHareketi yine de kaydedebilirsiniz; baÄŸlantÄ± gelince ERP'ye gÃ¶nderilir.";
            }

            _movePanel.IsVisible = true;
        });

        if (quick && _current is not null)
        {
            await QuickMoveAsync();
        }
    }

    private async Task QuickMoveAsync()
    {
        string? choice = await DisplayActionSheetAsync(
            $"{_current!.SerialNumber} Â· {_current.StatusName}", "Kapat", null, Actions.Select(a => a.Label).ToArray());
        int index = Array.FindIndex(Actions, a => a.Label == choice);
        if (index < 0)
        {
            return;
        }

        _action.SelectedIndex = index;
        if (Actions[index].Status == CustomerStatus)
        {
            await DisplayAlertAsync("MÃ¼ÅŸteriye teslim", "AÅŸaÄŸÄ±dan mÃ¼ÅŸteriyi arayÄ±p seÃ§in, sonra Kaydet'e basÄ±n.", "Tamam");
            _customerSearch.Focus();
            return;
        }

        if (Actions[index].Status == VehicleStatus)
        {
            await DisplayAlertAsync("Araca yÃ¼kle", "AÅŸaÄŸÄ±dan aracÄ±n plakasÄ±nÄ± seÃ§in veya yazÄ±n, sonra Kaydet'e basÄ±n.", "Tamam");
            return;
        }

        await MoveAsync(confirm: false);
    }

    private Task SearchCustomersAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        _customers = await ApiClient.Instance.CustomersAsync(_customerSearch.Text ?? string.Empty);
        _customer.ItemsSource = _customers.Select(c => $"{c.Name} ({c.Code})").ToList();
        if (_customers.Count == 1)
        {
            _customer.SelectedIndex = 0;
        }
        else if (_customers.Count == 0)
        {
            await DisplayAlertAsync("MÃ¼ÅŸteri", "EÅŸleÅŸen mÃ¼ÅŸteri yok.", "Tamam");
        }
    });

    private async Task MoveAsync(bool confirm)
    {
        bool batch = BatchMode;
        if ((batch ? _batch.Count == 0 : _current is null) || SelectedStatus is not int status)
        {
            await DisplayAlertAsync("TÃ¼p", batch ? "Ã–nce tÃ¼pleri okutup hareket seÃ§in." : "Ã–nce tÃ¼pÃ¼ sorgulayÄ±p hareket seÃ§in.", "Tamam");
            return;
        }

        CustomerItem? customer = status == CustomerStatus && _customer.SelectedIndex >= 0 ? _customers[_customer.SelectedIndex] : null;
        if (status == CustomerStatus && customer is null)
        {
            await DisplayAlertAsync("TÃ¼p", "MÃ¼ÅŸteriye teslim iÃ§in mÃ¼ÅŸteri seÃ§in. BaÄŸlantÄ± yoksa Ã¶nce baÄŸlantÄ±lÄ±yken mÃ¼ÅŸteriyi aratÄ±n.", "Tamam");
            return;
        }

        string? plate = null;
        if (status == VehicleStatus)
        {
            plate = (_plate.Text ?? string.Empty).Trim().ToUpperInvariant().Replace("-", " ");
            if (plate.Length == 0)
            {
                plate = null;
            }
            else if (!PlatePattern.IsMatch(plate))
            {
                await DisplayAlertAsync("Plaka", "Plaka geÃ§erli gÃ¶rÃ¼nmÃ¼yor. Ã–rnek: 01 ABC 123", "Tamam");
                return;
            }
        }

        bool? isFull = SelectedFullness;
        string label = _action.Items[_action.SelectedIndex]
            + (customer is null ? string.Empty : $" ({customer.Name})")
            + (plate is null ? string.Empty : $" ({plate})")
            + (isFull is null ? string.Empty : isFull.Value ? " Â· dolu" : " Â· boÅŸ");
        string subject = batch ? $"{_batch.Count} tÃ¼p" : $"{_current!.SerialNumber}: {_current.StatusName}";
        if ((confirm || batch) && !await DisplayAlertAsync("Onay", $"{subject} â†’ {label}\nKaydedilsin mi?", "Kaydet", "VazgeÃ§"))
        {
            return;
        }

        if (batch)
        {
            await SaveBatchAsync(status, customer, plate, isFull, label);
            return;
        }

        CylinderInfo current = _current!;
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                string message = await ApiClient.Instance.MoveCylinderAsync(current.Barcode, status, customer?.Id, _note.Text, plate, isFull);
                await DisplayAlertAsync("Kaydedildi", message, "Tamam");
                await LookupAsync(current.Barcode, quick: false);
            }
            catch (ServerUnreachableException)
            {
                OfflineMoves.Add(new PendingMove(current.Barcode, status, customer?.Id, _note.Text, label, DateTime.Now, plate, isFull));
                _movePanel.IsVisible = false;
                _detail.Text = $"ğŸ“¶ BaÄŸlantÄ± yok: {current.SerialNumber} â†’ {label} telefonda saklandÄ±. BaÄŸlantÄ± gelince otomatik gÃ¶nderilecek.";
            }
        });
    }

    private Task SaveBatchAsync(int status, CustomerItem? customer, string? plate, bool? isFull, string label) =>
        Ui.RunAsync(this, _busy, async () =>
        {
            List<string> codes = [.. _batch];
            try
            {
                CylinderBulkResponse response = await ApiClient.Instance.MoveCylindersAsync(codes, status, customer?.Id, _note.Text, plate, isFull);
                List<string> failed = response.Results.Where(r => !r.Ok).Select(r => $"{r.Code}: {r.Message}").ToList();
                await DisplayAlertAsync(response.Failed == 0 ? "Kaydedildi" : "KÄ±smen kaydedildi",
                    response.Message + (failed.Count == 0 ? string.Empty : "\n\nKaydedilemeyenler:\n" + string.Join("\n", failed)), "Tamam");
                _batch.Clear();
                _batch.AddRange(response.Results.Where(r => !r.Ok).Select(r => r.Code));
            }
            catch (ServerUnreachableException)
            {
                foreach (string code in codes)
                {
                    OfflineMoves.Add(new PendingMove(code, status, customer?.Id, _note.Text, label, DateTime.Now, plate, isFull));
                }

                _batch.Clear();
                await DisplayAlertAsync("BaÄŸlantÄ± yok", $"{codes.Count} tÃ¼p hareketi telefonda saklandÄ±. BaÄŸlantÄ± gelince otomatik gÃ¶nderilecek.", "Tamam");
            }

            RefreshBatch();
        });
}
