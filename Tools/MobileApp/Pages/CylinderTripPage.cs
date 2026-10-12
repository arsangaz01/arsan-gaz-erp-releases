using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Sefer bazlı araç ve tüp takibi: yükleme, teslim, dönüş okutması ve tutarlılık kontrolü.
public sealed class CylinderTripPage : ContentPage
{
    private static readonly string[] Modes = ["Yükleme", "Müşteriye teslim", "Depo dönüşü", "Kontrol"];

    private readonly Label _overview = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Picker _trip = new() { Title = "Aktif sefer" };
    private readonly Picker _vehicle = new() { Title = "Sefer aracı" };
    private readonly Picker _mode = new() { Title = "İşlem" };
    private readonly Entry _plateEntry = new() { Placeholder = "Yeni plaka (01 ABC 123)" };
    private readonly Entry _customerSearch = new() { Placeholder = "Müşteri ara", ReturnType = ReturnType.Search, IsVisible = false };
    private readonly Button _customerSearchButton;
    private readonly Picker _customer = new() { Title = "Teslim müşterisi", IsVisible = false };
    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no", ReturnType = ReturnType.Done };
    private readonly Label _scannedText = new() { FontSize = 14 };
    private readonly Label _result = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Border _resultCard;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<string> _scanned = [];
    private List<ActiveTripSummary> _trips = [];
    private List<CustomerItem> _customers = [];

    public CylinderTripPage()
    {
        Title = "Sefer";
        Ui.ApplyPageChrome(this);
        foreach (string mode in Modes)
        {
            _mode.Items.Add(mode);
        }

        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => ApplyMode();
        _trip.SelectedIndexChanged += (_, _) => RefreshVehicles();
        _customerSearch.Completed += async (_, _) => await SearchCustomersAsync();

        _resultCard = Ui.Card(_result);
        _resultCard.IsVisible = false;
        _customerSearchButton = Ui.Secondary("Müşteri ara", async (_, _) => await SearchCustomersAsync());
        _customerSearchButton.IsVisible = false;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Seferdeki araçları seçin, tüpleri araca yükleyin, müşteriye teslim ve depo dönüşünü okutun. Dönüşte yüklenenlerle eşleşmeyen kodlar kırmızı uyarı verir; teslim ve dönüş okutulmayan tüpler gaz türüne göre listelenir."),
                    Ui.Section(
                        "Seferdeki araçlar",
                        _overview,
                        Ui.ActionRow(
                            Ui.Secondary("Yenile", async (_, _) => await RefreshTripsAsync()),
                            Ui.Secondary("Yeni sefer", async (_, _) => await StartTripAsync()))),
                    Ui.Section(
                        "Sefer ve araç",
                        _trip,
                        _vehicle,
                        _plateEntry,
                        Ui.Secondary("Aracı sefere ekle", async (_, _) => await AddVehicleAsync())),
                    Ui.Section(
                        "Okutma",
                        _mode,
                        _customerSearch,
                        _customerSearchButton,
                        _customer,
                        Ui.Primary("📷 Okut", async (_, _) => await ScanAsync()),
                        _code,
                        Ui.ActionRow(
                            Ui.Secondary("Kodu ekle", (_, _) => AddManual()),
                            Ui.Secondary("Listeyi temizle", (_, _) => { _scanned.Clear(); RefreshScanned(); })),
                        _scannedText,
                        Ui.Primary("Kaydet / kontrol et", async (_, _) => await SubmitAsync())),
                    _busy,
                    _resultCard,
                    Ui.Secondary("Seferi kapat", async (_, _) => await CloseTripAsync())
                }
            }
        };

        RefreshScanned();
        ApplyMode();
        _ = RefreshTripsAsync();
    }

    private ActiveTripSummary? SelectedTrip => _trip.SelectedIndex >= 0 && _trip.SelectedIndex < _trips.Count ? _trips[_trip.SelectedIndex] : null;

    private string? SelectedPlate => _vehicle.SelectedItem as string;

    private CustomerItem? SelectedCustomer => _customer.SelectedIndex >= 0 && _customer.SelectedIndex < _customers.Count ? _customers[_customer.SelectedIndex] : null;

    private void ApplyMode()
    {
        bool deliver = _mode.SelectedIndex == 1;
        _customerSearch.IsVisible = deliver;
        _customerSearchButton.IsVisible = deliver;
        _customer.IsVisible = deliver;
    }

    private void RefreshScanned() =>
        _scannedText.Text = _scanned.Count == 0 ? "Henüz tüp okutulmadı." : $"{_scanned.Count} tüp:\n{string.Join(", ", _scanned)}";

    private void AddManual()
    {
        string code = (_code.Text ?? string.Empty).Trim();
        if (code.Length == 0)
        {
            return;
        }

        Add([code]);
        _code.Text = string.Empty;
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

    private async Task ScanAsync()
    {
        string? value = await ScanPage.ScanAsync(Navigation);
        if (value is not null)
        {
            Add([value]);
        }
    }

    private async Task RefreshTripsAsync()
    {
        _busy.IsVisible = true;
        try
        {
            _trips = await ApiClient.Instance.ActiveTripsAsync();
            _trip.ItemsSource = _trips.Select(t => $"{t.Title} · {t.LoadedTotal} yüklü · {t.Vehicles.Count(v => v.OnTrip)} araç").ToList();
            if (_trips.Count == 0)
            {
                _overview.Text = "Aktif sefer yok. \"Yeni sefer\" ile başlatın.";
                _trip.SelectedIndex = -1;
                _vehicle.ItemsSource = null;
                return;
            }

            if (_trip.SelectedIndex < 0)
            {
                _trip.SelectedIndex = 0;
            }

            ShowOverview();
            RefreshVehicles();
        }
        catch (Exception exception)
        {
            _overview.Text = "Sefer listesi alınamadı: " + exception.Message;
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private void ShowOverview()
    {
        if (SelectedTrip is not ActiveTripSummary trip)
        {
            _overview.Text = string.Empty;
            return;
        }

        List<string> lines =
        [
            $"{trip.Title} · başlangıç {trip.StartedAt:dd.MM.yyyy HH:mm}",
            $"Yüklü {trip.LoadedTotal} · teslim {trip.DeliveredTotal} · depo dönüş {trip.ReturnedTotal}"
        ];
        foreach (TripVehicleSummary vehicle in trip.Vehicles.Where(v => v.OnTrip))
        {
            lines.Add($"🚚 {vehicle.Plate} · {vehicle.LoadedCount} tüp (seferde)");
        }

        if (trip.Vehicles.All(v => !v.OnTrip))
        {
            lines.Add("Seferde dönmemiş araç görünmüyor.");
        }

        _overview.Text = string.Join(Environment.NewLine, lines);
    }

    private void RefreshVehicles()
    {
        ShowOverview();
        if (SelectedTrip is null)
        {
            _vehicle.ItemsSource = null;
            return;
        }

        List<string> plates = SelectedTrip.Vehicles.Select(v => v.Plate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _vehicle.ItemsSource = plates;
        if (plates.Count > 0 && (_vehicle.SelectedIndex < 0 || _vehicle.SelectedIndex >= plates.Count))
        {
            _vehicle.SelectedIndex = 0;
        }
    }

    private async Task StartTripAsync()
    {
        string? note = await DisplayPromptAsync("Yeni sefer", "Not (isteğe bağlı):", maxLength: 120);
        if (note is null)
        {
            return;
        }

        _busy.IsVisible = true;
        try
        {
            TripStartResponse response = await ApiClient.Instance.StartTripAsync(note, null);
            await DisplayAlertAsync("Sefer", response.Message, "Tamam");
            await RefreshTripsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Sefer", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task AddVehicleAsync()
    {
        if (SelectedTrip is null)
        {
            await DisplayAlertAsync("Sefer", "Önce aktif bir sefer seçin.", "Tamam");
            return;
        }

        string plate = (_plateEntry.Text ?? string.Empty).Trim();
        if (plate.Length == 0)
        {
            await DisplayAlertAsync("Sefer", "Plaka girin.", "Tamam");
            return;
        }

        _busy.IsVisible = true;
        try
        {
            string message = await ApiClient.Instance.AddTripVehicleAsync(SelectedTrip.Id, plate);
            _plateEntry.Text = string.Empty;
            await DisplayAlertAsync("Sefer", message, "Tamam");
            await RefreshTripsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Sefer", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task SearchCustomersAsync()
    {
        string query = (_customerSearch.Text ?? string.Empty).Trim();
        if (query.Length < 2)
        {
            await DisplayAlertAsync("Müşteri", "En az 2 karakter yazın.", "Tamam");
            return;
        }

        _busy.IsVisible = true;
        try
        {
            _customers = await ApiClient.Instance.CustomersAsync(query);
            _customer.ItemsSource = _customers.Select(c => $"{c.Name} ({c.Code})").ToList();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Müşteri", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task SubmitAsync()
    {
        if (SelectedTrip is null)
        {
            await DisplayAlertAsync("Sefer", "Aktif sefer seçin.", "Tamam");
            return;
        }

        string? plate = SelectedPlate;
        int mode = _mode.SelectedIndex;
        if (mode is 0 or 2 && string.IsNullOrWhiteSpace(plate))
        {
            await DisplayAlertAsync("Sefer", "Yükleme ve dönüş için araç seçin.", "Tamam");
            return;
        }

        _busy.IsVisible = true;
        _resultCard.IsVisible = false;
        try
        {
            TripReconcileResponse? reconcile = null;
            string message;
            switch (mode)
            {
                case 0:
                    TripLoadResponse load = await ApiClient.Instance.LoadTripCylindersAsync(SelectedTrip.Id, plate!, _scanned);
                    message = load.Message;
                    break;
                case 1:
                    if (SelectedCustomer is null)
                    {
                        await DisplayAlertAsync("Sefer", "Teslim için müşteri seçin.", "Tamam");
                        return;
                    }

                    if (_scanned.Count == 0)
                    {
                        await DisplayAlertAsync("Sefer", "En az bir tüp okutun.", "Tamam");
                        return;
                    }

                    message = await ApiClient.Instance.DeliverTripCylindersAsync(SelectedTrip.Id, SelectedCustomer.Id, _scanned, null);
                    break;
                case 2:
                    reconcile = await ApiClient.Instance.ReturnTripCylindersAsync(SelectedTrip.Id, plate!, _scanned, null);
                    message = reconcile.Message;
                    break;
                default:
                    reconcile = await ApiClient.Instance.CheckTripAsync(SelectedTrip.Id, plate);
                    message = reconcile.Message;
                    break;
            }

            ShowResult(message, reconcile);
            if (reconcile?.HasAlerts == true)
            {
                await DisplayAlertAsync("Sefer uyarısı", reconcile.Message, "Tamam");
            }

            _scanned.Clear();
            RefreshScanned();
            await RefreshTripsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Sefer", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private void ShowResult(string message, TripReconcileResponse? reconcile)
    {
        _result.Text = message;
        _result.TextColor = reconcile?.HasAlerts == true ? Colors.Red : Ui.Muted;
        if (reconcile is not null && reconcile.UnexpectedReturnCodes.Count > 0)
        {
            _result.Text += Environment.NewLine + "Eşleşmeyen: " + string.Join(", ", reconcile.UnexpectedReturnCodes);
        }

        if (reconcile is not null && reconcile.ShortagesByGas.Count > 0)
        {
            _result.Text += Environment.NewLine + "Okutulmayan: "
                + string.Join(", ", reconcile.ShortagesByGas.Select(s => $"{s.GasType} ×{s.Count}"));
        }

        _resultCard.IsVisible = true;
    }

    private async Task CloseTripAsync()
    {
        if (SelectedTrip is null)
        {
            return;
        }

        bool ok = await DisplayAlertAsync("Seferi kapat", $"{SelectedTrip.Title} kapatılsın mı? Eksik okutmalar raporlanır.", "Kapat", "İptal");
        if (!ok)
        {
            return;
        }

        _busy.IsVisible = true;
        try
        {
            TripCloseResponse closed = await ApiClient.Instance.CloseTripAsync(SelectedTrip.Id);
            ShowResult(closed.Message, closed.Reconcile);
            if (closed.Reconcile.HasAlerts)
            {
                await DisplayAlertAsync("Sefer kapatıldı — uyarı", closed.Reconcile.Message, "Tamam");
            }

            await RefreshTripsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Sefer", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }
}
