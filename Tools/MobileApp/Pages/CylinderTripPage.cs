using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Sefer bazlı araç ve tüp takibi: depoda dolu yükleme, müşteride teslim ve boş alma, depo dönüşü ve tutarlılık kontrolü.
public sealed class CylinderTripPage : ContentPage
{
    private const int LoadMode = 0;
    private const int DeliverMode = 1;
    private const int PickupMode = 2;
    private const int ReturnMode = 3;
    private const int CheckMode = 4;

    private readonly (string Label, int Kind, string Permission)[] _modes = BuildModes();
    private readonly List<Button> _stepButtons = [];
    private readonly bool _routes = ApiClient.Instance.Can(Perm.TripRoute);
    private readonly bool _saveLocation = ApiClient.Instance.Can(Perm.CustomerLocation);
    private readonly VerticalStackLayout _route = new() { Spacing = 10 };
    private readonly Label _routeInfo = Ui.Note(string.Empty);
    private List<TripStopItem> _stops = [];
    private readonly HashSet<int> _verifyAsked = [];

    private readonly Label _overview = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Picker _trip = new() { Title = "Aktif sefer" };
    private readonly Picker _vehicle = new() { Title = "Sefer aracı" };
    private readonly Picker _mode = new() { Title = "İşlem", IsVisible = false };
    private readonly Label _stepHint = new() { FontSize = 14, FontFamily = "OpenSansSemibold", TextColor = Ui.AccentDark, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Picker _groupPlate = new() { Title = "Araç filtresi" };
    private readonly ContentView _groups = new();
    private readonly Label _groupsTotal = Ui.Note(string.Empty);
    private readonly Label _customerLabel = new() { FontSize = 18, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private CustomerItem? _selectedCustomer;
    private readonly Stack<TripUndo> _undo = new();
    private readonly record struct TripUndo(bool Customer, string? Code, CustomerItem? PreviousCustomer);
    private readonly Border _customerSection;
    private readonly Label _tripLine = new() { FontSize = 18, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private readonly ContentView _tripTiles = new() { IsVisible = false };
    private readonly VerticalStackLayout _tripPickers;
    private readonly Button _startBig;
    private readonly Button _finishSimple;
    private readonly VerticalStackLayout _workArea;
    private readonly VerticalStackLayout _detailsBox;
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
    private bool _firstTrip = true;

    private static (string Label, int Kind, string Permission)[] BuildModes()
    {
        List<(string Label, int Kind, string Permission)> modes =
        [
            ("Araca yükle", LoadMode, "sefer-yukle"),
            ("Müşteriye teslim", DeliverMode, "sefer-teslim"),
            ("Aracı boşalt", ReturnMode, "sefer-teslim"),
            ("Müşteriden boş alma", PickupMode, "sefer-teslim"),
            ("Kontrol", CheckMode, "sefer-goruntule")
        ];
        return modes.Where(m => ApiClient.Instance.Can(m.Permission)).ToArray();
    }

    public CylinderTripPage()
    {
        Title = "Sefer";
        Ui.ApplyPageChrome(this);
        foreach ((string label, _, _) in _modes)
        {
            _mode.Items.Add(label);
        }

        _code.Completed += (_, _) =>
        {
            AddManual();
            _code.Focus();
        };

        VerticalStackLayout steps = new() { Spacing = 10 };
        int stepNumber = 0;
        for (int i = 0; i < _modes.Length; i++)
        {
            int index = i;
            int kind = _modes[i].Kind;
            if (kind is CheckMode or PickupMode)
            {
                continue;
            }

            stepNumber++;
            string label = $"{stepNumber}. {_modes[i].Label}";
            Button button = Ui.Secondary($"{StepIcon(kind)}  {label}", async (_, _) =>
            {
                _mode.SelectedIndex = index;
                ApplyMode();
                await StartStepAsync();
            });
            button.FontSize = 18;
            button.MinimumHeightRequest = 60;
            _stepButtons.Add(button);
            steps.Children.Add(button);
        }

        _mode.SelectedIndex = _modes.Length > 0 ? 0 : -1;
        _mode.SelectedIndexChanged += (_, _) => ApplyMode();
        _trip.SelectedIndexChanged += (_, _) => RefreshVehicles();
        _vehicle.SelectedIndexChanged += async (_, _) =>
        {
            UpdateSimpleView();
            await RefreshRouteAsync();
        };
        _groupPlate.SelectedIndexChanged += async (_, _) => await RefreshGroupsAsync();
        _customerSearch.Completed += async (_, _) => await SearchCustomersAsync();

        _resultCard = Ui.Card(_result);
        _resultCard.IsVisible = false;
        _customerSearchButton = Ui.Secondary("Müşteri ara", async (_, _) => await SearchCustomersAsync());
        _customerSection = Ui.Section("👤 HANGİ MÜŞTERİ?",
            _customerLabel,
            Ui.Secondary("🔎 Müşteri ara / değiştir", async (_, _) => await PickCustomerThenScanAsync()));
        ShowCustomer();

        bool canStart = ApiClient.Instance.Can("sefer-baslat");
        bool canFinish = ApiClient.Instance.Can("sefer-kapat") || ApiClient.Instance.Session?.IsDriver == true;
        _tripPickers = new VerticalStackLayout { Spacing = 4, IsVisible = false, Children = { _trip, _vehicle } };
        _startBig = Ui.Allowed(Ui.Primary("🚚  Sefer başlat", async (_, _) => await StartTripAsync()), "sefer-baslat");
        _startBig.FontSize = 20;
        _startBig.MinimumHeightRequest = 72;
        _startBig.IsVisible = false;
        _finishSimple = Ui.Primary("🏁  Seferi bitir", async (_, _) => await CloseTripAsync());
        _finishSimple.FontSize = 18;
        _finishSimple.MinimumHeightRequest = 60;
        _finishSimple.IsVisible = false;
        if (!canFinish)
        {
            _finishSimple.IsVisible = false;
        }

        Button save = Ui.Allowed(Ui.Primary("✅  Kaydet", async (_, _) => await SubmitAsync()), "sefer-yukle|sefer-teslim");
        save.FontSize = 18;
        save.MinimumHeightRequest = 60;
        Button undoLast = Ui.Secondary("↩️  Geri al", async (_, _) => await UndoLastAsync());
        undoLast.MinimumHeightRequest = 48;

        _workArea = new VerticalStackLayout
        {
            Spacing = 14,
            IsVisible = false,
            Children =
            {
                Ui.Card(new VerticalStackLayout { Spacing = 10, Children = { _mode, steps, _stepHint } }),
                _customerSection,
                Ui.Card(new VerticalStackLayout { Spacing = 10, Children = { _scannedText, _code, undoLast, save } }),
                _busy,
                _resultCard
            }
        };

        _detailsBox = new VerticalStackLayout
        {
            Spacing = 14,
            IsVisible = false,
            Children =
            {
                Ui.Section(
                    "Sefer ayrıntısı",
                    _overview,
                    Ui.ActionRow(
                        Ui.Secondary("🔄 Yenile", async (_, _) => await RefreshTripsAsync()),
                        Ui.Allowed(Ui.Secondary("➕ Sefere araç ekle", async (_, _) => await AddVehicleAsync()), "sefer-baslat")),
                    Ui.Allowed(Ui.Secondary("🚚 Yeni sefer başlat", async (_, _) => await StartTripAsync()), "sefer-baslat")),
                Ui.Section(
                    "OKUTMA SEÇENEKLERİ",
                    Ui.ActionRow(
                        Ui.Secondary("📷 Tek okut", async (_, _) => await ScanAsync()),
                        Ui.Secondary("📷 Toplu okut", async (_, _) => await ScanManyAsync())),
                    Ui.Secondary("Kodu ekle", (_, _) => AddManual()),
                    Ui.Secondary("🗑️ Okutulanları temizle", (_, _) => ClearScanned(trackUndo: false)),
                    Ui.Secondary("↩️ Son işlemi geri al", async (_, _) => await UndoLastAsync()),
                    _customerSearch,
                    _customerSearchButton,
                    _customer),
                Ui.Section(
                    "🗺️ ROTA (MÜŞTERİ SIRASI)",
                    _routeInfo,
                    _route,
                    Ui.ActionRow(
                        Ui.Secondary("Müşteri ekle", async (_, _) => await AddStopAsync()),
                        Ui.Secondary("Önerilenler", async (_, _) => await SuggestStopsAsync()))).ShowIf(_routes),
                Ui.Section(
                    "📋 YÜK LİSTESİ (GAZ TÜRÜNE GÖRE)",
                    _groupPlate,
                    _groupsTotal,
                    _groups,
                    Ui.Secondary("🔄 Listeyi yenile", async (_, _) => await RefreshGroupsAsync())),
                Ui.Allowed(Ui.Section(
                    "🏁 Seferi bitir",
                    Ui.Secondary("📝 Okutulmayan tüplere gerekçe yaz", async (_, _) => await ExplainMissingAsync()),
                    Ui.Primary("🏁 Seferi kapat", async (_, _) => await CloseTripAsync())), "sefer-kapat")
            }
        };

        Button details = Ui.Secondary(
            _routes ? "⚙️ Ayrıntılar (rota, yük listesi, diğer seçenekler)" : "⚙️ Ayrıntılar (yük listesi, diğer seçenekler)",
            (_, _) => { });
        details.Clicked += (_, _) =>
        {
            _detailsBox.IsVisible = !_detailsBox.IsVisible;
            details.Text = _detailsBox.IsVisible
                ? "⚙️ Ayrıntıları gizle"
                : (_routes ? "⚙️ Ayrıntılar (rota, yük listesi, diğer seçenekler)" : "⚙️ Ayrıntılar (yük listesi, diğer seçenekler)");
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    _startBig,
                    Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { _tripLine, _tripTiles, _tripPickers } }),
                    _finishSimple,
                    _workArea,
                    details,
                    _detailsBox
                }
            }
        };

        RefreshScanned();
        ApplyMode();
        _ = RefreshTripsAsync();
    }

    private ActiveTripSummary? SelectedTrip => _trip.SelectedIndex >= 0 && _trip.SelectedIndex < _trips.Count ? _trips[_trip.SelectedIndex] : null;

    private string? SelectedPlate => _vehicle.SelectedItem as string;

    private CustomerItem? SelectedCustomer => _selectedCustomer ?? (_customer.SelectedIndex >= 0 && _customer.SelectedIndex < _customers.Count ? _customers[_customer.SelectedIndex] : null);

    private int SelectedMode => _mode.SelectedIndex >= 0 && _mode.SelectedIndex < _modes.Length ? _modes[_mode.SelectedIndex].Kind : CheckMode;

    private void ApplyMode()
    {
        _customerSection.IsVisible = SelectedMode is DeliverMode or PickupMode;
        for (int i = 0; i < _stepButtons.Count; i++)
        {
            int modeIndex = ModeIndexForStepButton(i);
            Ui.Highlight(_stepButtons[i], modeIndex == _mode.SelectedIndex);
        }

        _stepHint.Text = SelectedMode switch
        {
            LoadMode => "👉 Düğmeye dokunun, kamera açılır: araca koyduğunuz tüpleri okutun, sonra Kaydet'e basın.",
            DeliverMode => "👉 Düğmeye dokunun: müşteriyi seçin, kamera açılır, verdiğiniz tüpleri okutun, Kaydet'e basın.",
            PickupMode => "👉 Müşteriden aldığınız boş tüpleri okutun, Kaydet'e basın.",
            ReturnMode => "👉 Düğmeye dokunun, kamera açılır: araçtan inen bütün tüpleri okutun, Kaydet'e basın.",
            _ => "👉 Araçtaki tüpleri okutun; yük listesiyle karşılaştırılır."
        };
    }

    private int ModeIndexForStepButton(int stepButtonIndex)
    {
        int seen = -1;
        for (int i = 0; i < _modes.Length; i++)
        {
            if (_modes[i].Kind is CheckMode or PickupMode)
            {
                continue;
            }

            seen++;
            if (seen == stepButtonIndex)
            {
                return i;
            }
        }

        return -1;
    }

    private async Task StartStepAsync()
    {
        if (SelectedTrip is null)
        {
            return;
        }

        switch (SelectedMode)
        {
            case LoadMode or ReturnMode:
                await ScanManyAsync();
                break;
            case DeliverMode or PickupMode:
                await PickCustomerThenScanAsync();
                break;
        }
    }

    private async Task PickCustomerThenScanAsync()
    {
        string title = SelectedMode == PickupMode ? "Boş alınan müşteri" : "Teslim müşterisi";
        CustomerItem? customer = await CustomerPickPage.OpenAsync(Navigation, title);
        if (customer is null)
        {
            return;
        }

        PushCustomerUndo();
        _selectedCustomer = customer;
        ShowCustomer();
        await ScanManyAsync();
    }

    private void ShowCustomer()
    {
        if (_selectedCustomer is CustomerItem customer)
        {
            _customerLabel.Text = "✅ " + customer.Name + (string.IsNullOrWhiteSpace(customer.City) ? string.Empty : $"  ·  📍 {customer.City}");
            _customerLabel.TextColor = Ui.Good;
        }
        else
        {
            _customerLabel.Text = "Müşteri seçilmedi";
            _customerLabel.TextColor = Ui.Muted;
        }
    }

    private void SelectMode(int kind)
    {
        int index = Array.FindIndex(_modes, m => m.Kind == kind);
        if (index >= 0)
        {
            _mode.SelectedIndex = index;
            ApplyMode();
        }
    }

    private bool HasMode(int kind) => _modes.Any(m => m.Kind == kind);

    private static string StepIcon(int kind) => kind switch
    {
        LoadMode => "📦",
        DeliverMode => "🤝",
        PickupMode => "♻️",
        ReturnMode => "🏭",
        _ => "🔍"
    };

    private string Step(int kind)
    {
        int index = Array.FindIndex(_modes, m => m.Kind == kind);
        if (index < 0)
        {
            return string.Empty;
        }

        int number = _modes.Take(index + 1).Count(m => m.Kind is not CheckMode and not PickupMode);
        return $"\"{number}. {_modes[index].Label}\"";
    }

    private async Task NextStepAsync(int mode, string message, TripReconcileResponse? reconcile)
    {
        bool failed = message.StartsWith("0/", StringComparison.Ordinal) || message.StartsWith("0 tüp", StringComparison.Ordinal);
        if (failed)
        {
            await DisplayAlertAsync("Kaydedilemedi", message, "Tamam");
            return;
        }

        if (reconcile?.HasAlerts == true && mode != ReturnMode)
        {
            return;
        }

        switch (mode)
        {
            case LoadMode when HasMode(DeliverMode):
                SelectMode(DeliverMode);
                await DisplayAlertAsync("Yükleme kaydedildi", $"{message}\n\nŞimdi {Step(DeliverMode)}: müşteriyi seçip teslim ettiğiniz tüpleri okutun.", "Tamam");
                break;
            case DeliverMode when HasMode(PickupMode):
                if (await DisplayAlertAsync("Teslim kaydedildi", message + "\n\nBu müşteriden boş tüp aldınız mı?", "Evet, boşları okut", "Hayır, sonraki müşteri"))
                {
                    SelectMode(PickupMode);
                    await ScanManyAsync();
                }
                else if (!await OfferFinishIfEmptyAsync())
                {
                    NextCustomer();
                }

                break;
            case DeliverMode:
                if (!await OfferFinishIfEmptyAsync())
                {
                    await DisplayAlertAsync("Teslim kaydedildi", message, "Tamam");
                    NextCustomer();
                }

                break;
            case PickupMode:
                await DisplayAlertAsync("Boş alım kaydedildi", message, "Tamam");
                NextCustomer();
                break;
            case ReturnMode when ApiClient.Instance.Can("sefer-kapat") || ApiClient.Instance.Session?.IsDriver == true:
                if (await DisplayAlertAsync(reconcile?.HasAlerts == true ? "Araç boşaltıldı — uyarı" : "Araç boşaltıldı", message + "\n\nSefer şimdi kapatılsın mı?", "Seferi kapat", "Daha sonra"))
                {
                    await CloseTripAsync(ask: false);
                }

                break;
        }
    }

    private void NextCustomer()
    {
        _undo.Clear();
        _selectedCustomer = null;
        ShowCustomer();
        SelectMode(DeliverMode);
        _stepHint.Text = "👉 Sıradaki müşteri için \"Müşteriye teslim\"e dokunun. Teslimatlar bitince "
            + (HasMode(ReturnMode) ? $"depoda {Step(ReturnMode)}." : "depoya dönün.");
    }

    private void PushCustomerUndo() => _undo.Push(new TripUndo(true, null, _selectedCustomer));

    private void PushScanUndo(string code) => _undo.Push(new TripUndo(false, code, null));

    private void ClearScanned(bool trackUndo)
    {
        if (!trackUndo)
        {
            List<TripUndo> customers = [.. _undo.Where(s => s.Customer)];
            _undo.Clear();
            for (int i = customers.Count - 1; i >= 0; i--)
            {
                _undo.Push(customers[i]);
            }
        }

        _scanned.Clear();
        RefreshScanned();
    }

    private async Task UndoLastAsync()
    {
        if (_undo.Count == 0)
        {
            if (_scanned.Count > 0)
            {
                string code = _scanned[^1];
                _scanned.RemoveAt(_scanned.Count - 1);
                RefreshScanned();
                await DisplayAlertAsync("Geri al", $"Son okutma geri alındı: {code}", "Tamam");
                return;
            }

            await DisplayAlertAsync("Geri al", "Geri alınacak son işlem yok.", "Tamam");
            return;
        }

        TripUndo step = _undo.Pop();
        if (step.Customer)
        {
            _selectedCustomer = step.PreviousCustomer;
            ShowCustomer();
            return;
        }

        if (!string.IsNullOrEmpty(step.Code))
        {
            for (int i = _scanned.Count - 1; i >= 0; i--)
            {
                if (string.Equals(_scanned[i], step.Code, StringComparison.OrdinalIgnoreCase))
                {
                    _scanned.RemoveAt(i);
                    break;
                }
            }

            RefreshScanned();
        }
    }

    private async Task<bool> OfferFinishIfEmptyAsync()
    {
        int onVehicle = VehicleOnBoard();
        if (!CanFinishTrip(onVehicle))
        {
            return false;
        }

        if (!await DisplayAlertAsync(
                "Teslimler bitti",
                "Araçta işlem yapılacak tüp kalmadı. Seferi bitirip özeti Tamamlanmış seferler'e taşıyalım mı?",
                "Seferi bitir",
                "Daha sonra"))
        {
            return false;
        }

        await CloseTripAsync(ask: false);
        return true;
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
                PushScanUndo(code);
            }
        }

        RefreshScanned();
    }

    private async Task ScanAsync()
    {
        try
        {
            Add(await ScanPage.ScanOneOrManyAsync(Navigation, _scanned));
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
            Add(await ScanPage.ScanManyAsync(Navigation, _scanned));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task RefreshTripsAsync(int? selectTripId = null)
    {
        _busy.IsVisible = true;
        try
        {
            _trips = await ApiClient.Instance.ActiveTripsAsync();
            _trip.ItemsSource = _trips.Select(t => $"{t.Title} · {t.LoadedTotal} yüklü · {t.Vehicles.Count(v => v.OnTrip)} araç").ToList();
            if (_trips.Count == 0)
            {
                _overview.Text = "Aktif sefer yok. \"Sefer başlat\" ile başlatın.";
                _trip.SelectedIndex = -1;
                _vehicle.ItemsSource = null;
                _groupPlate.ItemsSource = null;
                _groupsTotal.Text = string.Empty;
                _groups.Content = Ui.Note("Aktif sefer yok.");
                UpdateSimpleView();
                await RefreshRouteAsync();
                return;
            }

            int wanted = selectTripId is int id ? _trips.FindIndex(t => t.Id == id) : -1;
            if (wanted >= 0)
            {
                _trip.SelectedIndex = wanted;
            }
            else if (_trip.SelectedIndex < 0)
            {
                _trip.SelectedIndex = 0;
            }

            ShowOverview();
            RefreshVehicles();
            await RefreshRouteAsync();
            if (_firstTrip)
            {
                _firstTrip = false;
                if (SelectedTrip?.Vehicles.FirstOrDefault(v => string.Equals(v.Plate, SelectedPlate, StringComparison.OrdinalIgnoreCase)) is { OnTrip: true, OnVehicle: > 0 })
                {
                    SelectMode(HasMode(ReturnMode) ? ReturnMode : DeliverMode);
                }
            }
        }
        catch (Exception exception)
        {
            _overview.Text = "Sefer listesi alınamadı: " + exception.Message;
            _tripLine.Text = "⚠️ Sefer listesi alınamadı. İnternet bağlantısını kontrol edip \"Ayrıntılar > Yenile\"ye basın.";
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
            $"Dolu yüklü {trip.LoadedTotal} · teslim {trip.DeliveredTotal} · boş alınan {trip.PickedUpTotal} · depo dönüş {trip.ReturnedTotal}"
        ];
        foreach (TripVehicleSummary vehicle in trip.Vehicles.Where(v => v.OnTrip))
        {
            lines.Add($"🚚 {vehicle.Plate} · {vehicle.LoadedCount} yüklendi · araçta {vehicle.OnVehicle}");
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
            UpdateSimpleView();
            return;
        }

        string? previous = SelectedPlate;
        List<string> plates = SelectedTrip.Vehicles.Select(v => v.Plate).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _vehicle.ItemsSource = plates;
        int keep = previous is null ? -1 : plates.FindIndex(p => string.Equals(p, previous, StringComparison.OrdinalIgnoreCase));
        if (keep >= 0)
        {
            _vehicle.SelectedIndex = keep;
        }
        else if (plates.Count > 0)
        {
            _vehicle.SelectedIndex = 0;
        }

        string? groupPlate = _groupPlate.SelectedIndex > 0 ? _groupPlate.SelectedItem as string : null;
        List<string> filter = ["Tüm araçlar", .. plates];
        _groupPlate.ItemsSource = filter;
        int groupIndex = groupPlate is null ? 0 : Math.Max(0, filter.IndexOf(groupPlate));
        if (_groupPlate.SelectedIndex == groupIndex)
        {
            _ = RefreshGroupsAsync();
        }
        else
        {
            _groupPlate.SelectedIndex = groupIndex;
        }

        UpdateSimpleView();
    }

    private void UpdateSimpleView()
    {
        bool any = _trips.Count > 0;
        _startBig.IsVisible = !any && ApiClient.Instance.Can("sefer-baslat");
        _workArea.IsVisible = any;
        int plates = (_vehicle.ItemsSource as List<string>)?.Count ?? 0;
        _tripPickers.IsVisible = _trips.Count > 1 || plates > 1;
        _tripTiles.IsVisible = any;
        if (!any)
        {
            _tripLine.Text = ApiClient.Instance.Can("sefer-baslat")
                ? "Aktif sefer yok. Başlamak için yukarıdaki \"Sefer başlat\"a basın."
                : "Aktif sefer yok. Sefer depoda başlatılınca burada görünür.";
            _finishSimple.IsVisible = false;
            return;
        }

        int onVehicle = VehicleOnBoard();
        int delivered = SelectedTrip?.DeliveredTotal ?? 0;
        _tripLine.Text = $"🚚 {SelectedPlate ?? "Araç seçin"}" + (SelectedTrip is null ? string.Empty : $" · #{SelectedTrip.Id}");
        List<View> tiles =
        [
            Ui.StatTile("🛢️", onVehicle.ToString(), "Araçta tüp", Ui.Accent)
        ];
        if (onVehicle > 0 || delivered == 0)
        {
            tiles.Add(Ui.StatTile("🤝", delivered.ToString(), "Bu seferde teslim", Ui.Good));
        }
        else
        {
            _tripLine.Text += "\nTeslimler bitti — seferi bitirin; özet Tamamlanmış seferler'de kalır.";
        }

        _tripTiles.Content = Ui.TileGrid(tiles);
        _finishSimple.IsVisible = CanFinishTrip(onVehicle);
    }

    private int VehicleOnBoard() =>
        SelectedTrip?.Vehicles.FirstOrDefault(v => string.Equals(v.Plate, SelectedPlate, StringComparison.OrdinalIgnoreCase))?.OnVehicle ?? 0;

    private bool CanFinishTrip(int onVehicle)
    {
        if (SelectedTrip is null || onVehicle > 0)
        {
            return false;
        }

        return ApiClient.Instance.Can("sefer-kapat") || ApiClient.Instance.Session?.IsDriver == true;
    }

    private async Task RefreshGroupsAsync()
    {
        if (SelectedTrip is not ActiveTripSummary trip)
        {
            return;
        }

        string? plate = _groupPlate.SelectedIndex > 0 ? _groupPlate.SelectedItem as string : null;
        try
        {
            TripLoadView load = await ApiClient.Instance.TripLoadAsync(trip.Id, plate);
            _groupsTotal.Text = load.Total == 0
                ? string.Empty
                : $"{(plate ?? "Tüm araçlar")}: {load.Total} tüp yüklendi · araçta {load.OnVehicle}" + (load.Missing > 0 ? $" · ⚠ {load.Missing} eksik" : string.Empty);
            _groupsTotal.TextColor = load.Missing > 0 ? Colors.Red : Ui.Muted;
            _groups.Content = CylinderGroupPage.GroupList(Navigation, load.Groups,
                plate is null ? "Bu sefere henüz tüp yüklenmedi. \"Araca yükle\" adımında tüpleri okutup kaydedin." : $"{plate} aracına henüz tüp yüklenmedi.",
                plate ?? trip.Title);
        }
        catch (Exception exception)
        {
            _groupsTotal.Text = "Yük listesi alınamadı: " + exception.Message;
            _groupsTotal.TextColor = Colors.Red;
        }
    }

    // Ana menüden "Sevkiyat başlat" açık sefer yokken bunu çağırır.
    public async Task StartTripAsync()
    {
        (TripStartResponse Trip, string? FirstPlate)? started = await TripStartPage.OpenAsync(Navigation);
        if (started is null)
        {
            return;
        }

        (TripStartResponse trip, string? firstPlate) = started.Value;

        SelectMode(LoadMode);
        _undo.Clear();
        _scanned.Clear();
        RefreshScanned();
        await RefreshTripsAsync(trip.Id);
        if (firstPlate is not null && _vehicle.ItemsSource is List<string> plates && plates.IndexOf(firstPlate) is int index and >= 0)
        {
            _vehicle.SelectedIndex = index;
        }

        await DisplayAlertAsync("Sefer", trip.Message + "\n\nŞimdi \"1. Araca yükle\"ye dokunun veya tüpleri okutup Kaydet'e basın.", "Tamam");
    }

    private async Task AddVehicleAsync()
    {
        if (SelectedTrip is null)
        {
            await DisplayAlertAsync("Sefer", "Önce aktif bir sefer seçin.", "Tamam");
            return;
        }

        _busy.IsVisible = true;
        try
        {
            HashSet<string> onTrip = SelectedTrip.Vehicles.Select(v => v.Plate).ToHashSet(StringComparer.OrdinalIgnoreCase);
            List<VehicleItem> vehicles = (await ApiClient.Instance.VehiclesAsync(includeInactive: false))
                .Where(v => !onTrip.Contains(v.Plate) && v.ActiveTripId is null)
                .ToList();
            _busy.IsVisible = false;
            if (vehicles.Count == 0)
            {
                await DisplayAlertAsync("Sefer", "Eklenebilecek boşta araç yok. Yeni araç için ana menüdeki \"Araç İşlemleri\"ni kullanın.", "Tamam");
                return;
            }

            string? choice = await DisplayActionSheetAsync("Sefere eklenecek araç", "Vazgeç", null,
                vehicles.Select(v => v.Plate + (v.Caption.Length == 0 ? string.Empty : " · " + v.Caption)).ToArray());
            VehicleItem? picked = vehicles.FirstOrDefault(v => choice is not null && choice.StartsWith(v.Plate, StringComparison.Ordinal));
            if (picked is null)
            {
                return;
            }

            _busy.IsVisible = true;
            string message = await ApiClient.Instance.AddTripVehicleAsync(SelectedTrip.Id, picked.Plate);
            await DisplayAlertAsync("Sefer", message, "Tamam");
            await RefreshTripsAsync(SelectedTrip.Id);
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

        int tripId = SelectedTrip.Id;
        string? plate = SelectedPlate;
        int mode = SelectedMode;
        if (mode is LoadMode or PickupMode or ReturnMode && string.IsNullOrWhiteSpace(plate))
        {
            await DisplayAlertAsync("Sefer", "Yükleme, boş alma ve dönüş için araç seçin.", "Tamam");
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
                case LoadMode:
                case PickupMode:
                    TripLoadResponse load = mode == LoadMode
                        ? await ApiClient.Instance.LoadTripCylindersAsync(SelectedTrip.Id, plate!, _scanned)
                        : await ApiClient.Instance.PickupTripEmptiesAsync(SelectedTrip.Id, plate!, _scanned);
                    message = load.Message + (load.Messages.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, load.Messages) : string.Empty);
                    if (load.Refused is { Count: > 0 } refused)
                    {
                        _busy.IsVisible = false;
                        await DisplayAlertAsync("Bu tüpler yüklenemedi",
                            $"Sefere yalnızca DOLU tüp yüklenebilir. Şu {refused.Count} tüp reddedildi; araca koymayın:\n\n"
                            + string.Join(Environment.NewLine, load.Messages.Where(m => refused.Any(r => m.StartsWith(r + ":", StringComparison.OrdinalIgnoreCase)))),
                            "Tamam");
                    }

                    break;
                case DeliverMode:
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
                case ReturnMode:
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

            _undo.Clear();
            _scanned.Clear();
            RefreshScanned();
            await RefreshTripsAsync(tripId);
            await RefreshRouteAsync();
            _busy.IsVisible = false;
            await NextStepAsync(mode, message, reconcile);
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

    private async Task ExplainMissingAsync()
    {
        if (SelectedTrip is not ActiveTripSummary trip)
        {
            await DisplayAlertAsync("Sefer", "Aktif sefer seçin.", "Tamam");
            return;
        }

        _busy.IsVisible = true;
        try
        {
            List<TripOpenCylinder> open = await ApiClient.Instance.TripOpenCylindersAsync(trip.Id);
            List<TripOpenCylinder> missing = open.Where(c => c.Missing || c.Reason is not null).ToList();
            List<TripMissingReasonItem> reasons = await ApiClient.Instance.TripMissingReasonsAsync();
            _busy.IsVisible = false;
            if (missing.Count == 0)
            {
                await DisplayAlertAsync("Sefer", open.Count == 0
                    ? "Okutulmayan tüp yok; tüm tüpler teslim veya depo dönüşü olarak okutulmuş."
                    : $"{open.Count} tüp hâlâ araçta görünüyor. Önce \"Aracı boşalt\" ile dönen tüpleri okutun; okutulmayanlar sonra burada listelenir.", "Tamam");
                return;
            }

            List<TripOpenCylinder> unexplained = missing.Where(c => c.Reason is null).ToList();
            List<string> choices = [];
            if (unexplained.Count > 1)
            {
                choices.Add($"Gerekçesiz tümü ({unexplained.Count} tüp)");
            }

            choices.AddRange(missing.Select(CylinderChoice));
            string? choice = await DisplayActionSheetAsync("Hangi tüp(ler)?", "Vazgeç", null, choices.ToArray());
            List<TripOpenCylinder> picked = choice is null ? []
                : choice.StartsWith("Gerekçesiz tümü", StringComparison.Ordinal) ? unexplained
                : missing.Where(c => CylinderChoice(c) == choice).Take(1).ToList();
            if (picked.Count == 0)
            {
                return;
            }

            string? message = await ExplainAsync(trip.Id, picked, reasons, picked.Count == 1 ? CylinderChoice(picked[0]) : $"{picked.Count} tüp");
            if (message is not null)
            {
                await DisplayAlertAsync("Sefer", message, "Tamam");
            }
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

    private async Task<string?> ExplainAsync(int tripId, IReadOnlyList<TripOpenCylinder> cylinders, IReadOnlyList<TripMissingReasonItem> reasons, string title)
    {
        string? reasonChoice = await DisplayActionSheetAsync($"Nerede / neden okutulmadı?\n{title}", "Vazgeç", null, reasons.Select(r => r.Label).ToArray());
        TripMissingReasonItem? reason = reasons.FirstOrDefault(r => r.Label == reasonChoice);
        if (reason is null)
        {
            return null;
        }

        int? customerId = null;
        if (reason.AsksCustomer)
        {
            CustomerItem? customer = await PickCustomerAsync();
            customerId = customer?.Id;
        }

        bool noteRequired = reason.NoteRequired && customerId is null;
        string? note = await DisplayPromptAsync("Açıklama", noteRequired
            ? reason.AsksCustomer ? "Müşteri seçilmedi; müşteri adını ve ayrıntıyı yazın (zorunlu)." : "Bu gerekçe için açıklama zorunlu (araç, yer veya ayrıntı)."
            : "İsteğe bağlı açıklama", "Kaydet", "Vazgeç", "Açıklama", 500);
        if (note is null)
        {
            return null;
        }

        _busy.IsVisible = true;
        try
        {
            return await ApiClient.Instance.ExplainTripMissingAsync(tripId, cylinders.Select(c => c.CylinderId).ToList(), reason.Key, note, customerId);
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task<CustomerItem?> PickCustomerAsync()
    {
        string? query = await DisplayPromptAsync("Müşteri", "Tüpün teslim edildiği / kaldığı müşterinin adından en az 2 harf yazın. Bilmiyorsanız boş bırakıp açıklamaya yazın.",
            "Ara", "Atla", "Müşteri adı", 60);
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2)
        {
            return null;
        }

        _busy.IsVisible = true;
        List<CustomerItem> found;
        try
        {
            found = await ApiClient.Instance.CustomersAsync(query.Trim());
        }
        finally
        {
            _busy.IsVisible = false;
        }

        if (found.Count == 0)
        {
            await DisplayAlertAsync("Müşteri", "Müşteri bulunamadı; müşteri adını açıklamaya yazın.", "Tamam");
            return null;
        }

        List<CustomerItem> shown = found.Take(25).ToList();
        string? choice = await DisplayActionSheetAsync("Müşteri seçin", "Listede yok", null, shown.Select(c => $"{c.Name} ({c.Code})").ToArray());
        return shown.FirstOrDefault(c => $"{c.Name} ({c.Code})" == choice);
    }

    private static string CylinderChoice(TripOpenCylinder c) =>
        $"{c.SerialNumber} · {c.GasType} · {c.Plate} · {(c.IsPickup ? "boş alındı" : "dolu")} · "
        + (c.Reason is null ? "gerekçe yok" : c.ReasonLabel + (c.Checked ? " ✔" : string.Empty));

    private async Task CloseTripAsync(bool ask = true)
    {
        if (SelectedTrip is not ActiveTripSummary trip)
        {
            return;
        }

        if (ask && !await DisplayAlertAsync("Seferi kapat",
                $"{trip.Title} kapatılsın mı? Önce dönen tüm tüpleri \"Aracı boşalt\" ile okuttuğunuzdan emin olun. Okutulmayan her tüp için gerekçe seçmeniz istenecek.",
                "Devam", "İptal"))
        {
            return;
        }

        _busy.IsVisible = true;
        try
        {
            if (trip.RequireReasons && !await ExplainBeforeCloseAsync(trip))
            {
                return;
            }

            _busy.IsVisible = true;
            TripCloseResponse closed = await ApiClient.Instance.CloseTripAsync(trip.Id);
            ShowResult(closed.Message, closed.Reconcile);
            if (closed.Reconcile.HasAlerts)
            {
                await DisplayAlertAsync("Sefer kapatıldı — uyarı", closed.Message, "Tamam");
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

    private async Task<bool> ExplainBeforeCloseAsync(ActiveTripSummary trip)
    {
        List<TripOpenCylinder> unexplained = (await ApiClient.Instance.TripOpenCylindersAsync(trip.Id)).Where(c => c.Reason is null).ToList();
        if (unexplained.Count == 0)
        {
            return true;
        }

        List<TripMissingReasonItem> reasons = await ApiClient.Instance.TripMissingReasonsAsync();
        _busy.IsVisible = false;
        string? how = await DisplayActionSheetAsync(
            $"{unexplained.Count} tüp okutulmadı (ne teslim edildi ne depoya geri okutuldu). Kapatmak için her birinin nerede / neden okutulmadığını seçin.",
            "Vazgeç", null,
            unexplained.Count > 1 ? ["Tek tek seç", $"Hepsine aynı gerekçe ({unexplained.Count} tüp)"] : ["Gerekçe seç"]);
        if (how is null || how == "Vazgeç")
        {
            return false;
        }

        if (how.StartsWith("Hepsine", StringComparison.Ordinal))
        {
            return await ExplainAsync(trip.Id, unexplained, reasons, $"{unexplained.Count} tüp: " + string.Join(", ", unexplained.Select(c => c.SerialNumber))) is not null;
        }

        int done = 0;
        foreach (TripOpenCylinder cylinder in unexplained)
        {
            if (await ExplainAsync(trip.Id, [cylinder], reasons, $"{++done}/{unexplained.Count} · {CylinderChoice(cylinder)}") is null)
            {
                await DisplayAlertAsync("Sefer", $"Sefer kapatılmadı. {done - 1} tüpün gerekçesi kaydedildi; kalanlar için tekrar \"Seferi bitir\"e basın.", "Tamam");
                return false;
            }
        }

        return true;
    }

    // Route of the selected vehicle: one card per customer in visiting order with map, call and scan shortcuts.
    private async Task RefreshRouteAsync()
    {
        if (!_routes)
        {
            return;
        }

        if (SelectedTrip is not ActiveTripSummary trip || SelectedPlate is not string plate)
        {
            _routeInfo.Text = "Rota için sefer ve araç seçin.";
            _route.Children.Clear();
            _stops = [];
            return;
        }

        try
        {
            List<TripStopItem> stops = await ApiClient.Instance.TripRouteAsync(trip.Id, plate);
            _stops = stops;
            _route.Children.Clear();
            int done = stops.Count(s => s.Done);
            _routeInfo.Text = stops.Count == 0
                ? $"{plate} için durak yok. \"Müşteri ekle\" veya \"Önerilenler\" ile gideceğiniz müşterileri sırayla ekleyin; yol tarifi tek dokunuşla açılır."
                : $"{plate}: {stops.Count} durak · {done} tamamlandı · sıradaki: {stops.FirstOrDefault(s => !s.Done)?.CustomerName ?? "yok"}";
            int index = 0;
            foreach (TripStopItem stop in stops)
            {
                _route.Children.Add(StopCard(trip.Id, stop, ++index, stops.Count));
            }
        }
        catch (Exception exception)
        {
            _routeInfo.Text = "Rota alınamadı: " + exception.Message;
        }
    }

    private async Task SaveLocationAsync(TripStopItem stop, bool ask = true)
    {
        if (ask && !await DisplayAlertAsync("Konumu kaydet",
                $"Şu anki konumunuz {stop.CustomerName} müşterisinin konumu olarak kaydedilsin mi? Müşterinin kapısında / deposunda olduğunuzdan emin olun.",
                "Kaydet", "Vazgeç"))
        {
            return;
        }

        _busy.IsVisible = true;
        try
        {
            if (await Permissions.RequestAsync<Permissions.LocationWhenInUse>() != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("Konum", "Konum izni verilmedi. Telefon ayarlarından Arsan Gaz uygulamasına konum izni verin.", "Tamam");
                return;
            }

            Location? here = await Geolocation.Default.GetLocationAsync(new GeolocationRequest(GeolocationAccuracy.Best, TimeSpan.FromSeconds(20)));
            if (here is null)
            {
                await DisplayAlertAsync("Konum", "Konum alınamadı. Telefonun konum (GPS) ayarının açık olduğundan emin olup açık alanda tekrar deneyin.", "Tamam");
                return;
            }

            string message = await ApiClient.Instance.SaveCustomerLocationAsync(stop.CustomerId, here.Latitude, here.Longitude, here.Accuracy);
            await DisplayAlertAsync("Konum", message + (here.Accuracy is double accuracy ? $" (hassasiyet ±{accuracy:0} m)" : string.Empty), "Tamam");
            await RefreshRouteAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Konum", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task ConfirmLocationAsync(TripStopItem stop, bool force = false)
    {
        if (!_saveLocation || stop.LocationVerified != false || stop.Latitude is null || (!force && !_verifyAsked.Add(stop.CustomerId)))
        {
            return;
        }

        const string Correct = "✓ Evet, konum doğru";
        const string Here = "📍 Buradayım, konumu buraya taşı";
        const string Map = "🗺️ Haritada göster";
        string? choice = await DisplayActionSheetAsync(
            $"{stop.CustomerName}: haritadaki konum doğru mu? (Google'dan bulundu, henüz teyit edilmedi)", "Sonra", null, Correct, Here, Map);
        try
        {
            switch (choice)
            {
                case Correct:
                    string message = await ApiClient.Instance.VerifyCustomerLocationAsync(stop.CustomerId);
                    ShowInfo($"{stop.CustomerName}: {message}");
                    await RefreshRouteAsync();
                    break;
                case Here:
                    await SaveLocationAsync(stop, false);
                    break;
                case Map when stop.MapUrl is string url:
                    _verifyAsked.Remove(stop.CustomerId);
                    await OpenAsync(url);
                    break;
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Konum", exception.Message, "Tamam");
        }
    }

    private View StopCard(int tripId, TripStopItem stop, int index, int count)
    {
        string place = string.Join(", ", new[] { stop.Address, stop.District, stop.City }.Where(x => !string.IsNullOrWhiteSpace(x)));
        Label title = new()
        {
            Text = $"{index}. {stop.CustomerName}" + (stop.Done ? "  ✔" : string.Empty),
            FontSize = 15,
            FontFamily = "OpenSansSemibold",
            TextColor = stop.Done ? Colors.Green : null
        };
        Label detail = Ui.Note((place.Length == 0 ? "Adres kayıtlı değil" : place)
            + (stop.Delivered + stop.Collected > 0 ? $"\nBu seferde verilen {stop.Delivered}, alınan {stop.Collected}" : string.Empty)
            + (string.IsNullOrEmpty(stop.Note) ? string.Empty : "\nNot: " + stop.Note)
            + (stop.Latitude is null
                ? (stop.AlertMeters is null ? string.Empty : "\n📍 Konum kayıtlı değil")
                : stop.LocationVerified == false ? "\n📍 Konum Google'dan bulundu, doğrulanmamış"
                : "\n📍 Konum kayıtlı"));

        List<View> actions = [];
        if (stop.MapUrl is string url)
        {
            actions.Add(Ui.Compact("🧭 Yol tarifi", async (_, _) => await OpenAsync(url)));
        }

        if (_saveLocation)
        {
            actions.Add(stop.LocationVerified == false && stop.Latitude is not null
                ? Ui.Compact("📍 Konumu teyit et", async (_, _) => await ConfirmLocationAsync(stop, true))
                : Ui.Compact(stop.Latitude is null ? "📍 Konumu kaydet" : "📍 Konumu güncelle", async (_, _) => await SaveLocationAsync(stop)));
        }

        if (!string.IsNullOrWhiteSpace(stop.Phone))
        {
            actions.Add(Ui.Compact("📞 Ara", async (_, _) => await Ui.OpenPhoneAsync(this, stop.Phone)));
        }

        actions.Add(Ui.Compact("📷 Burada", (_, _) => UseStop(stop)));
        VerticalStackLayout body = new() { Spacing = 6, Children = { title, detail, Ui.ActionRow([.. actions]) } };
        body.Children.Add(Ui.ActionRow(
            Ui.Compact("▲", async (_, _) => await RouteActionAsync(() => ApiClient.Instance.MoveTripStopAsync(tripId, stop.Id, -1))).ShowIf(index > 1),
            Ui.Compact("▼", async (_, _) => await RouteActionAsync(() => ApiClient.Instance.MoveTripStopAsync(tripId, stop.Id, 1))).ShowIf(index < count),
            Ui.Compact(stop.Done ? "Yeniden aç" : "Tamamlandı", async (_, _) => await RouteActionAsync(() => ApiClient.Instance.SetTripStopDoneAsync(tripId, stop.Id, !stop.Done))),
            Ui.Compact("Çıkar", async (_, _) =>
            {
                if (await DisplayAlertAsync("Rota", $"{stop.CustomerName} rotadan çıkarılsın mı?", "Çıkar", "Vazgeç"))
                {
                    await RouteActionAsync(() => ApiClient.Instance.RemoveTripStopAsync(tripId, stop.Id));
                }
            })));
        return Ui.Card(body);
    }

    private void UseStop(TripStopItem stop)
    {
        PushCustomerUndo();
        _selectedCustomer = new CustomerItem(stop.CustomerId, string.Empty, stop.CustomerName, stop.City, stop.Phone, true, 0);
        ShowCustomer();
        int deliver = Array.FindIndex(_modes, m => m.Kind == DeliverMode);
        if (deliver >= 0 && SelectedMode is not (DeliverMode or PickupMode))
        {
            _mode.SelectedIndex = deliver;
        }

        ClearScanned(trackUndo: false);
        ShowInfo(HasMode(PickupMode)
            ? $"{stop.CustomerName} seçildi. Verdiğiniz dolu tüpleri \"Müşteriye teslim\", aldığınız boşları \"Müşteriden boş alma\" ile okutun."
            : $"{stop.CustomerName} seçildi. Teslim ettiğiniz tüpleri okutun.");
        _ = ConfirmLocationAsync(stop);
    }

    private void ShowInfo(string text)
    {
        _result.Text = "ℹ️ " + text;
        _result.TextColor = Ui.Accent;
        _resultCard.IsVisible = true;
    }

    private async Task OpenAsync(string uri)
    {
        try
        {
            await Launcher.Default.OpenAsync(new Uri(uri));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Rota", "Açılamadı: " + exception.Message, "Tamam");
        }
    }

    private async Task RouteActionAsync(Func<Task<string>> action)
    {
        _busy.IsVisible = true;
        try
        {
            await action();
            await RefreshRouteAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Rota", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task AddStopAsync()
    {
        if (SelectedTrip is not ActiveTripSummary trip || SelectedPlate is not string plate)
        {
            await DisplayAlertAsync("Rota", "Önce sefer ve araç seçin.", "Tamam");
            return;
        }

        CustomerItem? customer = await CustomerPickPage.OpenAsync(Navigation, "Rotaya müşteri ekle");
        if (customer is not null)
        {
            await AddStopAsync(trip.Id, plate, customer.Id);
        }
    }

    private async Task AddStopAsync(int tripId, string plate, int customerId)
    {
        _busy.IsVisible = true;
        try
        {
            string message = await ApiClient.Instance.AddTripStopAsync(tripId, plate, customerId);
            _routeInfo.Text = message;
            await RefreshRouteAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Rota", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task SuggestStopsAsync()
    {
        if (SelectedTrip is not ActiveTripSummary trip || SelectedPlate is not string plate)
        {
            await DisplayAlertAsync("Rota", "Önce sefer ve araç seçin.", "Tamam");
            return;
        }

        List<TripStopSuggestionItem> suggestions;
        try
        {
            _busy.IsVisible = true;
            suggestions = await ApiClient.Instance.TripStopSuggestionsAsync(trip.Id, plate);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Rota", exception.Message, "Tamam");
            return;
        }
        finally
        {
            _busy.IsVisible = false;
        }

        if (suggestions.Count == 0)
        {
            await DisplayAlertAsync("Rota", "Şu an sipariş zamanı gelen (veya geciken) ve rotada olmayan müşteri görünmüyor.", "Tamam");
            return;
        }

        List<string> items = suggestions.Select(s => $"{s.CustomerName}" + (string.IsNullOrEmpty(s.City) ? string.Empty : $" ({s.City})") + $" · {s.Reason}").ToList();
        string? picked = await DisplayActionSheetAsync("Önerilen müşteriler", "Kapat", null, items.ToArray());
        int index = picked is null ? -1 : items.IndexOf(picked);
        if (index >= 0)
        {
            await AddStopAsync(trip.Id, plate, suggestions[index].CustomerId);
        }
    }
}
