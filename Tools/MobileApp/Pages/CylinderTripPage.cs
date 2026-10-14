using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Sefer bazlı araç ve tüp takibi: depoda dolu yükleme, müşteride teslim ve boş alma, depo dönüşü ve tutarlılık kontrolü.
public sealed class CylinderTripPage : ContentPage
{
    private const int LoadMode = 0;
    private const int DeliverMode = 1;
    private const int PickupMode = 2;
    private const int ReturnMode = 3;

    private static readonly string[] Modes = ["Depoda yükleme (yalnız dolu)", "Müşteriye teslim", "Müşteriden boş alma", "Depo dönüşü", "Kontrol"];

    private readonly Label _overview = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Picker _trip = new() { Title = "Aktif sefer" };
    private readonly Picker _vehicle = new() { Title = "Sefer aracı" };
    private readonly Picker _mode = new() { Title = "İşlem" };
    private readonly Picker _groupPlate = new() { Title = "Araç filtresi" };
    private readonly ContentView _groups = new();
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
        foreach (string mode in Modes)
        {
            _mode.Items.Add(mode);
        }

        _mode.SelectedIndex = 0;
        _mode.SelectedIndexChanged += (_, _) => ApplyMode();
        _trip.SelectedIndexChanged += (_, _) => RefreshVehicles();
        _groupPlate.SelectedIndexChanged += async (_, _) => await RefreshGroupsAsync();
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
                    Ui.Note("\"Sefer Başlat\" ile seferi açın, depoda DOLU tüpleri okutup araca yükleyin (boş tüp kabul edilmez). Müşteride teslim edilen tüpleri \"Müşteriye teslim\", müşteriden alınan boşları \"Müşteriden boş alma\" ile okutun. Araç dönünce tüm tüpleri \"Depo dönüşü\" ile okutun: satılmayan dolular dolu, alınan boşlar boş olarak depoya girer. Okutulmayan her tüp için nerede / neden okutulmadığını seçmeden sefer kapatılamaz."),
                    Ui.Allowed(Ui.Primary("🚚 Sefer Başlat", async (_, _) => await StartTripAsync()), "sefer-baslat"),
                    Ui.Section(
                        "Seferdeki araçlar",
                        _overview,
                        Ui.ActionRow(
                            Ui.Secondary("Yenile", async (_, _) => await RefreshTripsAsync()),
                            Ui.Allowed(Ui.Secondary("Sefere araç ekle", async (_, _) => await AddVehicleAsync()), "sefer-baslat"))),
                    Ui.Section(
                        "Sefer ve araç",
                        _trip,
                        _vehicle),
                    Ui.Section(
                        "Okutma",
                        _mode,
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
                        Ui.Allowed(Ui.Primary("Kaydet / kontrol et", async (_, _) => await SubmitAsync()), "sefer-yukle|sefer-teslim"),
                        Ui.Allowed(Ui.Secondary("Okutulmayan tüplere gerekçe yaz", async (_, _) => await ExplainMissingAsync()), "sefer-teslim")),
                    _busy,
                    _resultCard,
                    Ui.Section(
                        "Yük listesi (gaz türüne göre)",
                        _groupPlate,
                        _groupsTotal,
                        _groups,
                        Ui.Secondary("Listeyi yenile", async (_, _) => await RefreshGroupsAsync())),
                    Ui.Allowed(Ui.Secondary("Seferi kapat", async (_, _) => await CloseTripAsync()), "sefer-kapat")
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
        bool deliver = _mode.SelectedIndex == DeliverMode;
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
            $"Dolu yüklü {trip.LoadedTotal} · teslim {trip.DeliveredTotal} · boş alınan {trip.PickedUpTotal} · depo dönüş {trip.ReturnedTotal}"
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

    private async Task StartTripAsync()
    {
        (TripStartResponse Trip, string? FirstPlate)? started = await TripStartPage.OpenAsync(Navigation);
        if (started is null)
        {
            return;
        }

        (TripStartResponse trip, string? firstPlate) = started.Value;

        _mode.SelectedIndex = LoadMode;
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
        int mode = _mode.SelectedIndex;
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

            _scanned.Clear();
            RefreshScanned();
            await RefreshTripsAsync(tripId);
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

    // Cylinders that came back without a delivery or depot return scan: the driver picks one (or all unexplained) and
    // chooses where it is / why it was not scanned.
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
                    : $"{open.Count} tüp hâlâ araçta görünüyor. Önce \"Depo dönüşü\" ile dönen tüpleri okutun; okutulmayanlar sonra burada listelenir.", "Tamam");
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

    // Asks "where / why" for the given cylinders and saves it; null when the user cancelled.
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

    private async Task CloseTripAsync()
    {
        if (SelectedTrip is not ActiveTripSummary trip)
        {
            return;
        }

        bool ok = await DisplayAlertAsync("Seferi kapat",
            $"{trip.Title} kapatılsın mı? Önce dönen tüm tüpleri \"Depo dönüşü\" ile okuttuğunuzdan emin olun. Okutulmayan her tüp için gerekçe seçmeniz istenecek.",
            "Devam", "İptal");
        if (!ok)
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

    // Every cylinder that was neither delivered nor scanned back needs a reason before the server lets the trip close.
    // Returns false when the user stopped; reasons chosen so far stay saved.
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
                await DisplayAlertAsync("Sefer", $"Sefer kapatılmadı. {done - 1} tüpün gerekçesi kaydedildi; kalanlar için tekrar \"Seferi kapat\"a basın.", "Tamam");
                return false;
            }
        }

        return true;
    }
}
