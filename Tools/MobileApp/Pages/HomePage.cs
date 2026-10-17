using System.Globalization;
using ArsanGazMobile.Services;
using Microsoft.Maui.Controls.Shapes;

namespace ArsanGazMobile.Pages;

// Ana menü: TupTakip düzeni (şoför önce sevkiyat; ofis geniş kartlar). ERP renkleri korunur.
public sealed class HomePage : ContentPage
{
    private const int Columns = 2;
    private const double TileHeight = 132;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private sealed record TileSpec(string Title, string? Subtitle, string Glyph, string Start, string End, string? Permission, bool Wide, Func<HomePage, Task> Open, Label? TitleLabel = null);

    private readonly Grid _grid = new() { ColumnSpacing = 12, RowSpacing = 12 };
    private readonly Label _userName = new() { TextColor = Colors.White, FontSize = 16, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.TailTruncation };
    private readonly Label _userRole = new() { TextColor = Color.FromRgba(255, 255, 255, 200), FontSize = 13 };
    private readonly Label _initials = new() { TextColor = Colors.White, FontSize = 16, FontFamily = "OpenSansSemibold", HorizontalTextAlignment = TextAlignment.Center, VerticalTextAlignment = TextAlignment.Center };
    private readonly Border _updateBanner;
    private readonly Border _checkBanner;
    private readonly Label _checkText = new()
    {
        FontSize = 12,
        TextColor = Colors.White.WithAlpha(0.85f),
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "Telefondaki sürüm: " + AppInfo.Current.VersionString
    };
    private readonly ActivityIndicator _checkBusy = new() { IsVisible = false, IsRunning = false, Color = Colors.White, WidthRequest = 22, HeightRequest = 22 };
    private readonly Label _updateText = new() { TextColor = Colors.White, FontSize = 14, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private readonly ActivityIndicator _updateBusy = new() { IsVisible = false, Color = Ui.Accent };
    private bool _checkingUpdate;
    private readonly Label _date = new() { FontSize = 15, FontFamily = "OpenSansSemibold", HorizontalTextAlignment = TextAlignment.Center };
    private readonly Dictionary<string, Page> _pages = [];
    private readonly Label _tripTitle = new()
    {
        Text = "Sevkiyat başlat",
        TextColor = Colors.White,
        FontSize = 18,
        FontFamily = "OpenSansSemibold",
        MaxLines = 2,
        LineBreakMode = LineBreakMode.WordWrap,
        HorizontalTextAlignment = TextAlignment.Start
    };
    private string _permissionKey = string.Empty;
    private bool _opening;
    private bool _started;
    private bool _hasOpenTrip;
    private bool _driver;

    public HomePage(UserSession session)
    {
        Title = "Ana menü";
        Shell.SetNavBarIsVisible(this, false);
        Ui.ApplyPageChrome(this);
        for (int i = 0; i < Columns; i++)
        {
            _grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        _updateBanner = new Border
        {
            IsVisible = false,
            Padding = new Thickness(14, 12),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Background = Gradient("#B45309", "#F59E0B"),
            Content = Row(Icons.Glyph(Icons.SystemUpdate, Colors.White, 26), _updateText)
        };
        AddTap(_updateBanner, async () =>
        {
            if (!_updateBusy.IsRunning)
            {
                await UpdateCheck.InstallAsync(this, _updateText, _updateBusy);
            }
        });

        Grid checkRow = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        checkRow.Add(Icons.Glyph(Icons.SystemUpdate, Colors.White, 26), 0, 0);
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
        _checkBanner = new Border
        {
            Padding = new Thickness(16, 12),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            BackgroundColor = Color.FromArgb("#334155"),
            Content = checkRow
        };
        SemanticProperties.SetDescription(_checkBanner, "Güncellemeyi denetle");
        AddTap(_checkBanner, CheckUpdateAsync);

        _date.SetAppThemeColor(Label.TextColorProperty, Color.FromArgb("#1E3A8A"), Color.FromArgb("#BAE6FD"));

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Spacing = 16,
                Padding = new Thickness(0, 0, 0, 24),
                Children =
                {
                    Header(),
                    new VerticalStackLayout
                    {
                        Padding = new Thickness(14, 0),
                        Spacing = 16,
                        Children = { _updateBanner, _updateBusy, _grid, _checkBanner, Footer() }
                    }
                }
            }
        };
        Apply(session);
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _date.Text = DateTime.Now.ToString("d MMMM yyyy, dddd", Turkish);
        if (!_started)
        {
            _started = true;
            try
            {
                await ServerUrlCoordinator.BootstrapAsync();
                Apply(await ApiClient.Instance.RefreshSessionAsync());
            }
            catch (ApiException)
            {
            }
        }

        if (_driver)
        {
            await RefreshTripTitleAsync();
        }

        await RefreshUpdateAsync();
    }

    private async Task CheckUpdateAsync()
    {
        if (_checkBusy.IsRunning || _updateBusy.IsRunning)
        {
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

        _updateBanner.IsVisible = available is not null;
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

    public async Task RefreshUpdateAsync()
    {
        if (_checkingUpdate || _updateBusy.IsRunning)
        {
            return;
        }

        _checkingUpdate = true;
        try
        {
            UpdateInfo? info = await UpdateCheck.GetAvailableAsync();
            _updateBanner.IsVisible = info is not null;
            if (info is null)
            {
                return;
            }

            _updateText.Text = $"Yeni sürüm {info.PublishedMobileVersion} (build {info.PublishedMobileBuild}) hazır. Kurmak için dokunun.";
            if (Shell.Current?.CurrentPage == this && Navigation.ModalStack.Count == 0)
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

    private void Apply(UserSession session)
    {
        _userName.Text = session.DisplayName;
        _userRole.Text = session.Role;
        _initials.Text = Initials(session.DisplayName);
        _driver = session.IsDriver;
        string key = (_driver ? "D|" : "O|") + string.Join(",", session.Permissions.Order());
        if (key == _permissionKey)
        {
            return;
        }

        _permissionKey = key;
        BuildGrid(BuildTiles(session));
    }

    private List<TileSpec> BuildTiles(UserSession session)
    {
        List<TileSpec> tiles = [];
        bool Can(string? permission) => permission is null || session.Can(permission);

        if (_driver)
        {
            if (Can("sefer-goruntule"))
            {
                tiles.Add(new(
                    "Sevkiyat başlat",
                    "Sefer, yükleme, teslim, dönüş",
                    Icons.LocalShipping,
                    "#B45309",
                    "#F59E0B",
                    "sefer-goruntule",
                    true,
                    p => p.OpenTripAsync(),
                    _tripTitle));
                tiles.Add(new(
                    "Tamamlanmış seferler",
                    "Bitmiş seferler · numara ve özet",
                    Icons.History,
                    "#0F766E",
                    "#14B8A6",
                    "sefer-goruntule",
                    true,
                    p => p.OpenAsync("Tamamlanmış Seferler", () => new CompletedTripsPage())));
                if (Can("sefer-rota|sefer-plani"))
                {
                    tiles.Add(new(
                        "Bugünkü sefer planım",
                        "Panelde hazırlanan müşteri sırası",
                        Icons.Event,
                        "#1E3A8A",
                        "#0D9488",
                        "sefer-rota|sefer-plani",
                        true,
                        p => p.OpenAsync("Bugünkü sefer planım", () => new TripPlanTodayPage())));
                }
            }

            if (Can("tup-goruntule"))
            {
                tiles.Add(new(
                    "Tüp bilgisi",
                    "Barkodu okut: tüp nerede, dolu mu",
                    Icons.ManageSearch,
                    "#0F766E",
                    "#14B8A6",
                    "tup-goruntule",
                    true,
                    p => p.OpenAsync("Tüp Bilgisi", () => new CylinderInfoPage())));
            }

            if (Can("depo-sayim"))
            {
                tiles.Add(new(
                    "Sayım",
                    "Sayımı başlat → doğrudan okutma",
                    Icons.FactCheck,
                    "#3730A3",
                    "#6366F1",
                    "depo-sayim",
                    true,
                    p => p.OpenAsync("Depo Sayımı", () => new StockCountPage())));
            }

            if (Can("dolum-kaydi"))
            {
                tiles.Add(new("Dolum", "Lot ile depoda dolu", Icons.WaterDrop, "#0369A1", "#38BDF8", "dolum-kaydi", false,
                    p => p.OpenAsync("Dolum", () => new FillPage())));
            }

            if (Can("arac-goruntule"))
            {
                tiles.Add(new("Araçlar", null, Icons.LocalShipping, "#0E7490", "#22D3EE", "arac-goruntule", false,
                    p => p.OpenAsync("Araç İşlemleri", () => new VehiclePage())));
            }

            if (Can("tup-sayim"))
            {
                tiles.Add(new("Müşteride Sayım", null, Icons.FactCheck, "#155E75", "#0891B2", "tup-sayim", false,
                    p => p.OpenAsync("Müşteride Sayım", () => new CylinderAuditPage())));
            }

            if (Can("ozet|rapor|hareket-goruntule"))
            {
                tiles.Add(new("Raporlar", null, Icons.Insights, "#1E40AF", "#3B82F6", "ozet|rapor|hareket-goruntule", false,
                    p => p.OpenAsync("Raporlar", () => new SummaryPage())));
            }
        }
        else
        {
            if (Can("tup-goruntule"))
            {
                tiles.Add(new("Tüp Okut", "Barkod / QR ile hareket kaydı", Icons.QrScanner, "#1D4ED8", "#0EA5E9", "tup-goruntule", true,
                    p => p.OpenAsync("Tüp Okut", () => new CylinderPage())));
                tiles.Add(new("Tüp Bilgisi", null, Icons.ManageSearch, "#0F766E", "#14B8A6", "tup-goruntule", false,
                    p => p.OpenAsync("Tüp Bilgisi", () => new CylinderInfoPage())));
            }

            if (Can("tup-goruntule"))
            {
                tiles.Add(new("Tüp Durumu", null, Icons.Dashboard, "#3730A3", "#6366F1", "tup-goruntule", false,
                    p => p.OpenAsync("Tüp Durumu", () => new CylinderStatusPage())));
            }

            if (Can("tup-goruntule"))
            {
                tiles.Add(new("Müşteri Stoku", "Gaz/boyut, gün, barkodlar", Icons.PropaneTank, "#0F766E", "#14B8A6", "tup-goruntule", true,
                    p => p.OpenAsync("Müşteri Stoku", () => new CustomerStockPage())));
            }

            if (Can("sefer-goruntule"))
            {
                tiles.Add(new("Sefer", "Yükle, teslim et, dönüş", Icons.Route, "#1E3A8A", "#0D9488", "sefer-goruntule", true,
                    p => p.OpenAsync("Sefer", () => new CylinderTripPage())));
                tiles.Add(new("Tamamlanmış seferler", "Bitmiş seferler · numara ve özet", Icons.History, "#0F766E", "#14B8A6", "sefer-goruntule", true,
                    p => p.OpenAsync("Tamamlanmış Seferler", () => new CompletedTripsPage())));
                if (Can("sefer-rota|sefer-plani"))
                {
                    tiles.Add(new("Bugünkü sefer planım", "Panelde hazırlanan müşteri sırası", Icons.Event, "#1E3A8A", "#0D9488", "sefer-rota|sefer-plani", true,
                        p => p.OpenAsync("Bugünkü sefer planım", () => new TripPlanTodayPage())));
                }
            }

            if (Can("arac-goruntule"))
            {
                tiles.Add(new("Araçlar", null, Icons.LocalShipping, "#0E7490", "#22D3EE", "arac-goruntule", false,
                    p => p.OpenAsync("Araç İşlemleri", () => new VehiclePage())));
            }

            if (Can("depo-sayim"))
            {
                tiles.Add(new("Depo Sayımı", "Okut, gaz ve boyuta göre say", Icons.FactCheck, "#155E75", "#22D3EE", "depo-sayim", true,
                    p => p.OpenAsync("Depo Sayımı", () => new StockCountPage())));
            }

            if (Can("dolum-kaydi"))
            {
                tiles.Add(new("Dolum", "Lot ile depoda dolu", Icons.WaterDrop, "#0369A1", "#38BDF8", "dolum-kaydi", false,
                    p => p.OpenAsync("Dolum", () => new FillPage())));
            }

            if (Can("tup-sayim"))
            {
                tiles.Add(new("Müşteride Sayım", null, Icons.FactCheck, "#155E75", "#0891B2", "tup-sayim", false,
                    p => p.OpenAsync("Müşteride Sayım", () => new CylinderAuditPage())));
            }

            if (Can("ozet|rapor|hareket-goruntule"))
            {
                tiles.Add(new("Raporlar", null, Icons.Insights, "#1E40AF", "#3B82F6", "ozet|rapor|hareket-goruntule", false,
                    p => p.OpenAsync("Raporlar", () => new SummaryPage())));
            }

            if (Can("crm-goruntule"))
            {
                tiles.Add(new("CRM Firmalar", null, Icons.Handshake, "#4338CA", "#7C3AED", "crm-goruntule", false,
                    p => p.OpenAsync("CRM", () => new CrmPage(followUps: false))));
                tiles.Add(new("Takip", null, Icons.EventNote, "#5B21B6", "#8B5CF6", "crm-goruntule", false,
                    p => p.OpenAsync("Takip", () => new CrmPage(followUps: true))));
            }

            if (Can("kampanya-goruntule"))
            {
                tiles.Add(new("Kampanya", null, Icons.Campaign, "#9D174D", "#DB2777", "kampanya-goruntule", false,
                    p => p.OpenAsync("Kampanya", () => new CampaignPage())));
            }

            if (Can("asistan|cursor-gorev"))
            {
                tiles.Add(new("Asistan", null, Icons.SmartToy, "#0F766E", "#10B981", "asistan|cursor-gorev", false,
                    p => p.OpenAsync("Asistan", () => new ChatPage())));
            }
        }

        tiles.Add(new("Hesap ve Bağlantı", null, Icons.Settings, "#1F2937", "#475569", null, false,
            p => p.OpenAsync("Hesap ve Bağlantı", () => new ConnectionPage())));
        tiles.Add(new("Çıkış", null, Icons.Power, "#52525B", "#71717A", null, false,
            p => ConnectionPage.ConfirmLogoutAsync(p)));
        return tiles;
    }

    private void BuildGrid(List<TileSpec> tiles)
    {
        _grid.Children.Clear();
        _grid.RowDefinitions.Clear();
        List<List<(TileSpec Tile, int Span)>> rows = [[]];
        int used = 0;
        foreach (TileSpec tile in tiles)
        {
            int span = tile.Wide ? 2 : 1;
            if (used + span > Columns)
            {
                (TileSpec last, int lastSpan) = rows[^1][^1];
                rows[^1][^1] = (last, lastSpan + Columns - used);
                rows.Add([]);
                used = 0;
            }

            rows[^1].Add((tile, span));
            used += span;
        }

        if (used is > 0 and < Columns)
        {
            (TileSpec last, int lastSpan) = rows[^1][^1];
            rows[^1][^1] = (last, lastSpan + Columns - used);
        }

        for (int r = 0; r < rows.Count; r++)
        {
            _grid.RowDefinitions.Add(new RowDefinition(new GridLength(TileHeight)));
            int column = 0;
            foreach ((TileSpec tile, int span) in rows[r])
            {
                View view = Tile(tile, horizontal: span > 1 && tile.Subtitle is not null);
                _grid.Add(view, column, r);
                Grid.SetColumnSpan(view, span);
                column += span;
            }
        }
    }

    private Border Tile(TileSpec spec, bool horizontal)
    {
        Border iconBubble = new()
        {
            WidthRequest = horizontal ? 58 : 50,
            HeightRequest = horizontal ? 58 : 50,
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromRgba(255, 255, 255, 70)),
            BackgroundColor = Color.FromRgba(255, 255, 255, 38),
            StrokeShape = new RoundRectangle { CornerRadius = horizontal ? 29 : 25 },
            HorizontalOptions = LayoutOptions.Center,
            VerticalOptions = LayoutOptions.Center,
            Content = Icons.Glyph(spec.Glyph, Colors.White, horizontal ? 32 : 28)
        };
        Label title = spec.TitleLabel ?? new Label
        {
            Text = spec.Title,
            TextColor = Colors.White,
            FontSize = horizontal ? 18 : 15,
            FontFamily = "OpenSansSemibold",
            MaxLines = 2,
            LineBreakMode = LineBreakMode.WordWrap,
            HorizontalTextAlignment = horizontal ? TextAlignment.Start : TextAlignment.Center
        };
        if (spec.TitleLabel is not null)
        {
            title.FontSize = horizontal ? 18 : 15;
            title.HorizontalTextAlignment = horizontal ? TextAlignment.Start : TextAlignment.Center;
        }

        View body;
        if (horizontal)
        {
            Grid grid = new()
            {
                ColumnSpacing = 14,
                ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)],
                VerticalOptions = LayoutOptions.Center
            };
            grid.Add(iconBubble, 0, 0);
            grid.Add(new VerticalStackLayout
            {
                Spacing = 2,
                VerticalOptions = LayoutOptions.Center,
                Children =
                {
                    title,
                    new Label { Text = spec.Subtitle, TextColor = Color.FromRgba(255, 255, 255, 210), FontSize = 13, MaxLines = 2, LineBreakMode = LineBreakMode.WordWrap }
                }
            }, 1, 0);
            grid.Add(Icons.Glyph(Icons.ChevronRight, Color.FromRgba(255, 255, 255, 180), 26), 2, 0);
            body = grid;
        }
        else
        {
            body = new VerticalStackLayout
            {
                Spacing = 8,
                VerticalOptions = LayoutOptions.Center,
                Children = { iconBubble, title }
            };
        }

        Grid layers = new()
        {
            Children =
            {
                new Ellipse { Fill = new SolidColorBrush(Color.FromRgba(255, 255, 255, 22)), WidthRequest = 130, HeightRequest = 130, HorizontalOptions = LayoutOptions.End, VerticalOptions = LayoutOptions.Start, Margin = new Thickness(0, -60, -50, 0) },
                new Ellipse { Fill = new SolidColorBrush(Color.FromRgba(255, 255, 255, 14)), WidthRequest = 80, HeightRequest = 80, HorizontalOptions = LayoutOptions.Start, VerticalOptions = LayoutOptions.End, Margin = new Thickness(-30, 0, 0, -40) },
                new ContentView { Padding = new Thickness(horizontal ? 16 : 10, horizontal ? 14 : 16), Content = body }
            }
        };

        Border tile = new()
        {
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromRgba(255, 255, 255, 40)),
            StrokeShape = new RoundRectangle { CornerRadius = 22 },
            MinimumHeightRequest = horizontal ? 96 : TileHeight,
            Background = Gradient(spec.Start, spec.End),
            Shadow = new Shadow { Brush = new SolidColorBrush(Color.FromArgb(spec.Start)), Opacity = 0.35f, Radius = 14, Offset = new Point(0, 6) },
            Content = layers
        };
        SemanticProperties.SetDescription(tile, spec.Title);
        AddTap(tile, () => spec.Open(this));
        return tile;
    }

    private Border Header()
    {
        Border logo = new()
        {
            WidthRequest = 54,
            HeightRequest = 54,
            Padding = 6,
            StrokeThickness = 0,
            BackgroundColor = Colors.White,
            StrokeShape = new RoundRectangle { CornerRadius = 27 },
            Content = new Image { Source = "arsan_logo.png", Aspect = Aspect.AspectFit }
        };
        Grid brand = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)]
        };
        brand.Add(logo, 0, 0);
        brand.Add(new VerticalStackLayout
        {
            VerticalOptions = LayoutOptions.Center,
            Children =
            {
                new Label { Text = "Arsan Gaz", TextColor = Colors.White, FontSize = 22, FontFamily = "OpenSansSemibold", CharacterSpacing = 0.4 },
                new Label { Text = "Endüstriyel ve medikal tüp takip sistemi", TextColor = Color.FromArgb("#BAE6FD"), FontSize = 13 }
            }
        }, 1, 0);

        Border avatar = new()
        {
            WidthRequest = 42,
            HeightRequest = 42,
            StrokeThickness = 0,
            Background = Gradient("#14B8A6", "#0EA5E9"),
            StrokeShape = new RoundRectangle { CornerRadius = 21 },
            Content = _initials
        };
        Grid user = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)]
        };
        user.Add(avatar, 0, 0);
        user.Add(new VerticalStackLayout { VerticalOptions = LayoutOptions.Center, Children = { _userName, _userRole } }, 1, 0);
        Border userCard = new()
        {
            Padding = new Thickness(12, 10),
            StrokeThickness = 1,
            Stroke = new SolidColorBrush(Color.FromRgba(255, 255, 255, 45)),
            BackgroundColor = Color.FromRgba(255, 255, 255, 26),
            StrokeShape = new RoundRectangle { CornerRadius = 18 },
            Content = user
        };

        return new Border
        {
            Padding = new Thickness(18, 22, 18, 18),
            StrokeThickness = 0,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(0, 0, 28, 28) },
            Background = new LinearGradientBrush(
                [
                    new GradientStop(Color.FromArgb("#0B1736"), 0),
                    new GradientStop(Color.FromArgb("#1E3A8A"), 0.55f),
                    new GradientStop(Color.FromArgb("#0F766E"), 1)
                ],
                new Point(0, 0), new Point(1, 1)),
            Content = new VerticalStackLayout { Spacing = 16, Children = { brand, userCard } }
        };
    }

    private View Footer()
    {
        Grid line = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)]
        };
        line.Add(new BoxView { HeightRequest = 2, CornerRadius = 1, VerticalOptions = LayoutOptions.Center, Color = Color.FromArgb("#14B8A6") }, 0, 0);
        line.Add(_date, 1, 0);
        line.Add(new BoxView { HeightRequest = 2, CornerRadius = 1, VerticalOptions = LayoutOptions.Center, Color = Color.FromArgb("#1E3A8A") }, 2, 0);
        Label version = Ui.Note($"Arsan Gaz ERP mobil {AppInfo.Current.VersionString} (build {AppInfo.Current.BuildString})");
        version.HorizontalTextAlignment = TextAlignment.Center;
        return new VerticalStackLayout { Spacing = 6, Children = { line, version } };
    }

    private async Task RefreshTripTitleAsync()
    {
        try
        {
            List<ActiveTripSummary> trips = await ApiClient.Instance.ActiveTripsAsync();
            _hasOpenTrip = trips.Count > 0;
        }
        catch (Exception exception) when (exception is ApiException or HttpRequestException or TaskCanceledException)
        {
            _hasOpenTrip = false;
        }

        _tripTitle.Text = _hasOpenTrip ? "Sevkiyata devam" : "Sevkiyat başlat";
    }

    private async Task OpenTripAsync()
    {
        await OpenAsync("Sefer", () => new CylinderTripPage());
        if (!_hasOpenTrip
            && ApiClient.Instance.Session?.Can("sefer-baslat") == true
            && _pages.GetValueOrDefault("Sefer") is CylinderTripPage page
            && Navigation.NavigationStack.LastOrDefault() == page)
        {
            await page.StartTripAsync();
        }

        await RefreshTripTitleAsync();
    }

    private async Task OpenAsync(string title, Func<Page> create)
    {
        if (!_pages.TryGetValue(title, out Page? page))
        {
            try
            {
                page = create();
                _pages[title] = page;
            }
            catch (Exception exception)
            {
                CrashLog.Write(exception, title + " ekranı");
                page = CrashLog.ErrorPage(title + " ekranı açılamadı", exception.ToString());
            }
        }

        await Navigation.PushAsync(page);
    }

    private void AddTap(View view, Func<Task> action)
    {
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) =>
        {
            if (_opening)
            {
                return;
            }

            _opening = true;
            try
            {
                await view.ScaleToAsync(0.94, 70, Easing.CubicOut);
                await view.ScaleToAsync(1, 90, Easing.CubicIn);
                await action();
            }
            catch (Exception exception)
            {
                await DisplayAlertAsync("Arsan Gaz ERP", exception.Message, "Tamam");
            }
            finally
            {
                _opening = false;
            }
        };
        view.GestureRecognizers.Add(tap);
    }

    private static LinearGradientBrush Gradient(string start, string end) =>
        new([new GradientStop(Color.FromArgb(start), 0), new GradientStop(Color.FromArgb(end), 1)], new Point(0, 0), new Point(1, 1));

    private static Grid Row(View icon, View text)
    {
        Grid grid = new()
        {
            ColumnSpacing = 12,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)]
        };
        grid.Add(icon, 0, 0);
        grid.Add(text, 1, 0);
        return grid;
    }

    private static string Initials(string name)
    {
        string[] parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length switch
        {
            0 => "?",
            1 => parts[0][..1].ToUpper(Turkish),
            _ => (parts[0][..1] + parts[^1][..1]).ToUpper(Turkish)
        };
    }
}
