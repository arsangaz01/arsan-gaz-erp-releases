using System.Text.RegularExpressions;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

public sealed class CylinderPage : ContentPage
{
    internal const int CustomerStatus = 3;
    internal const int VehicleStatus = 2;

    private static readonly (string Label, int Status)[] Actions =
    [
        ("Müşteriye teslim", CustomerStatus),
        ("Depoya iade", 1),
        ("Araca yükle (taşınıyor)", VehicleStatus),
        ("Dolum", 4),
        ("Servis", 5)
    ];

    private static readonly (string Label, bool? Value)[] Fullness =
    [
        ("Dolu/boş: otomatik", null),
        ("Dolu", true),
        ("Boş", false)
    ];

    private static readonly Regex PlatePattern = new(@"^\d{2}\s*[A-ZÇĞİÖŞÜ]{1,3}\s*\d{2,4}$");

    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no", ReturnType = ReturnType.Search };
    private readonly Entry _batchCode = new() { Placeholder = "Barkod veya seri no (elle ekle)", ReturnType = ReturnType.Done };
    private readonly Label _detail = new() { FontSize = 14 };
    private readonly Picker _action = new() { Title = "Hareket" };
    private readonly Entry _customerSearch = new() { Placeholder = "Müşteri ara (ad, kod, şehir)", ReturnType = ReturnType.Search };
    private readonly Picker _customer = new() { Title = "Müşteri seçin" };
    private readonly Entry _plate = new() { Placeholder = "Plaka (ör. 01 ABC 123, boş bırakılabilir)" };
    private readonly Picker _knownPlates = new() { Title = "Kayıtlı araçlar" };
    private readonly Picker _fullness = new() { Title = "Dolu / boş" };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı)" };
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
        Title = "Tüp";
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
            Children = { _pendingText, Ui.Secondary("Bekleyenleri şimdi gönder", async (_, _) => await FlushAsync(showResult: true)) }
        });
        _pendingPanel.IsVisible = false;
        _customerPanel = new VerticalStackLayout
        {
            Spacing = 6,
            IsVisible = false,
            Children =
            {
                _customerSearch,
                Ui.ActionRow(
                    Ui.Secondary("Müşteri ara", async (_, _) => await SearchCustomersAsync()),
                    Ui.Secondary("➕ Yeni müşteri", async (_, _) => await NewCustomerAsync())),
                _customer
            }
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
            "Tek tüp",
            Ui.Primary("📷 Barkod / QR okut", async (_, _) => await ScanAsync()),
            _code,
            Ui.ActionRow(
                Ui.Secondary("Sorgula", async (_, _) => await LookupAsync(_code.Text, quick: false)),
                Ui.Secondary("➕ Yeni tüp", async (_, _) => await RegisterNewAsync())),
            _detail);
        _batchPanel = Ui.Section(
            "Toplu okutma",
            Ui.Primary("📷 Art arda okut", async (_, _) => await ScanManyAsync()),
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
                Ui.Note("Birden çok tüpe aynı hareketi uygular")
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
                        Ui.Secondary("📊 Tüp durumu", async (_, _) => await Navigation.PushAsync(new CylinderStatusPage())),
                        Ui.Secondary("📋 Müşteride sayım", async (_, _) => await Navigation.PushAsync(new CylinderAuditPage()))),
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
        _pendingText.Text = $"📶 Gönderilmeyi bekleyen {count} tüp hareketi var. Bağlantı gelince otomatik gönderilir.";
    }

    private void RefreshBatch()
    {
        _batchText.Text = _batch.Count == 0
            ? "Liste boş. \"Art arda okut\" ile tüpleri sırayla okutun."
            : $"{_batch.Count} tüp seçildi:\n{string.Join(", ", _batch)}";
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

        (int sent, string? error, string? warning) = await OfflineMoves.FlushAsync();
        if (warning is not null)
        {
            await DisplayAlertAsync("Bekleyen hareketlerde müşteri değişikliği",
                "Bağlantısızken kaydedilen bu tüpler sistemde başka bir müşteride görünüyordu; teslim yine de işlendi, önceki müşterinin kaydını kontrol edin:\n\n" + warning, "Tamam");
        }

        if (error is not null)
        {
            await DisplayAlertAsync("Bekleyen hareket reddedildi", error, "Tamam");
        }
        else if (showResult || sent > 0)
        {
            int left = OfflineMoves.Pending.Count;
            await DisplayAlertAsync("Bekleyen hareketler",
                left == 0 ? $"{sent} hareket sunucuya gönderildi." : $"{sent} gönderildi; {left} hareket bağlantı bekliyor.", "Tamam");
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
            AddToBatch(await ScanPage.ScanManyAsync(Navigation, _batch));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task RegisterNewAsync()
    {
        if (await CylinderRegisterPage.RegisterAsync(Navigation, (_code.Text ?? string.Empty).Trim()) is string saved)
        {
            _code.Text = saved;
            await LookupAsync(saved, quick: false);
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
        bool unknown = false;
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
                _detail.Text = $"📶 Bağlantı yok. Tüp bilgisi alınamadı ({value}).\nHareketi yine de kaydedebilirsiniz; bağlantı gelince sunucuya gönderilir.";
            }
            catch (ApiException exception) when (exception.StatusCode == 404)
            {
                unknown = true;
                _detail.Text = exception.Message;
                return;
            }

            _movePanel.IsVisible = true;
        });

        if (unknown)
        {
            if (await DisplayAlertAsync("Kayıtlı olmayan tüp", $"'{value}' sistemde kayıtlı değil. Yeni tüp olarak kaydedilsin mi?", "Yeni tüp kaydet", "Vazgeç")
                && await CylinderRegisterPage.RegisterAsync(Navigation, value) is string saved)
            {
                _code.Text = saved;
                await LookupAsync(saved, quick: false);
            }

            return;
        }

        if (quick && _current is not null)
        {
            await QuickMoveAsync();
        }
    }

    private async Task QuickMoveAsync()
    {
        string? choice = await DisplayActionSheetAsync(
            $"{_current!.SerialNumber} · {_current.StatusName}", "Kapat", null, Actions.Select(a => a.Label).ToArray());
        int index = Array.FindIndex(Actions, a => a.Label == choice);
        if (index < 0)
        {
            return;
        }

        _action.SelectedIndex = index;
        if (Actions[index].Status == CustomerStatus)
        {
            await DisplayAlertAsync("Müşteriye teslim", "Aşağıdan müşteriyi arayıp seçin, sonra Kaydet'e basın.", "Tamam");
            _customerSearch.Focus();
            return;
        }

        if (Actions[index].Status == VehicleStatus)
        {
            await DisplayAlertAsync("Araca yükle", "Aşağıdan aracın plakasını seçin veya yazın, sonra Kaydet'e basın.", "Tamam");
            return;
        }

        await MoveAsync(confirm: false);
    }

    private async Task NewCustomerAsync()
    {
        if (await CustomerCreatePage.CreateAsync(Navigation, _customerSearch.Text) is not CustomerItem created)
        {
            return;
        }

        _customers = [created];
        _customerSearch.Text = created.Name;
        _customer.ItemsSource = _customers.Select(c => c.Name + (string.IsNullOrWhiteSpace(c.City) ? string.Empty : " · " + c.City)).ToList();
        _customer.SelectedIndex = 0;
    }

    private Task SearchCustomersAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        _customers = await ApiClient.Instance.CustomersAsync(_customerSearch.Text ?? string.Empty);
        _customer.ItemsSource = _customers.Select(c => c.Name + (string.IsNullOrWhiteSpace(c.City) ? string.Empty : " · " + c.City)).ToList();
        if (_customers.Count == 1)
        {
            _customer.SelectedIndex = 0;
        }
        else if (_customers.Count == 0)
        {
            await DisplayAlertAsync("Müşteri", "Eşleşen müşteri yok.", "Tamam");
        }
    });

    private async Task MoveAsync(bool confirm)
    {
        bool batch = BatchMode;
        if ((batch ? _batch.Count == 0 : _current is null) || SelectedStatus is not int status)
        {
            await DisplayAlertAsync("Tüp", batch ? "Önce tüpleri okutup hareket seçin." : "Önce tüpü sorgulayıp hareket seçin.", "Tamam");
            return;
        }

        CustomerItem? customer = status == CustomerStatus && _customer.SelectedIndex >= 0 ? _customers[_customer.SelectedIndex] : null;
        if (status == CustomerStatus && customer is null)
        {
            await DisplayAlertAsync("Tüp", "Müşteriye teslim için müşteri seçin. Bağlantı yoksa önce bağlantılıyken müşteriyi aratın.", "Tamam");
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
                await DisplayAlertAsync("Plaka", "Plaka geçerli görünmüyor. Örnek: 01 ABC 123", "Tamam");
                return;
            }
        }

        bool? isFull = SelectedFullness;
        string label = _action.Items[_action.SelectedIndex]
            + (customer is null ? string.Empty : $" ({customer.Name})")
            + (plate is null ? string.Empty : $" ({plate})")
            + (isFull is null ? string.Empty : isFull.Value ? " · dolu" : " · boş");
        string subject = batch ? $"{_batch.Count} tüp" : $"{_current!.SerialNumber}: {_current.StatusName}";
        if ((confirm || batch) && !await DisplayAlertAsync("Onay", $"{subject} → {label}\nKaydedilsin mi?", "Kaydet", "Vazgeç"))
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
                string message;
                try
                {
                    message = await ApiClient.Instance.MoveCylinderAsync(current.Barcode, status, customer?.Id, _note.Text, plate, isFull);
                }
                catch (ApiException conflict) when (conflict.IsCustomerConflict)
                {
                    if (!await DisplayAlertAsync("Tüp başka müşteride", conflict.Message, "Yine de kaydet", "Vazgeç"))
                    {
                        return;
                    }

                    message = await ApiClient.Instance.MoveCylinderAsync(current.Barcode, status, customer?.Id, _note.Text, plate, isFull, confirmCustomerChange: true);
                }

                await DisplayAlertAsync("Kaydedildi", message, "Tamam");
                await LookupAsync(current.Barcode, quick: false);
            }
            catch (ServerUnreachableException)
            {
                OfflineMoves.Add(new PendingMove(current.Barcode, status, customer?.Id, _note.Text, label, DateTime.Now, plate, isFull));
                _movePanel.IsVisible = false;
                _detail.Text = $"📶 Bağlantı yok: {current.SerialNumber} → {label} telefonda saklandı. Bağlantı gelince otomatik gönderilecek.";
            }
        });
    }

    private Task SaveBatchAsync(int status, CustomerItem? customer, string? plate, bool? isFull, string label) =>
        Ui.RunAsync(this, _busy, async () =>
        {
            List<string> codes = [.. _batch];
            List<string> kept = [];
            try
            {
                CylinderBulkResponse response;
                try
                {
                    response = await ApiClient.Instance.MoveCylindersAsync(codes, status, customer?.Id, _note.Text, plate, isFull);
                }
                catch (ApiException conflict) when (conflict.IsCustomerConflict)
                {
                    IReadOnlyList<string> clashing = conflict.ConflictCodes();
                    string summary = $"{clashing.Count} tüp sistemde başka bir müşteride görünüyor. Hiçbir hareket kaydedilmedi.\n\n{conflict.Message}";
                    if (!await DisplayAlertAsync("Başka müşteride kayıtlı tüpler", summary, "Seçenekler", "Vazgeç"))
                    {
                        return;
                    }

                    const string all = "Yine de hepsini kaydet";
                    const string skip = "Bunları çıkar, kalanları kaydet";
                    string? choice = await DisplayActionSheetAsync($"{clashing.Count} çakışan tüp", "Vazgeç", null, all, skip);
                    if (choice == skip)
                    {
                        kept = codes.Where(code => clashing.Contains(code, StringComparer.OrdinalIgnoreCase)).ToList();
                        codes = codes.Except(kept, StringComparer.OrdinalIgnoreCase).ToList();
                        if (codes.Count == 0)
                        {
                            await DisplayAlertAsync("Toplu hareket", "Çakışanlar çıkarılınca kaydedilecek tüp kalmadı.", "Tamam");
                            return;
                        }
                    }
                    else if (choice != all)
                    {
                        return;
                    }

                    response = await ApiClient.Instance.MoveCylindersAsync(codes, status, customer?.Id, _note.Text, plate, isFull, confirmCustomerChange: choice == all);
                }

                List<string> failed = response.Results.Where(r => !r.Ok).Select(r => $"{r.Code}: {r.Message}").ToList();
                List<string> warnings = response.Results.Where(r => r.Ok && r.Message.Contains('⚠')).Select(r => r.Message).ToList();
                await DisplayAlertAsync(response.Failed == 0 ? "Kaydedildi" : "Kısmen kaydedildi",
                    response.Message
                    + (failed.Count == 0 ? string.Empty : "\n\nKaydedilemeyenler:\n" + string.Join("\n", failed))
                    + (warnings.Count == 0 ? string.Empty : "\n\nMüşteri değiştirilenler:\n" + string.Join("\n", warnings))
                    + (kept.Count == 0 ? string.Empty : $"\n\n{kept.Count} çakışan tüp listede bırakıldı."), "Tamam");
                _batch.Clear();
                _batch.AddRange(kept);
                _batch.AddRange(response.Results.Where(r => !r.Ok).Select(r => r.Code));
            }
            catch (ServerUnreachableException)
            {
                foreach (string code in codes)
                {
                    OfflineMoves.Add(new PendingMove(code, status, customer?.Id, _note.Text, label, DateTime.Now, plate, isFull));
                }

                _batch.Clear();
                _batch.AddRange(kept);
                await DisplayAlertAsync("Bağlantı yok", $"{codes.Count} tüp hareketi telefonda saklandı. Bağlantı gelince otomatik gönderilecek.", "Tamam");
            }

            RefreshBatch();
        });
}
