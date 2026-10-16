using System.Globalization;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Start screen: one tile per section the signed-in user may open; every tile pushes the existing page, back returns here.
public sealed class HomePage : ContentPage
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly Color Glow = Color.FromArgb("#5EEAD4");

    private sealed record Tile(string Glyph, string Title, string? Subtitle, bool Wide, Color From, Color To, Func<Task> Open, Label? TitleLabel = null);

    private static readonly TimeSpan AccessRefreshInterval = TimeSpan.FromSeconds(30);
    private static DateTime _accessCheckedUtc;

    private readonly Dictionary<string, Page> _pages = [];
    private readonly bool _driver;
    private readonly Label _tripTitle = new() { Text = "Sevkiyat başlat" };
    private bool _hasOpenTrip;
    private bool _checkingAccess;
    private readonly Border _sync;
    private readonly Label _syncText = new() { FontSize = 12, TextColor = Colors.White.WithAlpha(0.85f), LineBreakMode = LineBreakMode.WordWrap };
    private readonly ActivityIndicator _syncBusy = new() { IsVisible = false, IsRunning = false, Color = Colors.White, WidthRequest = 22, HeightRequest = 22 };
    private readonly Border _check;
    private readonly Label _checkText = new() { FontSize = 12, TextColor = Colors.White.WithAlpha(0.85f), LineBreakMode = LineBreakMode.WordWrap,
        Text = "Telefondaki sürüm: " + AppInfo.Current.VersionString };
    private readonly ActivityIndicator _checkBusy = new() { IsVisible = false, IsRunning = false, Color = Colors.White, WidthRequest = 22, HeightRequest = 22 };
    private readonly Border _update;
    private readonly Label _updateText = new() { FontSize = 14, FontFamily = "OpenSansSemibold", TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap, VerticalTextAlignment = TextAlignment.Center };
    private readonly ActivityIndicator _updateBusy = new() { IsVisible = false, Color = Glow };
    private bool _checkingUpdate;
    private readonly int _simulationVersion = ApiClient.Instance.SimulationVersion;

    public HomePage()
    {
        Title = "ArsanTüpTakip";
        NavigationPage.SetHasNavigationBar(this, false);
        Ui.ApplyPageChrome(this);
        ApiClient api = ApiClient.Instance;
        AccountInfo? account = api.Account;
        _accessCheckedUtc = DateTime.UtcNow;

        List<Tile> tiles = [];
        Color navy = Ui.Navy;
        Color indigo = Ui.Indigo;
        _driver = account?.IsDriver == true;
        // Perm.CustomerLocation is only granted while the trip plan feature is on.
        Tile? planTile = api.Can(Perm.TripRoute) && api.Can(Perm.CustomerLocation)
            ? new Tile(Icons.Event, "Bugünkü sefer planım", "Panelde hazırlanan müşteri sırası", true, Color.FromArgb("#0E7490"), navy,
                () => Open("sefer-plani", () => new TripPlanTodayPage()))
            : null;
        if (_driver)
        {
            // Drivers: the four daily jobs first, in this order; every tile still needs its permission, so a permission
            // removed in the panel hides its tile and one granted there shows the regular tile further down.
            string? plate = account?.FixedPlate;
            if (api.Can(Perm.TripView))
            {
                tiles.Add(new Tile(Icons.Shipping, "Sevkiyat başlat", plate is null ? "Sefer, yükleme, teslim, dönüş" : $"Araç: {plate}", true,
                    Color.FromArgb("#B45309"), Color.FromArgb("#78350F"), OpenTripAsync, _tripTitle));
                tiles.Add(new Tile(Icons.FactCheck, "Tamamlanmış seferler", "Bitmiş seferler · numara ve özet", true,
                    Color.FromArgb("#0F766E"), Color.FromArgb("#134E4A"),
                    () => Open("sefer-gecmis", () => new CompletedTripsPage())));
                if (planTile is not null)
                {
                    tiles.Add(planTile);
                }
            }

            if (api.Can(Perm.CylinderView))
            {
                tiles.Add(new Tile(Icons.ManageSearch, "Tüp bilgisi", "Barkodu okut: tüp nerede, dolu mu", true, Color.FromArgb("#0F766E"), Color.FromArgb("#134E4A"),
                    () => Open("bilgi", () => new CylinderInfoPage())));
            }

            if (api.Can(Perm.StockCount))
            {
                tiles.Add(new Tile(Icons.Warehouse, "Sayım", "Sayımı başlat → doğrudan okutma", true, Color.FromArgb("#312E81"), navy,
                    () => Open("depo-sayim", () => new StockCountPage())));
            }
        }
        else
        {
            if (api.Can(Perm.CylinderView))
            {
                tiles.Add(new Tile(Icons.ManageSearch, "Tüp Bilgisi", null, false, indigo, navy, () => Open("bilgi", () => new CylinderInfoPage())));
            }
        }

        if (api.Can(Perm.Summary))
        {
            tiles.Add(new Tile(Icons.Dashboard, "Tüp Durumu", null, false, indigo, navy, () => Open("durum", () => new CylinderStatusPage())));
        }

        if (!_driver && api.Can(Perm.CylinderView))
        {
            tiles.Add(new Tile(Icons.Inventory, "Müşteri Stoku", "Müşterideki tüpler: adet, gün, barkod", true, Color.FromArgb("#0F766E"), navy,
                () => Open("stok", () => new CustomerStockPage())));
        }

        if (!_driver && api.Can(Perm.TripView))
        {
            tiles.Add(new Tile(Icons.Shipping, "Sefer", "Yükle, teslim et, dönüş", true, Color.FromArgb("#312E81"), navy, () => Open("sefer", () => new CylinderTripPage())));
            tiles.Add(new Tile(Icons.FactCheck, "Tamamlanmış seferler", "Bitmiş seferler · numara ve özet", true,
                Color.FromArgb("#0F766E"), navy, () => Open("sefer-gecmis", () => new CompletedTripsPage())));
            if (planTile is not null)
            {
                tiles.Add(planTile);
            }
        }

        if (api.Can(Perm.VehicleView))
        {
            tiles.Add(new Tile(Icons.Car, "Araçlar", null, false, indigo, navy, () => Open("arac", () => new VehiclePage())));
        }

        if (!_driver && api.Can(Perm.StockCount))
        {
            tiles.Add(new Tile(Icons.Warehouse, "Depo Sayımı", "Okut, gaz ve boyuta göre say", true, Color.FromArgb("#0F766E"), navy,
                () => Open("depo-sayim", () => new StockCountPage())));
        }

        if (api.Can(Perm.FillRecord))
        {
            tiles.Add(new Tile(Icons.Tank, "Dolum", "Doldurulan tüpleri lot no ile okut", false, Color.FromArgb("#0E7490"), navy, () => Open("dolum", () => new FillPage())));
        }

        if (api.Can(Perm.CylinderAudit))
        {
            tiles.Add(new Tile(Icons.FactCheck, "Müşteride Sayım", null, false, indigo, navy, () => Open("sayim", () => new CylinderAuditPage())));
        }

        if (api.CanAny(Perm.Summary, Perm.Reports, Perm.MovementHistory))
        {
            tiles.Add(new Tile(Icons.Insights, "Raporlar", null, false, indigo, navy, () => Open("rapor", () => new SummaryPage())));
        }

        if (api.IsAdmin)
        {
            tiles.Add(new Tile(Icons.ManageSearch, "Kullanıcı gözünden", "Başka kullanıcının ekranını gör, yetkilerini ekle/çıkar", true,
                Color.FromArgb("#7C3AED"), navy, () => Open("simulasyon", () => new SimulationPage())));
        }

        tiles.Add(new Tile(Icons.Account, "Hesap", null, false, indigo, navy, () => Open("hesap", () => new AccountPage())));
        tiles.Add(new Tile(Icons.Logout, "Çıkış", null, false, Color.FromArgb("#4B5563"), Color.FromArgb("#1F2937"), LogoutAsync));

        Grid syncRow = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        syncRow.Add(Ui.IconLabel(Icons.Sync, Colors.White, 26), 0, 0);
        syncRow.Add(new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "Senkronize et", FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap },
                _syncText
            }
        }, 1, 0);
        syncRow.Add(_syncBusy, 2, 0);
        TapGestureRecognizer syncTap = new();
        syncTap.Tapped += async (_, _) => await SyncAsync();
        _sync = new Border
        {
            Padding = new Thickness(16, 12),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#0F766E"),
            Content = syncRow,
            GestureRecognizers = { syncTap }
        };
        SemanticProperties.SetDescription(_sync, "Senkronize et");
        OfflineMoves.Changed += () => MainThread.BeginInvokeOnMainThread(RefreshSync);
        OfflineTripOps.Changed += () => MainThread.BeginInvokeOnMainThread(RefreshSync);
        OfflineTripOps.Watch();

        Grid checkRow = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        checkRow.Add(Ui.IconLabel(Icons.SystemUpdate, Colors.White, 26), 0, 0);
        checkRow.Add(new VerticalStackLayout
        {
            Spacing = 2,
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "Güncellemeyi denetle", FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = Colors.White },
                _checkText
            }
        }, 1, 0);
        checkRow.Add(_checkBusy, 2, 0);
        TapGestureRecognizer checkTap = new();
        checkTap.Tapped += async (_, _) => await CheckUpdateAsync();
        _check = new Border
        {
            Padding = new Thickness(16, 12),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#334155"),
            Content = checkRow,
            GestureRecognizers = { checkTap }
        };
        SemanticProperties.SetDescription(_check, "Güncellemeyi denetle");

        Grid updateRow = new() { ColumnSpacing = 12, ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)] };
        updateRow.Add(Ui.IconLabel(Icons.SystemUpdate, Colors.White, 26), 0, 0);
        updateRow.Add(_updateText, 1, 0);
        TapGestureRecognizer updateTap = new();
        updateTap.Tapped += async (_, _) =>
        {
            if (!_updateBusy.IsRunning)
            {
                await UpdateCheck.InstallAsync(this, _updateText, _updateBusy);
            }
        };
        _update = new Border
        {
            Padding = new Thickness(16, 12),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            Background = new LinearGradientBrush([new GradientStop(Color.FromArgb("#B45309"), 0), new GradientStop(Color.FromArgb("#F59E0B"), 1)], new Point(0, 0), new Point(1, 1)),
            IsVisible = false,
            Content = updateRow,
            GestureRecognizers = { updateTap }
        };
        SemanticProperties.SetDescription(_update, "Güncelleme");

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(0, 0, 0, 20),
                Children =
                {
                    Header(account),
                    Body(_driver
                        ? [SimulationBanner(), _update, _updateBusy, TileGrid(tiles), _sync, _check, Footer()]
                        : [SimulationBanner(), _update, _updateBusy, _sync, TileGrid(tiles), _check, Footer()])
                }
            }
        };
    }

    private View SimulationBanner()
    {
        if (ApiClient.Instance.Simulation is not SimulationView s)
        {
            return new ContentView { IsVisible = false };
        }

        int userId = s.Id;
        return new Border
        {
            Padding = new Thickness(14, 12),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#7C3AED"),
            Content = new VerticalStackLayout
            {
                Spacing = 8,
                Children =
                {
                    new Label
                    {
                        Text = $"Simülasyon: {s.DisplayName} ({s.User}, {s.Role}) gözünden görüyorsunuz.",
                        FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = Colors.White, LineBreakMode = LineBreakMode.WordWrap
                    },
                    new Label
                    {
                        Text = "Ekranlar onun yetkileriyle görünür; yaptığınız işlemler yine sizin (Yönetici) hesabınızla kaydedilir.",
                        FontSize = 12, TextColor = Colors.White.WithAlpha(0.85f), LineBreakMode = LineBreakMode.WordWrap
                    },
                    new Grid
                    {
                        ColumnSpacing = 10,
                        ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)],
                        Children =
                        {
                            Ui.Secondary("Yetkileri düzenle", async (_, _) => await Open("simulasyon-" + userId, () => new SimulationPage(userId))),
                            WithColumn(Ui.Primary("Bitir", (_, _) => { ApiClient.Instance.EndSimulation(); App.ShowMain(); }), 1)
                        }
                    }
                }
            }
        };
    }

    private static View WithColumn(View view, int column)
    {
        Grid.SetColumn(view, column);
        return view;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_simulationVersion != ApiClient.Instance.SimulationVersion)
        {
            App.ShowMain();
            return;
        }

        RefreshSync();
        await ServerUrlCoordinator.RefreshIfDueAsync();
        if (await RefreshAccessAsync())
        {
            return;
        }

        await RefreshTripTileAsync();
        await RefreshUpdateAsync();
        await LocationPermission.EnsureAsync(this);
    }

    // Also called when the app comes back to the foreground. Permissions or the fixed vehicle changed in the panel
    // rebuild the home screen at once (tiles appear or disappear); returns true when it was rebuilt.
    public async Task<bool> RefreshAccessAsync(bool force = false)
    {
        // Rebuilding the home screen during a sync or an update download would cut that work off.
        if (_checkingAccess || SyncNow.IsRunning || _updateBusy.IsRunning
            || (!force && DateTime.UtcNow - _accessCheckedUtc < AccessRefreshInterval) || ApiClient.Instance.RealAccount is not AccountInfo before)
        {
            return false;
        }

        _checkingAccess = true;
        try
        {
            AccountInfo now = await ApiClient.Instance.RefreshAccountAsync();
            _accessCheckedUtc = DateTime.UtcNow;
            if (now.SameAccess(before))
            {
                return false;
            }

            App.ShowMain();
            return true;
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
        {
            // Offline or the session ended (the login screen is shown by ApiClient); keep the current tiles.
            return false;
        }
        finally
        {
            _checkingAccess = false;
        }
    }

    // "Sevkiyat başlat" becomes "Sevkiyata devam" while the driver's vehicle (or, without one, any trip) is still open.
    private async Task RefreshTripTileAsync()
    {
        if (!_driver || !ApiClient.Instance.Can(Perm.TripView))
        {
            return;
        }

        try
        {
            string? plate = ApiClient.Instance.Account?.FixedPlate;
            List<ActiveTripSummary> trips = await ApiClient.Instance.ActiveTripsAsync();
            _hasOpenTrip = plate is null
                ? trips.Count > 0
                : trips.Any(t => t.Vehicles.Any(v => string.Equals(v.Plate, plate, StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
        {
            _hasOpenTrip = false;
        }

        _tripTitle.Text = _hasOpenTrip ? "Sevkiyata devam" : "Sevkiyat başlat";
    }

    private async Task OpenTripAsync()
    {
        await Open("sefer", () => new CylinderTripPage());
        if (!_hasOpenTrip && ApiClient.Instance.Can(Perm.TripStart) && _pages.GetValueOrDefault("sefer") is CylinderTripPage page
            && Navigation.NavigationStack.LastOrDefault() == page)
        {
            await page.StartTripAsync();
        }
    }

    private void RefreshSync()
    {
        _syncText.Text = SyncNow.StatusText();
        _sync.BackgroundColor = OfflineMoves.Pending.Count + OfflineTripOps.Pending.Count > 0 ? Color.FromArgb("#B45309") : Color.FromArgb("#0F766E");
    }

    private async Task SyncAsync()
    {
        if (_syncBusy.IsRunning)
        {
            return;
        }

        if (_updateBusy.IsRunning)
        {
            await DisplayAlertAsync("Senkronizasyon", "Güncelleme indiriliyor; bitince senkronize edin.", "Tamam");
            return;
        }

        _syncBusy.IsVisible = _syncBusy.IsRunning = true;
        _syncText.Text = "Kayıtlar gönderiliyor…";
        try
        {
            await SyncNow.RunAsync(this);
        }
        finally
        {
            _syncBusy.IsVisible = _syncBusy.IsRunning = false;
            RefreshSync();
        }
    }

    private async Task CheckUpdateAsync()
    {
        if (_checkBusy.IsRunning || _updateBusy.IsRunning)
        {
            return;
        }

        if (SyncNow.IsRunning)
        {
            await DisplayAlertAsync("Güncelleme", "Senkronizasyon sürüyor. Bitmesini bekleyin, sonra güncellemeyi denetleyin.", "Tamam");
            return;
        }

        _checkBusy.IsVisible = _checkBusy.IsRunning = true;
        _checkText.Text = "Denetleniyor…";
        UpdateInfo? available;
        try
        {
            (available, string text) = await UpdateCheck.CheckNowAsync();
            _checkText.Text = text;
        }
        finally
        {
            _checkBusy.IsVisible = _checkBusy.IsRunning = false;
        }

        _update.IsVisible = available is not null;
        if (available is null)
        {
            return;
        }

        _updateText.Text = $"Yeni sürüm {available.PublishedMobileVersion} (build {available.PublishedMobileBuild}) hazır. Kurmak için dokunun.";
        if (await DisplayAlertAsync("Güncelleme", _checkText.Text + " Şimdi güncellensin mi?", "Güncelle", "Sonra"))
        {
            await UpdateCheck.InstallAsync(this, _updateText, _updateBusy);
        }
    }

    // Also called when the app comes back to the foreground: a signed-in user may never see the login screen again.
    public async Task RefreshUpdateAsync()
    {
        if (_checkingUpdate || _updateBusy.IsRunning || SyncNow.IsRunning)
        {
            return;
        }

        _checkingUpdate = true;
        try
        {
            UpdateInfo? info = await UpdateCheck.GetAvailableAsync();
            _update.IsVisible = info is not null;
            if (info is null)
            {
                return;
            }

            _updateText.Text = $"Yeni sürüm {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}) hazır. Kurmak için dokunun.";
            if (Navigation.NavigationStack.LastOrDefault() == this)
            {
                await UpdateCheck.PromptOnceAsync(this, info, _updateText, _updateBusy);
            }
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, "Güncelleme denetimi");
        }
        finally
        {
            _checkingUpdate = false;
        }
    }

    private static VerticalStackLayout Body(View[] views)
    {
        VerticalStackLayout body = new() { Padding = new Thickness(14, 0), Spacing = 12 };
        foreach (View view in views)
        {
            body.Children.Add(view);
        }

        return body;
    }

    private static View Header(AccountInfo? account)
    {
        Grid grid = new()
        {
            ColumnSpacing = 12,
            RowSpacing = 2,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto)]
        };

        Grid brand = new() { ColumnSpacing = 10, ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)] };
        brand.Add(new Border
        {
            WidthRequest = 46,
            HeightRequest = 46,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Glow.WithAlpha(0.5f)),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 14 },
            BackgroundColor = Colors.White.WithAlpha(0.08f),
            Content = Ui.IconLabel(Icons.Tank, Glow, 28)
        }, 0, 0);
        brand.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label
                {
                    FormattedText = new FormattedString
                    {
                        Spans =
                        {
                            new Span { Text = "Arsan", TextColor = Colors.White, FontSize = 24, FontFamily = "OpenSansSemibold" },
                            new Span { Text = "TüpTakip", TextColor = Glow, FontSize = 24, FontFamily = "OpenSansSemibold" }
                        }
                    }
                },
                new Label { Text = "TÜP TAKİP SİSTEMİ", FontSize = 11, CharacterSpacing = 1.5, TextColor = Colors.White.WithAlpha(0.65f) }
            }
        }, 1, 0);
        grid.Add(brand, 0, 0);
        Grid.SetColumnSpan(brand, 2);

        BoxView divider = new() { HeightRequest = 1, Color = Colors.White.WithAlpha(0.12f), Margin = new Thickness(0, 14, 0, 10) };
        grid.Add(divider, 0, 1);
        Grid.SetColumnSpan(divider, 2);

        Grid user = new() { ColumnSpacing = 10, ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)] };
        user.Add(new Border
        {
            WidthRequest = 36,
            HeightRequest = 36,
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.Ellipse(),
            BackgroundColor = Ui.Accent.WithAlpha(0.35f),
            Content = Ui.IconLabel(Icons.Account, Colors.White, 22)
        }, 0, 0);
        VerticalStackLayout userTexts = new()
        {
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = account?.DisplayName ?? "-", FontSize = 15, FontFamily = "OpenSansSemibold", TextColor = Colors.White, LineBreakMode = LineBreakMode.TailTruncation }
            }
        };
        if (account?.IsDriver != true && !string.IsNullOrEmpty(account?.RoleText))
        {
            userTexts.Children.Add(new Label { Text = account.RoleText, FontSize = 12, TextColor = Glow });
        }

        if (!string.IsNullOrEmpty(account?.FixedPlate))
        {
            userTexts.Children.Add(new Label
            {
                Text = "🚚 Araç: " + account.FixedPlate,
                FontSize = 16,
                FontFamily = "OpenSansSemibold",
                TextColor = Colors.White
            });
        }

        user.Add(userTexts, 1, 0);
        grid.Add(user, 0, 2);
        Grid.SetColumnSpan(user, 2);

        return new Border
        {
            Padding = new Thickness(18, 22, 18, 18),
            StrokeThickness = 0,
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = new CornerRadius(0, 0, 28, 28) },
            Background = new LinearGradientBrush(
                [new GradientStop(Ui.Navy, 0), new GradientStop(Ui.Indigo, 0.55f), new GradientStop(Color.FromArgb("#0F766E"), 1)],
                new Point(0, 0), new Point(1, 1)),
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.25f, Radius = 16, Offset = new Point(0, 4) },
            Content = grid
        };
    }

    // Wide tiles take a full row; a normal tile left without a partner spans the row too, so the grid never has holes.
    private static Grid TileGrid(List<Tile> tiles)
    {
        Grid grid = new()
        {
            ColumnSpacing = 12,
            RowSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)]
        };
        int row = 0;
        Tile? waiting = null;

        void Place(Tile tile, int column, int span)
        {
            if (grid.RowDefinitions.Count <= row)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            }

            View view = TileView(tile, span == 2);
            grid.Add(view, column, row);
            Grid.SetColumnSpan(view, span);
        }

        foreach (Tile tile in tiles)
        {
            if (tile.Wide)
            {
                if (waiting is not null)
                {
                    Place(waiting, 0, 2);
                    row++;
                    waiting = null;
                }

                Place(tile, 0, 2);
                row++;
            }
            else if (waiting is null)
            {
                waiting = tile;
            }
            else
            {
                Place(waiting, 0, 1);
                Place(tile, 1, 1);
                row++;
                waiting = null;
            }
        }

        if (waiting is not null)
        {
            Place(waiting, 0, 2);
        }

        return grid;
    }

    private static View TileView(Tile tile, bool wide)
    {
        Border iconBubble = new()
        {
            WidthRequest = wide ? 58 : 62,
            HeightRequest = wide ? 58 : 62,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Glow.WithAlpha(0.35f)),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 20 },
            BackgroundColor = Colors.White.WithAlpha(0.08f),
            HorizontalOptions = wide ? LayoutOptions.Start : LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = Ui.IconLabel(tile.Glyph, Glow, wide ? 32 : 36)
        };
        Label title = tile.TitleLabel ?? new Label { Text = tile.Title };
        title.FontSize = wide ? 18 : 15;
        title.FontFamily = "OpenSansSemibold";
        title.TextColor = Colors.White;
        title.HorizontalTextAlignment = wide ? TextAlignment.Start : TextAlignment.Center;
        title.LineBreakMode = LineBreakMode.WordWrap;

        View content;
        if (wide)
        {
            Grid row = new()
            {
                ColumnSpacing = 16,
                ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
            };
            row.Add(iconBubble, 0, 0);
            VerticalStackLayout texts = new() { VerticalOptions = LayoutOptions.Center, Spacing = 2, Children = { title } };
            if (tile.Subtitle is not null)
            {
                texts.Children.Add(new Label { Text = tile.Subtitle, FontSize = 12, TextColor = Colors.White.WithAlpha(0.7f) });
            }

            row.Add(texts, 1, 0);
            row.Add(new Label { Text = "›", FontSize = 30, TextColor = Glow, VerticalTextAlignment = TextAlignment.Center }, 2, 0);
            content = row;
        }
        else
        {
            content = new VerticalStackLayout
            {
                Spacing = 10,
                VerticalOptions = LayoutOptions.Center,
                Children = { iconBubble, title }
            };
        }

        Border card = new()
        {
            Padding = wide ? new Thickness(18, 16) : new Thickness(10, 18),
            MinimumHeightRequest = wide ? 96 : 132,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Colors.White.WithAlpha(0.08f)),
            StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 22 },
            Background = new LinearGradientBrush([new GradientStop(tile.From, 0), new GradientStop(tile.To, 1)], new Point(0, 0), new Point(1, 1)),
            Shadow = new Shadow { Brush = new SolidColorBrush(Colors.Black), Opacity = 0.22f, Radius = 10, Offset = new Point(0, 4) },
            Content = content
        };
        SemanticProperties.SetDescription(card, tile.Title);

        bool busy = false;
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            if (busy)
            {
                return;
            }

            busy = true;
            try
            {
                await card.ScaleToAsync(0.94, 70, Easing.CubicOut);
                await card.ScaleToAsync(1, 90, Easing.CubicIn);
                await tile.Open();
            }
            finally
            {
                busy = false;
            }
        };
        card.GestureRecognizers.Add(tap);
        return card;
    }

    private static View Footer()
    {
        Grid footer = new()
        {
            ColumnSpacing = 12,
            Margin = new Thickness(0, 8, 0, 0),
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)]
        };
        footer.Add(new BoxView { HeightRequest = 2, Color = Ui.Accent, VerticalOptions = LayoutOptions.Center }, 0, 0);
        footer.Add(new Label
        {
            Text = DateTime.Now.ToString("d MMMM yyyy, dddd", Turkish),
            FontSize = 14,
            FontFamily = "OpenSansSemibold",
            TextColor = Ui.Muted
        }, 1, 0);
        footer.Add(new BoxView { HeightRequest = 2, Color = Ui.Indigo, VerticalOptions = LayoutOptions.Center }, 2, 0);
        return footer;
    }

    // Section pages are created once and reused, like the former tabs, so their state and event handlers are not duplicated.
    private async Task Open(string key, Func<Page> create)
    {
        try
        {
            if (!_pages.TryGetValue(key, out Page? page))
            {
                page = create();
                _pages[key] = page;
            }

            if (Navigation.NavigationStack.Contains(page))
            {
                return;
            }

            await Navigation.PushAsync(page);
        }
        catch (Exception exception)
        {
            CrashLog.Write(exception, key + " ekranı");
            await Navigation.PushAsync(CrashLog.ErrorPage("Ekran açılamadı", exception.ToString()));
        }
    }

    private async Task LogoutAsync()
    {
        if (await DisplayAlertAsync("Çıkış", "Bu telefondaki oturum kapatılsın mı? Tekrar kullanmak için kullanıcı adı ve şifre gerekir.", "Çıkış yap", "Vazgeç"))
        {
            await App.LogoutAsync();
        }
    }
}
