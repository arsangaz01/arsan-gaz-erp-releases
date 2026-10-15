using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Sefer bazlı araç ve tüp takibi: yükleme, teslim, dönüş okutması ve tutarlılık kontrolü.
public sealed class CylinderTripPage : ContentPage
{
    private const int LoadMode = 0;
    private const int DeliverMode = 1;
    private const int ReturnMode = 2;
    private const int PickupMode = 4;

    // Pickup and the route are optional features: when they are off the page looks and works as before 1.4.0.
    private readonly bool _pickup = ApiClient.Instance.Can(Perm.TripPickup);
    private readonly bool _routes = ApiClient.Instance.Can(Perm.TripRoute);
    private readonly bool _saveLocation = ApiClient.Instance.Can(Perm.CustomerLocation);
    private readonly (string Label, int Kind, string Permission)[] _modes = Modes();

    // Approach warning (trip plan feature): only shown when the server sends warning distances with the route.
    private readonly Switch _proximitySwitch = new() { IsToggled = ProximityAlerts.Enabled, VerticalOptions = LayoutOptions.Center };
    private readonly Label _proximityInfo = Ui.Note(string.Empty);
    private readonly VerticalStackLayout _proximityBox = new() { Spacing = 4, IsVisible = false };
    private List<TripStopItem> _stops = [];
    private bool _locationAsked;
    private readonly TodayPlanView _todayPlan = new();
    private readonly Border _todaySection;
    // Customers whose found location was already asked about on this page; asked again on the next visit.
    private readonly HashSet<int> _verifyAsked = [];

    private static (string Label, int Kind, string Permission)[] Modes()
    {
        (string Label, int Kind, string Permission)[] all =
        [
            ("Yükleme", LoadMode, Perm.TripLoad),
            (ApiClient.Instance.Can(Perm.TripPickup) ? "Müşteriye teslim (dolu)" : "Müşteriye teslim", DeliverMode, Perm.TripDeliver),
            ("Müşteriden boş alım", PickupMode, Perm.TripPickup),
            ("Aracı boşalt (depo)", ReturnMode, Perm.TripReturn),
            ("Kontrol", 3, Perm.TripView)
        ];
        return all.Where(m => ApiClient.Instance.Can(m.Permission)).ToArray();
    }

    private readonly Label _overview = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Picker _trip = new() { Title = "Aktif sefer" };
    private readonly Picker _vehicle = new() { Title = "Sefer aracı" };
    // Holds the chosen step; the numbered step buttons below replace it on screen.
    private readonly Picker _mode = new() { Title = "İşlem", IsVisible = false };
    private readonly List<Button> _stepButtons = [];
    private readonly Label _stepHint = new() { FontSize = 14, FontFamily = "OpenSansSemibold", TextColor = Ui.AccentDark, LineBreakMode = LineBreakMode.WordWrap };
    private bool _firstTrip = true;
    private readonly Picker _groupPlate = new() { Title = "Araç filtresi" };
    private readonly ContentView _groups = new();
    private readonly VerticalStackLayout _route = new() { Spacing = 10 };
    private readonly Label _routeInfo = Ui.Note(string.Empty);
    private readonly Label _groupsTotal = Ui.Note(string.Empty);
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
        // Handheld scanners (e.g. Honeywell EDA50 keyboard wedge) type the code and press Enter.
        _code.Completed += (_, _) =>
        {
            AddManual();
            _code.Focus();
        };
        foreach (var mode in _modes)
        {
            _mode.Items.Add(mode.Label);
        }

        for (int i = 0; i < _modes.Length; i++)
        {
            int index = i;
            string label = _modes[i].Kind == 3 ? _modes[i].Label : $"{i + 1}. {_modes[i].Label}";
            _stepButtons.Add(Ui.Secondary(label, (_, _) =>
            {
                _mode.SelectedIndex = index;
                ApplyMode();
            }));
        }

        VerticalStackLayout steps = new() { Spacing = 8 };
        for (int i = 0; i < _stepButtons.Count; i += 2)
        {
            steps.Children.Add(Ui.ActionRow([.. _stepButtons.Skip(i).Take(2)]));
        }

        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => ApplyMode();
        _trip.SelectedIndexChanged += (_, _) => RefreshVehicles();
        _vehicle.SelectedIndexChanged += async (_, _) => await RefreshRouteAsync();
        _groupPlate.SelectedIndexChanged += async (_, _) => await RefreshGroupsAsync();
        Ui.LiveSearch(_customerSearch, SearchCustomersAsync);

        _proximityBox.Children.Add(new HorizontalStackLayout
        {
            Spacing = 8,
            Children = { new Label { Text = "📳 Yaklaşma uyarısı", FontSize = 15, VerticalOptions = LayoutOptions.Center }, _proximitySwitch }
        });
        _proximityBox.Children.Add(_proximityInfo);
        _proximitySwitch.Toggled += async (_, e) =>
        {
            ProximityAlerts.Enabled = e.Value;
            if (!e.Value)
            {
                ProximityAlerts.Stop();
            }

            _locationAsked = false;
            await UpdateProximityAsync();
        };

        _resultCard = Ui.Card(_result);
        _resultCard.IsVisible = false;
        _customerSearchButton = Ui.Secondary("Müşteri ara", async (_, _) => await SearchCustomersAsync());
        _customerSearchButton.IsVisible = false;
        _todaySection = Ui.Section("🗓️ Bugünkü sefer planım", _todayPlan);
        _todaySection.IsVisible = false;

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    _todaySection,
                    Ui.Note((_pickup
                        ? "\"Sefer Başlat\" ile kayıtlı araçlardan seçip seferi açın, tüpleri okutup araca yükleyin; yük gaz türüne göre listelenir, bir türe dokununca barkodlar ve ayrıntılar açılır. Müşteride verdiğiniz dolu tüpleri \"Müşteriye teslim (dolu)\", aldığınız boşları \"Müşteriden boş alım\" ile okutun; her kayıttan sonra o müşteriye verilen/alınan sayısı ve müşterideki güncel tüp bakiyesi gösterilir."
                        : "\"Sefer Başlat\" ile kayıtlı araçlardan seçip seferi açın, tüpleri okutup araca yükleyin; yük gaz türüne göre listelenir, bir türe dokununca barkodlar ve ayrıntılar açılır. Müşteriye teslimi de buradan okutun.")
                        + (ApiClient.Instance.Can(Perm.TripReturn)
                            ? " Depoya dönen aracın dolu ve boş tüplerini \"Aracı boşalt (depo)\" ile okutun; eşleşmeyen veya okutulmayan tüpler kırmızı uyarı verir. Sefer kapatılırken okutulmayan her tüp için gerekçe seçilir; gerekçeler yöneticiye bildirilir (müşteriye mesaj gitmez)."
                            : " Teslimatlar bitince depoya dönün; aracı depoda operatör boşaltıp seferi kapatır.")),
                    Ui.Primary("🚚 Sefer Başlat", async (_, _) => await StartTripAsync()).ShowIf(ApiClient.Instance.Can(Perm.TripStart)),
                    Ui.Section(
                        "Seferdeki araçlar",
                        _overview,
                        Ui.ActionRow(
                            Ui.Secondary("Yenile", async (_, _) => await RefreshTripsAsync()),
                            Ui.Secondary("Sefere araç ekle", async (_, _) => await AddVehicleAsync()).ShowIf(ApiClient.Instance.Can(Perm.TripStart) && FixedPlate is null))),
                    Ui.Section(
                        "Sefer ve araç",
                        _trip,
                        _vehicle),
                    Ui.Section(
                        "Rota (müşteri sırası)",
                        _routeInfo,
                        _proximityBox,
                        _route,
                        Ui.ActionRow(
                            Ui.Secondary("Müşteri ekle", async (_, _) => await AddStopAsync()),
                            Ui.Secondary("Önerilenler", async (_, _) => await SuggestStopsAsync()))).ShowIf(_routes),
                    Ui.Section(
                        "Okutma",
                        _mode,
                        steps,
                        _stepHint,
                        _customerSearch,
                        _customerSearchButton,
                        _customer,
                        Ui.ActionRow(
                            Ui.Primary("📷 Okut", async (_, _) => await ScanAsync()),
                            Ui.Primary("📷 Toplu okut", async (_, _) => await ScanManyAsync())),
                        _code,
                        Ui.ActionRow(
                            Ui.Secondary("Kodu ekle", (_, _) => AddManual()),
                            Ui.Secondary("Listeyi temizle", (_, _) => { _scanned.Clear(); RefreshScanned(); })),
                        _scannedText,
                        Ui.Primary("Kaydet / kontrol et", async (_, _) => await SubmitAsync())),
                    _busy,
                    _resultCard,
                    Ui.Section(
                        "Yük listesi (gaz türüne göre)",
                        _groupPlate,
                        _groupsTotal,
                        _groups,
                        Ui.Secondary("Listeyi yenile", async (_, _) => await RefreshGroupsAsync())),
                    Ui.Secondary("Okutulmayan tüplere gerekçe yaz", async (_, _) => await ExplainMissingAsync(false))
                        .ShowIf(ApiClient.Instance.Can(Perm.TripReturn) || ApiClient.Instance.Can(Perm.TripClose)),
                    Ui.Secondary("Seferi kapat", async (_, _) => await CloseTripAsync()).ShowIf(ApiClient.Instance.Can(Perm.TripClose))
                }
            }
        };

        RefreshScanned();
        ApplyMode();
        _ = RefreshTripsAsync();
    }

    private async Task RefreshTodayPlanAsync()
    {
        if (_routes)
        {
            _todaySection.IsVisible = await _todayPlan.RefreshAsync();
        }
    }

    // The signed-in driver's fixed vehicle: trips start with it directly and it is preselected for loading and return.
    private static string? FixedPlate => ApiClient.Instance.Account?.FixedPlate is { Length: > 0 } plate ? plate : null;

    private ActiveTripSummary? SelectedTrip => _trip.SelectedIndex >= 0 && _trip.SelectedIndex < _trips.Count ? _trips[_trip.SelectedIndex] : null;

    private string? SelectedPlate => _vehicle.SelectedItem as string;

    private CustomerItem? SelectedCustomer => _customer.SelectedIndex >= 0 && _customer.SelectedIndex < _customers.Count ? _customers[_customer.SelectedIndex] : null;

    private int SelectedMode => _mode.SelectedIndex >= 0 && _mode.SelectedIndex < _modes.Length ? _modes[_mode.SelectedIndex].Kind : 3;

    private void ApplyMode()
    {
        bool deliver = SelectedMode is DeliverMode or PickupMode;
        _customerSearch.IsVisible = deliver;
        _customerSearchButton.IsVisible = deliver;
        _customer.IsVisible = deliver;
        for (int i = 0; i < _stepButtons.Count; i++)
        {
            Ui.Highlight(_stepButtons[i], i == _mode.SelectedIndex);
        }

        _stepHint.Text = SelectedMode switch
        {
            LoadMode => "Araca yüklediğiniz tüpleri okutun, sonra \"Kaydet\".",
            DeliverMode => "Müşteriyi seçin, verdiğiniz dolu tüpleri okutun, \"Kaydet\". Her müşteri için tekrarlayın.",
            PickupMode => "Müşteriyi seçin, aldığınız boş tüpleri okutun, \"Kaydet\".",
            ReturnMode => "Depoda araçtan inen bütün tüpleri (kalan dolular ve müşterilerden alınan boşlar) okutun, \"Kaydet\". Ardından sefer kapatılır.",
            _ => "Araçtaki tüpleri okutup yük listesiyle karşılaştırın."
        };
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

    private string Step(int kind)
    {
        int index = Array.FindIndex(_modes, m => m.Kind == kind);
        return index < 0 ? string.Empty : $"\"{index + 1}. {_modes[index].Label}\"";
    }

    // Leads on after a saved scan: loading → delivery, delivery → empties of the same customer or the next customer,
    // unloading the vehicle → closing the trip.
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
                }
                else
                {
                    NextCustomer();
                }

                break;
            case DeliverMode:
                await DisplayAlertAsync("Teslim kaydedildi", message, "Tamam");
                NextCustomer();
                break;
            case PickupMode:
                await DisplayAlertAsync("Boş alım kaydedildi", message, "Tamam");
                NextCustomer();
                break;
            case ReturnMode when ApiClient.Instance.Can(Perm.TripClose):
                if (await DisplayAlertAsync(reconcile?.HasAlerts == true ? "Araç boşaltıldı — uyarı" : "Araç boşaltıldı", message + "\n\nSefer şimdi kapatılsın mı?", "Seferi kapat", "Daha sonra"))
                {
                    await CloseTripAsync(ask: false);
                }

                break;
        }
    }

    private void NextCustomer()
    {
        _customerSearch.Text = string.Empty;
        _customers = [];
        _customer.ItemsSource = null;
        _customer.SelectedIndex = -1;
        SelectMode(DeliverMode);
        _stepHint.Text = "Sıradaki müşteriyi seçin ve teslim ettiğiniz tüpleri okutun. Teslimatlar bitince "
            + (HasMode(ReturnMode) ? $"depoda {Step(ReturnMode)}." : "depoya dönün; aracı depoda operatör boşaltıp seferi kapatır.");
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
                _overview.Text = "Aktif sefer yok. \"Sefer Başlat\" ile başlatın.";
                _trip.SelectedIndex = -1;
                _vehicle.ItemsSource = null;
                _groupPlate.ItemsSource = null;
                _groupsTotal.Text = string.Empty;
                _groups.Content = Ui.Note("Aktif sefer yok.");
                return;
            }

            int wanted = selectTripId is int id ? _trips.FindIndex(t => t.Id == id) : -1;
            if (wanted < 0 && _trip.SelectedIndex < 0 && FixedPlate is string own)
            {
                wanted = _trips.FindIndex(t => t.Vehicles.Any(v => string.Equals(v.Plate, own, StringComparison.OrdinalIgnoreCase)));
            }

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
            if (_firstTrip)
            {
                _firstTrip = false;
                if (SelectedTrip?.Vehicles.FirstOrDefault(v => string.Equals(v.Plate, SelectedPlate, StringComparison.OrdinalIgnoreCase)) is { OnTrip: true, LoadedCount: > 0 })
                {
                    SelectMode(HasMode(ReturnMode) ? ReturnMode : DeliverMode);
                }
            }
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

        string? previous = SelectedPlate ?? FixedPlate;
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
                plate is null ? "Bu sefere henüz tüp yüklenmedi. \"Yükleme\" seçiliyken tüpleri okutup kaydedin." : $"{plate} aracına henüz tüp yüklenmedi.",
                plate ?? trip.Title);
        }
        catch (Exception exception)
        {
            _groupsTotal.Text = "Yük listesi alınamadı: " + exception.Message;
            _groupsTotal.TextColor = Colors.Red;
        }
    }

    // A driver with a fixed vehicle skips the vehicle choice: the trip starts with that vehicle after one confirmation.
    public async Task StartTripAsync()
    {
        (TripStartResponse Trip, string? FirstPlate)? started;
        if (FixedPlate is string own)
        {
            if (!await DisplayAlertAsync("Sevkiyat başlat", $"{own} aracınızla yeni sefer başlatılsın mı?", "Başlat", "Vazgeç"))
            {
                return;
            }

            try
            {
                _busy.IsVisible = true;
                started = (await ApiClient.Instance.StartTripAsync(null, [own]), own);
            }
            catch (Exception exception)
            {
                await DisplayAlertAsync("Sevkiyat", exception.Message, "Tamam");
                await RefreshTripsAsync();
                return;
            }
            finally
            {
                _busy.IsVisible = false;
            }
        }
        else
        {
            started = await TripStartPage.OpenAsync(Navigation);
        }

        if (started is null)
        {
            return;
        }

        (TripStartResponse trip, string? firstPlate) = started.Value;

        _mode.SelectedIndex = 0;
        _scanned.Clear();
        RefreshScanned();
        await RefreshTripsAsync(trip.Id);
        if (firstPlate is not null && _vehicle.ItemsSource is List<string> plates && plates.IndexOf(firstPlate) is int index and >= 0)
        {
            _vehicle.SelectedIndex = index;
        }

        await DisplayAlertAsync("Sefer", trip.Message + "\n\nŞimdi \"📷 Okut\" veya \"📷 Toplu okut\" ile araca yüklenen tüpleri okutup \"Kaydet\"e basın.", "Tamam");
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
                await DisplayAlertAsync("Sefer", "Eklenebilecek boşta araç yok. Yeni araç için \"Araç\" sekmesini kullanın.", "Tamam");
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

    private async Task SearchCustomersAsync(bool live = false)
    {
        string query = (_customerSearch.Text ?? string.Empty).Trim();
        if (query.Length < Ui.LiveSearchMinLength)
        {
            if (!live)
            {
                await DisplayAlertAsync("Müşteri", "En az 2 karakter yazın.", "Tamam");
            }

            return;
        }

        _busy.IsVisible = true;
        try
        {
            var found = await ApiClient.Instance.CustomersAsync(query);
            if (live && query != (_customerSearch.Text ?? string.Empty).Trim())
            {
                return;
            }

            _customers = found;
            _customer.ItemsSource = _customers.Select(c => $"{c.Name} ({c.Code})").ToList();
        }
        catch (Exception exception)
        {
            if (!live)
            {
                await DisplayAlertAsync("Müşteri", exception.Message, "Tamam");
            }
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
        if (mode is LoadMode or ReturnMode or PickupMode && string.IsNullOrWhiteSpace(plate))
        {
            await DisplayAlertAsync("Sefer", "Yükleme, boş alım ve dönüş için araç seçin.", "Tamam");
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
                    TripLoadResponse load = await ApiClient.Instance.LoadTripCylindersAsync(SelectedTrip.Id, plate!, _scanned);
                    message = load.Message + (load.Messages.Count > 0 ? Environment.NewLine + string.Join(Environment.NewLine, load.Messages) : string.Empty);
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
                case PickupMode:
                    if (SelectedCustomer is null || _scanned.Count == 0)
                    {
                        await DisplayAlertAsync("Sefer", "Boş alım için müşteri seçip en az bir tüp okutun.", "Tamam");
                        return;
                    }

                    message = await ApiClient.Instance.PickupTripCylindersAsync(SelectedTrip.Id, plate!, SelectedCustomer.Id, _scanned, null);
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

    private async Task CloseTripAsync(bool ask = true)
    {
        if (SelectedTrip is null)
        {
            return;
        }

        ActiveTripSummary trip = SelectedTrip;
        if (ask && HasMode(ReturnMode) && trip.Vehicles.FirstOrDefault(v => v.OnTrip) is TripVehicleSummary loaded)
        {
            const string unload = "Aracı boşalt";
            const string anyway = "Yine de kapat";
            string? choice = await DisplayActionSheetAsync(
                $"{loaded.Plate} henüz boşaltılmadı. Önce depoda {Step(ReturnMode)} ile araçtan inen tüpleri (kalan dolular ve boşlar) okutun, sonra seferi kapatın.",
                "Vazgeç", null, unload, anyway);
            if (choice == unload)
            {
                if (_vehicle.ItemsSource is List<string> plates && plates.FindIndex(p => string.Equals(p, loaded.Plate, StringComparison.OrdinalIgnoreCase)) is int index and >= 0)
                {
                    _vehicle.SelectedIndex = index;
                }

                SelectMode(ReturnMode);
                _code.Focus();
                return;
            }

            if (choice != anyway)
            {
                return;
            }
        }
        else if (ask && !await DisplayAlertAsync("Seferi kapat", $"{trip.Title} kapatılsın mı? Okutulmayan tüpler gerekçeleriyle yöneticiye bildirilir.", "Kapat", "İptal"))
        {
            return;
        }

        if (trip.RequireReasons && !await ExplainMissingAsync(true))
        {
            return;
        }

        _busy.IsVisible = true;
        try
        {
            TripCloseResponse closed = await ApiClient.Instance.CloseTripAsync(trip.Id);
            ProximityAlerts.Stop();
            ShowResult(closed.Message, closed.Reconcile);
            await DisplayAlertAsync(closed.Reconcile.HasAlerts ? "Sefer kapatıldı — uyarı" : "Sefer kapatıldı", closed.Message, "Tamam");
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
            _proximityBox.IsVisible = false;
            if (_trips.Count == 0)
            {
                ProximityAlerts.Stop();
            }

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

            await UpdateProximityAsync();
        }
        catch (Exception exception)
        {
            _routeInfo.Text = "Rota alınamadı: " + exception.Message;
        }
    }

    // Starts, updates or stops the approach warning for the undone located stops of the shown route.
    private async Task UpdateProximityAsync()
    {
        bool feature = _stops.Any(s => s.AlertMeters is not null);
        _proximityBox.IsVisible = feature;
        if (!feature || SelectedTrip is not ActiveTripSummary trip)
        {
            if (!feature && SelectedTrip is not null)
            {
                ProximityAlerts.Stop();
            }

            return;
        }

        List<ProximityTarget> targets = _stops
            .Where(s => !s.Done && s.Latitude is not null && s.Longitude is not null)
            .Select(s => new ProximityTarget(s.Id, s.CustomerId, s.CustomerName, s.Latitude!.Value, s.Longitude!.Value, s.AlertMeters ?? 300))
            .ToList();
        int unlocated = _stops.Count(s => !s.Done && s.Latitude is null);
        string missing = unlocated > 0 ? $" Konumu kayıtlı olmayan {unlocated} müşteride uyarı çıkmaz." : string.Empty;
        if (!ProximityAlerts.Enabled)
        {
            ProximityAlerts.Stop();
            _proximityInfo.Text = "Kapalı. Açarsanız sefer boyunca planlı müşteriye yaklaşınca telefon titreşir ve bildirim gösterir.";
            return;
        }

        if (targets.Count == 0)
        {
            ProximityAlerts.Stop();
            _proximityInfo.Text = unlocated > 0
                ? "Rotadaki müşterilerin konumu kayıtlı değil; uyarı çıkmaz. Müşterinin yanındayken \"📍 Konumu kaydet\" ile kaydedilebilir."
                : "Uğranmamış durak kalmadı.";
            return;
        }

        ProximityAlerts.SetTargets(trip.Id, targets);
        string? problem = ProximityAlerts.Running ? null : await ProximityAlerts.StartAsync(!_locationAsked);
        _locationAsked = true;
        int min = targets.Min(t => t.Meters);
        int max = targets.Max(t => t.Meters);
        _proximityInfo.Text = problem
            ?? $"Açık: {targets.Count} müşteriye {(min == max ? $"{min} m" : $"{min}–{max} m")} kalınca telefon titreşir ve bildirim gösterir. Konumunuz sunucuya gönderilmez.{missing}";
    }

    private void OnApproached(ProximityTarget target, double distance)
    {
        if (_stops.FirstOrDefault(s => s.Id == target.StopId) is TripStopItem stop)
        {
            UseStop(stop);
        }

        _result.Text = $"📍 {target.Name} müşterisine yaklaştınız (yaklaşık {Math.Round(distance / 10) * 10:0} m). Tüpleri teslim etmeyi unutmayın."
            + (_resultCard.IsVisible && !string.IsNullOrEmpty(_result.Text) ? Environment.NewLine + _result.Text : string.Empty);
        _result.TextColor = Ui.Accent;
        _resultCard.IsVisible = true;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        ProximityAlerts.Approached -= OnApproached;
        ProximityAlerts.Approached += OnApproached;
        _proximitySwitch.IsToggled = ProximityAlerts.Enabled;
        _verifyAsked.Clear();
        _ = RefreshTodayPlanAsync();
    }

    protected override void OnDisappearing()
    {
        ProximityAlerts.Approached -= OnApproached;
        base.OnDisappearing();
    }

    // The driver stands at the customer: the phone's own GPS fix becomes the customer's location.
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
                await DisplayAlertAsync("Konum", "Konum izni verilmedi. Telefon ayarlarından ArsanTüpTakip'e konum izni verin.", "Tamam");
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

    // A location found on Google is asked about the first time the driver is at the customer (approach warning or
    // "📷 Burada") until someone confirms or corrects it.
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
                    _result.Text = $"{stop.CustomerName}: {message}";
                    _result.TextColor = Ui.Muted;
                    _resultCard.IsVisible = true;
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
            + (stop.AlertMeters is null ? string.Empty
                : stop.Latitude is null ? "\n📍 Konum kayıtlı değil (yaklaşma uyarısı çıkmaz)"
                : stop.LocationVerified == false ? $"\n📍 Konum Google'dan bulundu, doğrulanmamış · {stop.AlertMeters} m kala uyarı"
                : $"\n📍 Konum kayıtlı · {stop.AlertMeters} m kala uyarı"));

        List<View> actions = [];
        if (stop.MapUrl is string url)
        {
            actions.Add(Ui.Compact("🧭 Yol tarifi", async (_, _) => await OpenAsync(url)));
        }

        if (_saveLocation && stop.AlertMeters is not null)
        {
            actions.Add(stop.LocationVerified == false
                ? Ui.Compact("📍 Konumu teyit et", async (_, _) => await ConfirmLocationAsync(stop, true))
                : Ui.Compact(stop.Latitude is null ? "📍 Konumu kaydet" : "📍 Konumu güncelle", async (_, _) => await SaveLocationAsync(stop)));
        }

        if (!string.IsNullOrWhiteSpace(stop.Phone))
        {
            actions.Add(Ui.Compact("📞 Ara", async (_, _) => await OpenAsync("tel:" + new string(stop.Phone.Where(c => char.IsDigit(c) || c == '+').ToArray()))));
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

    // The scan section switches to delivery for this customer; "Müşteriden boş alım" keeps the same customer.
    private void UseStop(TripStopItem stop)
    {
        _customers = [new CustomerItem(stop.CustomerId, string.Empty, stop.CustomerName, stop.City, null, true, 0)];
        _customer.ItemsSource = _customers.Select(c => c.Name).ToList();
        _customer.SelectedIndex = 0;
        int deliver = Array.FindIndex(_modes, m => m.Kind == DeliverMode);
        if (deliver >= 0 && SelectedMode is not (DeliverMode or PickupMode))
        {
            _mode.SelectedIndex = deliver;
        }

        _scanned.Clear();
        RefreshScanned();
        _result.Text = _pickup
            ? $"{stop.CustomerName} seçildi. Verdiğiniz dolu tüpleri \"Müşteriye teslim (dolu)\", aldığınız boşları \"Müşteriden boş alım\" ile okutun."
            : $"{stop.CustomerName} seçildi. Teslim ettiğiniz tüpleri okutun.";
        _result.TextColor = Ui.Muted;
        _resultCard.IsVisible = true;
        _ = ConfirmLocationAsync(stop);
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

        CustomerItem? customer = await PickCustomerAsync("Rotaya eklenecek müşteriyi arayın (en az 2 harf).", "Vazgeç");
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

    private sealed record CylinderChoice(string Text, IReadOnlyList<TripOpenCylinder> Cylinders);

    // Asks a reason for every cylinder that was loaded but neither delivered nor scanned back. With forClose it loops
    // until nothing is left unexplained and returns false if the user gives up (the server refuses the close anyway).
    private async Task<bool> ExplainMissingAsync(bool forClose)
    {
        if (SelectedTrip is not ActiveTripSummary trip)
        {
            await DisplayAlertAsync("Sefer", "Aktif sefer seçin.", "Tamam");
            return false;
        }

        List<TripMissingReasonItem> reasons;
        try
        {
            _busy.IsVisible = true;
            reasons = await ApiClient.Instance.TripMissingReasonsAsync();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Gerekçe", exception.Message, "Tamam");
            return false;
        }
        finally
        {
            _busy.IsVisible = false;
        }

        while (true)
        {
            List<TripOpenCylinder> open;
            try
            {
                _busy.IsVisible = true;
                open = await ApiClient.Instance.TripOpenCylindersAsync(trip.Id);
            }
            catch (Exception exception)
            {
                await DisplayAlertAsync("Gerekçe", exception.Message, "Tamam");
                return false;
            }
            finally
            {
                _busy.IsVisible = false;
            }

            List<TripOpenCylinder> unexplained = open.Where(c => string.IsNullOrEmpty(c.Reason)).ToList();
            if (forClose && unexplained.Count == 0)
            {
                return true;
            }

            if (open.Count == 0)
            {
                await DisplayAlertAsync("Gerekçe", "Bu seferde teslim veya dönüş okutması eksik tüp yok.", "Tamam");
                return true;
            }

            List<CylinderChoice> choices = [];
            if (unexplained.Count > 1)
            {
                choices.Add(new CylinderChoice($"Gerekçesiz {unexplained.Count} tüpün tümü", unexplained));
            }

            choices.AddRange(open.Select(c => new CylinderChoice(
                $"{c.SerialNumber} · {c.GasType} · {c.Plate} · " + (c.ReasonLabel ?? "gerekçe yok"), [c])));
            string title = forClose
                ? $"Kapatmadan önce {unexplained.Count} tüp için gerekçe seçin"
                : $"Okutulmayan tüpler ({unexplained.Count} gerekçesiz)";
            string? picked = await DisplayActionSheetAsync(title, forClose ? "Vazgeç (sefer kapanmaz)" : "Bitti", null, choices.Select(c => c.Text).ToArray());
            CylinderChoice? choice = choices.FirstOrDefault(c => c.Text == picked);
            if (choice is null)
            {
                return !forClose;
            }

            await ExplainAsync(trip.Id, choice.Cylinders, reasons);
        }
    }

    private async Task ExplainAsync(int tripId, IReadOnlyList<TripOpenCylinder> cylinders, List<TripMissingReasonItem> reasons)
    {
        string? label = await DisplayActionSheetAsync(
            cylinders.Count == 1 ? cylinders[0].SerialNumber + " neden okutulmadı?" : cylinders.Count + " tüp neden okutulmadı?",
            "Vazgeç", null, reasons.Select(r => r.Label).ToArray());
        TripMissingReasonItem? reason = reasons.FirstOrDefault(r => r.Label == label);
        if (reason is null)
        {
            return;
        }

        int? customerId = null;
        string? note = null;
        if (reason.AsksCustomer)
        {
            CustomerItem? customer = await PickCustomerAsync();
            customerId = customer?.Id;
        }

        if (customerId is null || !reason.AsksCustomer)
        {
            string prompt = reason.AsksCustomer
                ? "Müşteri seçilmedi. Müşteri adını veya ayrıntıyı yazın."
                : reason.NoteRequired ? "Açıklama yazın (araç, yer veya ayrıntı)." : "İsterseniz açıklama yazın (boş bırakılabilir).";
            note = await DisplayPromptAsync(reason.Label, prompt, "Kaydet", "Vazgeç", maxLength: 500);
            if (note is null)
            {
                return;
            }
        }

        _busy.IsVisible = true;
        try
        {
            string message = await ApiClient.Instance.ExplainTripMissingAsync(tripId, cylinders.Select(c => c.CylinderId).ToList(), reason.Key, note, customerId);
            _result.Text = message;
            _result.TextColor = Ui.Muted;
            _resultCard.IsVisible = true;
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Gerekçe", exception.Message, "Tamam");
        }
        finally
        {
            _busy.IsVisible = false;
        }
    }

    private async Task<CustomerItem?> PickCustomerAsync(
        string prompt = "Tüpün kaldığı müşteriyi arayın (en az 2 harf). Boş bırakırsanız adı elle yazarsınız.", string cancel = "Elle yaz")
    {
        while (true)
        {
            string? query = await DisplayPromptAsync("Müşteri", prompt, "Ara", cancel);
            query = query?.Trim();
            if (string.IsNullOrEmpty(query))
            {
                return null;
            }

            if (query.Length < Ui.LiveSearchMinLength)
            {
                continue;
            }

            List<CustomerItem> found;
            try
            {
                _busy.IsVisible = true;
                found = await ApiClient.Instance.CustomersAsync(query);
            }
            catch (Exception exception)
            {
                await DisplayAlertAsync("Müşteri", exception.Message, "Tamam");
                return null;
            }
            finally
            {
                _busy.IsVisible = false;
            }

            List<string> items = found.Select(c => $"{c.Name} ({c.Code})").ToList();
            string? picked = await DisplayActionSheetAsync(found.Count == 0 ? "Müşteri bulunamadı" : "Müşteri seçin", "Yeniden ara", null, items.ToArray());
            int index = picked is null ? -1 : items.IndexOf(picked);
            if (index >= 0)
            {
                return found[index];
            }
        }
    }
}
