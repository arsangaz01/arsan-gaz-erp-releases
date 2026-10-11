using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Views;

public sealed partial class AgentControlCenterWindow : Window
{
    private const int ActivityWindowDays = 30;
    private static string QueueDirectory => AgentWatcherPaths.QueueDirectory;
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly Color Accent = Color.FromRgb(0, 120, 168);
    private static readonly Color Green = Color.FromRgb(43, 227, 160);
    private static readonly Color Red = Color.FromRgb(255, 84, 104);

    private readonly TextBlock _checkedAt = MutedText();
    private readonly TextBlock _totalRuns = MetricText();
    private readonly TextBlock _successRate = MetricText();
    private readonly TextBlock _pendingCount = MetricText();
    private readonly TextBlock _criticalCount = MetricText();
    private readonly StackPanel _agentChart = new();
    private readonly StackPanel _trendChart = new();
    private readonly StackPanel _healthChecks = new();
    private readonly StackPanel _recommendations = new();
    private readonly DataGrid _agentGrid = CreateGrid();
    private readonly DataGrid _activityGrid = CreateGrid();
    private readonly TextBlock _activitySummary = MutedText();
    private readonly TextBlock _activityEmptyState = MutedText();
    private readonly TextBlock _status = MutedText();
    private readonly ComboBox _agentFilter = new() { MinWidth = 170, Padding = new Thickness(6) };
    private List<ActivityRecord> _activities = [];
    private SupervisorReport? _healthReport;

    public AgentControlCenterWindow()
    {
        Title = "Arsan Gaz ERP — Ajan Kontrol Merkezi";
        Width = 1420;
        Height = 920;
        MinWidth = Math.Min(1080, Width);
        MinHeight = Math.Min(700, Height);
        WindowWorkAreaService.Fit(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrush("CanvasBrush");
        Content = BuildLayout();
        Loaded += async (_, _) => await RefreshAsync();
    }

    private UIElement BuildLayout()
    {
        Grid root = new() { Margin = new Thickness(22) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());

        DockPanel header = new() { Margin = new Thickness(0, 0, 0, 18) };
        Button cloudSettings = Button("Ajan Yetkileri / Cloudflare", OpenCloudAgentSettingsAsync);
        DockPanel.SetDock(cloudSettings, Dock.Right);
        header.Children.Add(cloudSettings);
        Button refresh = Button("Yenile", RefreshAsync);
        DockPanel.SetDock(refresh, Dock.Right);
        header.Children.Add(refresh);
        StackPanel title = new();
        title.Children.Add(new TextBlock
        {
            Text = "Ajan Kontrol Merkezi",
            FontSize = 27,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeBrush("InkBrush")
        });
        title.Children.Add(new TextBlock
        {
            Text = "Ajan faaliyetleri, görev sonuçları, sistem sağlığı ve iyileştirme önerileri",
            Margin = new Thickness(0, 5, 0, 0),
            Foreground = ThemeBrush("MutedBrush")
        });
        header.Children.Add(title);
        root.Children.Add(header);

        UniformGrid metrics = new() { Columns = 4, Margin = new Thickness(-5, 0, -5, 14) };
        metrics.Children.Add(MetricCard("Son 30 gün · işlenen işler", _totalRuns, Accent));
        metrics.Children.Add(MetricCard("Başarı oranı", _successRate, Green));
        metrics.Children.Add(MetricCard("Bekleyen / çalışan", _pendingCount, Accent));
        metrics.Children.Add(MetricCard("Kritik bulgu", _criticalCount, Red));
        Grid.SetRow(metrics, 1);
        root.Children.Add(metrics);

        TabControl tabs = new();
        tabs.Items.Add(new TabItem { Header = "Genel bakış", Content = BuildOverview() });
        tabs.Items.Add(new TabItem { Header = "Ajanlar", Content = BuildAgents() });
        tabs.Items.Add(new TabItem { Header = "Görev günlüğü", Content = BuildActivity() });
        tabs.Items.Add(new TabItem { Header = "Cursor işleri", Content = BuildCursorJobs() });
        Grid.SetRow(tabs, 2);
        root.Children.Add(tabs);
        return root;
    }

    private async Task OpenCloudAgentSettingsAsync()
    {
        CloudAgentSettingsWindow settingsWindow = new() { Owner = this };
        settingsWindow.ShowDialog();
        await RefreshAsync();
    }

    private UIElement BuildOverview()
    {
        Grid overview = new() { Margin = new Thickness(10) };
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.15, GridUnitType.Star) });
        overview.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.85, GridUnitType.Star) });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        overview.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Border agentCard = Card();
        StackPanel agentContent = new() { Margin = new Thickness(16) };
        agentContent.Children.Add(SectionTitle("Ajan bazında görev başarısı"));
        agentContent.Children.Add(new TextBlock
        {
            Text = "Son 30 gündeki tamamlanmış işlere göre başarı oranı",
            Foreground = ThemeBrush("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        agentContent.Children.Add(_agentChart);
        agentCard.Child = agentContent;
        overview.Children.Add(agentCard);

        Border trendCard = Card();
        StackPanel trendContent = new() { Margin = new Thickness(16) };
        trendContent.Children.Add(SectionTitle("Günlük görev hacmi"));
        trendContent.Children.Add(new TextBlock
        {
            Text = "Son 7 gün · yeşil: başarılı · kırmızı: başarısız",
            Foreground = ThemeBrush("MutedBrush"),
            Margin = new Thickness(0, 0, 0, 10)
        });
        trendContent.Children.Add(_trendChart);
        Grid.SetColumn(trendCard, 1);
        trendCard.Child = trendContent;
        overview.Children.Add(trendCard);

        Border healthCard = Card();
        StackPanel healthContent = new() { Margin = new Thickness(16) };
        healthContent.Children.Add(SectionTitle("Gözcü · sistem sağlığı"));
        healthContent.Children.Add(_healthChecks);
        Grid.SetRow(healthCard, 1);
        Grid.SetColumnSpan(healthCard, 2);
        healthCard.Child = healthContent;
        overview.Children.Add(healthCard);

        Border recommendationCard = Card();
        StackPanel recommendationContent = new() { Margin = new Thickness(16) };
        DockPanel recommendationHeader = new();
        Button chat = Button("Ajanlara danış", () =>
        {
            new AgentChatWindow { Owner = this }.ShowDialog();
        });
        DockPanel.SetDock(chat, Dock.Right);
        recommendationHeader.Children.Add(chat);
        recommendationHeader.Children.Add(SectionTitle("Geliştirme önerileri"));
        recommendationContent.Children.Add(recommendationHeader);
        recommendationContent.Children.Add(_recommendations);
        Grid.SetRow(recommendationCard, 2);
        Grid.SetColumnSpan(recommendationCard, 2);
        recommendationCard.Child = recommendationContent;
        overview.Children.Add(recommendationCard);

        Border actionCard = Card();
        StackPanel actionContent = new() { Margin = new Thickness(16) };
        actionContent.Children.Add(SectionTitle("Ajanlara yeni görev ver"));
        WrapPanel actions = new() { Margin = new Thickness(0, 10, 0, 0) };
        actions.Children.Add(Button("CRM analizi", () => QueueTaskAsync("CRM Analizi", "CRM fırsatları ve gecikmiş takipler için özet analiz hazırla.")));
        actions.Children.Add(Button("Satış analizi", () => QueueTaskAsync("Satış Tahmini", "Satış verilerine dayalı, varsayımları açıkça belirten tahmin ve takip önerisi hazırla.")));
        actions.Children.Add(Button("İş planı", () => QueueTaskAsync("Görev Planlama", "Açık ERP görevleri, teklif takipleri ve gecikmiş faturalar için önceliklendirilmiş plan hazırla.")));
        actionContent.Children.Add(actions);
        _checkedAt.Margin = new Thickness(0, 8, 0, 0);
        actionContent.Children.Add(_checkedAt);
        _status.Margin = new Thickness(0, 4, 0, 0);
        actionContent.Children.Add(_status);
        Grid.SetRow(actionCard, 3);
        Grid.SetColumnSpan(actionCard, 2);
        actionCard.Child = actionContent;
        overview.Children.Add(actionCard);
        return new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = overview };
    }

    private UIElement BuildAgents()
    {
        ConfigureAgentGrid();
        return new Border
        {
            Margin = new Thickness(10),
            Padding = new Thickness(8),
            Background = ThemeBrush("SurfaceBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = _agentGrid
        };
    }

    private UIElement BuildActivity()
    {
        Grid layout = new() { Margin = new Thickness(10) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(new TextBlock { Text = "Ajan:", VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) });
        _agentFilter.Items.Add("Tüm ajanlar");
        foreach (ErpAgentProfile profile in AgentChatService.Profiles)
        {
            _agentFilter.Items.Add(profile.Name);
        }
        _agentFilter.Items.Add("Gözcü");
        _agentFilter.Items.Add("ERP Denetim");
        _agentFilter.SelectedIndex = 0;
        _agentFilter.SelectionChanged += (_, _) => ApplyActivityFilter();
        toolbar.Children.Add(_agentFilter);
        _activitySummary.Margin = new Thickness(12, 0, 0, 0);
        _activitySummary.VerticalAlignment = VerticalAlignment.Center;
        toolbar.Children.Add(_activitySummary);
        Grid.SetRow(toolbar, 0);
        layout.Children.Add(toolbar);

        _activityEmptyState.Text = "Bu filtre için gösterilecek görev yok. Ajan filtresini değiştirin veya Yenile'yi seçin.";
        _activityEmptyState.Margin = new Thickness(10, 4, 10, 12);
        _activityEmptyState.TextWrapping = TextWrapping.Wrap;
        _activityEmptyState.Visibility = Visibility.Collapsed;
        Grid.SetRow(_activityEmptyState, 1);
        layout.Children.Add(_activityEmptyState);

        _activityGrid.Columns.Add(TextColumn("Zaman", nameof(ActivityRecord.DisplayTime), 150));
        _activityGrid.Columns.Add(TextColumn("Ajan", nameof(ActivityRecord.Agent), 110));
        _activityGrid.Columns.Add(TextColumn("Görev", nameof(ActivityRecord.Title), 260));
        _activityGrid.Columns.Add(TextColumn("Durum", nameof(ActivityRecord.Status), 110));
        _activityGrid.Columns.Add(TextColumn("Süre", nameof(ActivityRecord.DurationText), 90));
        _activityGrid.RowHeight = double.NaN;
        _activityGrid.MinRowHeight = 34;
        ScrollViewer.SetHorizontalScrollBarVisibility(_activityGrid, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_activityGrid, ScrollBarVisibility.Auto);
        DataGridTextColumn summaryColumn = TextColumn("Sonuç / açıklama", nameof(ActivityRecord.Summary), 560);
        summaryColumn.ElementStyle = new Style(typeof(TextBlock))
        {
            Setters =
            {
                new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Center),
                new Setter(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3))
            }
        };
        _activityGrid.Columns.Add(summaryColumn);
        Grid.SetRow(_activityGrid, 2);
        layout.Children.Add(_activityGrid);
        return layout;
    }

    private async Task RefreshAsync()
    {
        _status.Text = "Veriler yenileniyor…";
        try
        {
            await using ArsanGazDbContext db = new();
            List<AgentRun> runs = await db.AgentRuns
                .AsNoTracking()
                .OrderByDescending(run => run.StartedAtUtc)
                .Take(1000)
                .ToListAsync();

            _activities = runs.Select(ToActivity).ToList();
            List<string> unreadableTasks = [];
            _activities.AddRange(ReadWatcherActivities(unreadableTasks));
            _healthReport = await SystemSupervisorService.ReadLatestReportAsync();

            UpdateMetrics();
            UpdateAgentChart();
            UpdateTrendChart();
            UpdateHealthChecks();
            UpdateRecommendations(unreadableTasks, runs.Count);
            UpdateAgentGrid();
            ApplyActivityFilter();
            _checkedAt.Text = _healthReport is null
                ? $"Son yenileme: {DateTime.Now:dd.MM.yyyy HH:mm:ss} · Gözcü raporu henüz oluşmadı."
                : $"Son yenileme: {DateTime.Now:dd.MM.yyyy HH:mm:ss} · Son sistem kontrolü: {_healthReport.CheckedAtLocal:dd.MM.yyyy HH:mm:ss}";
            _status.Text = "Gösterilen metrikler yerel ajan çalışma kayıtları ve watcher görev dosyalarından hesaplandı.";
        }
        catch (Exception exception)
        {
            _status.Text = "Kontrol merkezi yenilenemedi: " + exception.GetBaseException().Message;
        }
    }

    private void UpdateMetrics()
    {
        DateTime cutoff = DateTime.Now.AddDays(-ActivityWindowDays);
        List<ActivityRecord> recent = _activities.Where(activity => activity.Timestamp >= cutoff).ToList();
        List<ActivityRecord> completed = recent.Where(activity => activity.IsCompleted).ToList();
        int succeeded = completed.Count(activity => activity.Succeeded);
        int failed = completed.Count - succeeded;
        int pending = recent.Count(activity => !activity.IsCompleted && activity.Status is "Bekliyor" or "Çalışıyor");
        int critical = recent.Sum(activity => activity.CriticalCount);

        _totalRuns.Text = recent.Count.ToString("N0", TurkishCulture);
        _successRate.Text = completed.Count == 0
            ? "—"
            : (succeeded * 100d / completed.Count).ToString("N0", TurkishCulture) + "%";
        _pendingCount.Text = pending.ToString("N0", TurkishCulture);
        _criticalCount.Text = critical.ToString("N0", TurkishCulture);
        _criticalCount.Foreground = new SolidColorBrush(critical > 0 ? Red : Green);
    }

    private void UpdateAgentChart()
    {
        _agentChart.Children.Clear();
        DateTime cutoff = DateTime.Now.AddDays(-ActivityWindowDays);
        List<ActivityRecord> recent = _activities
            .Where(activity => activity.Timestamp >= cutoff && activity.IsCompleted)
            .ToList();

        foreach ((string name, _) in GetAgentDefinitions().Where(item => item.Name != "Gözcü"))
        {
            List<ActivityRecord> agentRuns = recent.Where(activity => activity.Agent == name).ToList();
            int succeeded = agentRuns.Count(activity => activity.Succeeded);
            double rate = agentRuns.Count == 0 ? 0 : succeeded * 100d / agentRuns.Count;

            Grid row = new() { Margin = new Thickness(0, 5, 0, 5) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.SemiBold });
            ProgressBar bar = new()
            {
                Minimum = 0,
                Maximum = 100,
                Value = rate,
                Height = 15,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(rate < 70 && agentRuns.Count > 0 ? Red : Green),
                Background = ThemeBrush("TableHeaderBrush")
            };
            Grid.SetColumn(bar, 1);
            row.Children.Add(bar);
            TextBlock count = new()
            {
                Text = agentRuns.Count == 0 ? "Henüz görev yok" : $"{rate:N0}% · {succeeded}/{agentRuns.Count}",
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("MutedBrush"),
                Margin = new Thickness(8, 0, 0, 0)
            };
            Grid.SetColumn(count, 2);
            row.Children.Add(count);
            _agentChart.Children.Add(row);
        }
    }

    private void UpdateTrendChart()
    {
        _trendChart.Children.Clear();
        DateTime firstDay = DateTime.Today.AddDays(-6);
        List<(DateTime Day, int Success, int Failure)> daily = Enumerable.Range(0, 7)
            .Select(offset =>
            {
                DateTime day = firstDay.AddDays(offset);
                List<ActivityRecord> dayRuns = _activities
                    .Where(activity => activity.IsCompleted && activity.Timestamp.Date == day)
                    .ToList();
                return (day, dayRuns.Count(run => run.Succeeded), dayRuns.Count(run => !run.Succeeded));
            })
            .ToList();
        int max = Math.Max(1, daily.Max(day => day.Success + day.Failure));

        foreach ((DateTime day, int success, int failure) in daily)
        {
            Grid row = new() { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(72) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(55) });
            row.Children.Add(new TextBlock
            {
                Text = day.ToString("ddd dd", TurkishCulture),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("MutedBrush")
            });
            Grid barGrid = new() { Height = 16, VerticalAlignment = VerticalAlignment.Center };
            double scale = 100d / max;
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(success * scale, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(failure * scale, GridUnitType.Star) });
            barGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((max - success - failure) * scale, GridUnitType.Star) });
            if (success > 0)
            {
                barGrid.Children.Add(new Border { Background = new SolidColorBrush(Green), CornerRadius = new CornerRadius(3) });
            }
            if (failure > 0)
            {
                Border failedBar = new() { Background = new SolidColorBrush(Red), CornerRadius = new CornerRadius(3) };
                Grid.SetColumn(failedBar, 1);
                barGrid.Children.Add(failedBar);
            }
            Grid.SetColumn(barGrid, 1);
            row.Children.Add(barGrid);
            TextBlock total = new()
            {
                Text = (success + failure).ToString(TurkishCulture),
                TextAlignment = TextAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = ThemeBrush("MutedBrush")
            };
            Grid.SetColumn(total, 2);
            row.Children.Add(total);
            _trendChart.Children.Add(row);
        }
    }

    private void UpdateRecommendations(IReadOnlyList<string> unreadableTasks, int databaseRunCount)
    {
        _recommendations.Children.Clear();
        List<string> advice = [];

        if (_healthReport is null)
        {
            advice.Add("Gözcü raporu henüz yok. ERP oturumunu açık tutup ilk sağlık kontrolünün tamamlanmasını bekleyin.");
        }
        else
        {
            advice.AddRange(_healthReport.Checks
                .Where(check => !check.IsHealthy)
                .Select(check => $"{check.Name}: {check.Details} Çözüm: bağlantı/ayar durumunu ilgili ERP ekranından inceleyin; Gözcü otomatik değişiklik yapmaz."));
        }

        List<ActivityRecord> completed = _activities
            .Where(activity => activity.IsCompleted && activity.Timestamp >= DateTime.Now.AddDays(-ActivityWindowDays))
            .ToList();
        if (completed.Count >= 5)
        {
            double successRate = completed.Count(activity => activity.Succeeded) * 100d / completed.Count;
            if (successRate < 85)
            {
                advice.Add($"Son 30 günlük görev başarı oranı %{successRate:N0}. Başarısız işlerin raporlarını açıp hata türüne göre tekrar deneyin; tekrarı otomatik başlatmayın.");
            }
        }

        int critical = completed.Sum(activity => activity.CriticalCount);
        if (critical > 0)
        {
            advice.Add($"{critical} kritik bulgu kaydedilmiş. Finansal veya operasyonel işlem yapmadan önce ilgili kayıtları yetkili kullanıcıyla doğrulayın.");
        }

        if (completed.Count == 0 && databaseRunCount == 0)
        {
            advice.Add("Henüz tamamlanmış ajan işi yok. Test için CRM analizi gibi salt-okunur bir görev çalıştırıp ilk başarı ölçümünü oluşturun.");
        }

        advice.AddRange(unreadableTasks.Select(message => "Watcher görev kaydı okunamadı: " + message));
        if (advice.Count == 0)
        {
            advice.Add("İzlenen ölçümlerde öncelikli bir aksaklık yok. Ajan görevlerini haftalık başarı oranı ve tekrar eden kritik bulgulara göre değerlendirin.");
        }

        foreach (string item in advice)
        {
            Border recommendation = new()
            {
                Margin = new Thickness(0, 5, 0, 5),
                Padding = new Thickness(11, 9, 11, 9),
                Background = ThemeBrush("SurfaceBrush"),
                BorderBrush = ThemeBrush("BorderBrush"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Child = new TextBlock { Text = item, TextWrapping = TextWrapping.Wrap }
            };
            _recommendations.Children.Add(recommendation);
        }
    }

    private void UpdateHealthChecks()
    {
        _healthChecks.Children.Clear();
        if (_healthReport is null)
        {
            _healthChecks.Children.Add(new TextBlock
            {
                Text = "Henüz sağlık raporu yok; Gözcü ilk kontrolü tamamladıktan sonra durumlar burada görünür.",
                Foreground = ThemeBrush("MutedBrush"),
                TextWrapping = TextWrapping.Wrap
            });
            return;
        }

        foreach (SupervisorCheck check in _healthReport.Checks)
        {
            Grid row = new() { Margin = new Thickness(0, 4, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(160) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(110) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(new TextBlock
            {
                Text = check.Name,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            });
            TextBlock state = new()
            {
                Text = check.IsHealthy ? "Sağlıklı" : "İnceleme gerekli",
                Foreground = new SolidColorBrush(check.IsHealthy ? Green : Red),
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(state, 1);
            row.Children.Add(state);
            TextBlock details = new()
            {
                Text = check.Details,
                Foreground = ThemeBrush("MutedBrush"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetColumn(details, 2);
            row.Children.Add(details);
            _healthChecks.Children.Add(row);
        }
    }

    private void UpdateAgentGrid()
    {
        DateTime cutoff = DateTime.Now.AddDays(-ActivityWindowDays);
        List<ActivityRecord> recent = _activities.Where(activity => activity.Timestamp >= cutoff).ToList();
        _agentGrid.ItemsSource = GetAgentDefinitions()
            .Select(definition =>
            {
                if (definition.Name == "Gözcü")
                {
                    int checks = _healthReport?.Checks.Count ?? 0;
                    int healthy = _healthReport?.Checks.Count(check => check.IsHealthy) ?? 0;
                    return new AgentSummaryRow(
                        definition.Name,
                        definition.Role,
                        checks,
                        checks,
                        healthy,
                        checks - healthy,
                        checks == 0 ? "—" : (healthy * 100d / checks).ToString("N0", TurkishCulture) + "%",
                        _healthReport?.CheckedAtLocal.ToString("dd.MM.yyyy HH:mm", TurkishCulture) ?? "Henüz rapor yok");
                }

                List<ActivityRecord> items = recent.Where(activity => activity.Agent == definition.Name).ToList();
                List<ActivityRecord> finished = items.Where(activity => activity.IsCompleted).ToList();
                int successes = finished.Count(activity => activity.Succeeded);
                string rate = finished.Count == 0 ? "—" : (successes * 100d / finished.Count).ToString("N0", TurkishCulture) + "%";
                DateTime? last = items.Count == 0 ? null : items.Max(activity => activity.Timestamp);
                return new AgentSummaryRow(
                    definition.Name,
                    definition.Role,
                    items.Count,
                    finished.Count,
                    successes,
                    finished.Count - successes,
                    rate,
                    last?.ToString("dd.MM.yyyy HH:mm", TurkishCulture) ?? "Henüz çalışmadı");
            })
            .ToList();
    }

    private void ConfigureAgentGrid()
    {
        _agentGrid.Columns.Add(TextColumn("Ajan", nameof(AgentSummaryRow.Name), 130));
        _agentGrid.Columns.Add(TextColumn("Uzmanlık / sorumluluk", nameof(AgentSummaryRow.Role), 290));
        _agentGrid.Columns.Add(TextColumn("Toplam", nameof(AgentSummaryRow.Total), 90));
        _agentGrid.Columns.Add(TextColumn("Tamamlandı", nameof(AgentSummaryRow.Completed), 110));
        _agentGrid.Columns.Add(TextColumn("Başarılı", nameof(AgentSummaryRow.Successes), 90));
        _agentGrid.Columns.Add(TextColumn("Başarısız", nameof(AgentSummaryRow.Failures), 95));
        _agentGrid.Columns.Add(TextColumn("Başarı oranı", nameof(AgentSummaryRow.SuccessRate), 110));
        _agentGrid.Columns.Add(TextColumn("Son aktivite", nameof(AgentSummaryRow.LastActivity), 160));
    }

    private void ApplyActivityFilter()
    {
        string selected = _agentFilter.SelectedItem as string ?? "Tüm ajanlar";
        List<ActivityRecord> matching = _activities
            .Where(activity => selected == "Tüm ajanlar" || activity.Agent == selected)
            .OrderByDescending(activity => activity.Timestamp)
            .ToList();
        List<ActivityRecord> visible = matching.Take(500).ToList();
        _activityGrid.ItemsSource = visible;
        _activitySummary.Text = matching.Count > visible.Count
            ? $"İlk {visible.Count:N0} / {matching.Count:N0} kayıt"
            : $"{matching.Count:N0} kayıt";
        _activityEmptyState.Visibility = matching.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static ActivityRecord ToActivity(AgentRun run)
    {
        string agent = GetAgentDefinitions()
            .Select(definition => definition.Name)
            .FirstOrDefault(name => run.Summary.StartsWith(name + ":", StringComparison.OrdinalIgnoreCase))
            ?? "ERP Denetim";
        string summary = run.Summary.StartsWith(agent + ":", StringComparison.OrdinalIgnoreCase)
            ? run.Summary[(agent.Length + 1)..].Trim()
            : run.Summary;
        double duration = Math.Max(0, (run.CompletedAtUtc - run.StartedAtUtc).TotalSeconds);
        DateTime timestamp = (run.CompletedAtUtc == default ? run.StartedAtUtc : run.CompletedAtUtc).ToLocalTime();
        return new ActivityRecord(
            timestamp,
            agent,
            agent == "ERP Denetim" ? "ERP sağlık/işletim analizi" : "ERP sohbeti",
            run.Succeeded,
            run.Succeeded ? "Başarılı" : "Başarısız",
            summary,
            duration,
            true,
            run.CriticalCount,
            run.FindingCount);
    }

    private static List<ActivityRecord> ReadWatcherActivities(List<string> warnings)
    {
        List<ActivityRecord> records = [];
        if (!Directory.Exists(QueueDirectory))
        {
            return records;
        }

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(QueueDirectory, "*.json").ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            warnings.Add(exception.GetBaseException().Message);
            return records;
        }

        foreach (string file in files)
        {
            try
            {
                WatcherTask? item = JsonSerializer.Deserialize<WatcherTask>(
                    File.ReadAllText(file),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                if (item is null)
                {
                    warnings.Add(Path.GetFileName(file) + " içeriği boş.");
                    continue;
                }

                string extension = Path.GetExtension(Path.GetFileNameWithoutExtension(file));
                bool failed = extension.Equals(".error", StringComparison.OrdinalIgnoreCase);
                bool succeeded = extension.Equals(".done", StringComparison.OrdinalIgnoreCase) ||
                                 item.Status.Equals("Tamamlandı", StringComparison.OrdinalIgnoreCase);
                bool completed = failed || succeeded;
                string status = failed ? "Başarısız" : item.Status;
                string agent = MapTaskToAgent(item.Type);
                DateTime at = item.Completed ?? item.Started ?? item.Created;
                double duration = item.Started is DateTime started && item.Completed is DateTime completedAt
                    ? Math.Max(0, (completedAt - started).TotalSeconds)
                    : 0;
                string subtaskSummary = item.AgentSubtasks.Count == 0
                    ? item.Error ?? item.Title
                    : string.Join(
                        " · ",
                        item.AgentSubtasks.Select(task => $"{task.AgentName}: {task.Status}"));
                records.Add(new ActivityRecord(
                    at,
                    agent,
                    string.IsNullOrWhiteSpace(item.Type) ? item.Title : item.Type,
                    succeeded,
                    completed ? status : status + " · beklemede",
                    ReadWatcherReportSummary(item.Report) ?? subtaskSummary,
                    duration,
                    completed,
                    0,
                    0));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            {
                warnings.Add($"{Path.GetFileName(file)} ({exception.GetBaseException().Message})");
            }
        }

        return records;
    }

    private static string? ReadWatcherReportSummary(string? reportPath)
    {
        if (string.IsNullOrWhiteSpace(reportPath))
        {
            return null;
        }

        try
        {
            string reportsRoot = Path.GetFullPath(AgentWatcherPaths.ReportsDirectory)
                .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(reportPath);
            if (!fullPath.StartsWith(reportsRoot, StringComparison.OrdinalIgnoreCase) || !File.Exists(fullPath))
            {
                return "Rapor yolu geçersiz veya rapor dosyası bulunamadı.";
            }

            using FileStream stream = new(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using StreamReader reader = new(stream);
            char[] buffer = new char[4000];
            int read = reader.ReadBlock(buffer, 0, buffer.Length);
            string text = new(buffer, 0, read);
            return text.Length == buffer.Length ? text + "…" : text;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return "Rapor okunamadı: " + exception.GetBaseException().Message;
        }
    }

    private static string MapTaskToAgent(string type)
    {
        if (type.Contains("Gözcü", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Ajan Ekibi", StringComparison.OrdinalIgnoreCase))
        {
            return "Gözcü";
        }

        if (type.Contains("CRM", StringComparison.OrdinalIgnoreCase))
        {
            return "Defne";
        }
        if (type.Contains("Satış", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Tahmin", StringComparison.OrdinalIgnoreCase))
        {
            return "Mert";
        }
        if (type.Contains("Finans", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Fatura", StringComparison.OrdinalIgnoreCase))
        {
            return "Ece";
        }
        if (type.Contains("Görev", StringComparison.OrdinalIgnoreCase) ||
            type.Contains("Plan", StringComparison.OrdinalIgnoreCase))
        {
            return "Can";
        }

        return "ERP Denetim";
    }

    private static IReadOnlyList<(string Name, string Role)> GetAgentDefinitions() =>
    [
        ("Defne", "CRM, firma ilişkileri ve gecikmiş takipler"),
        ("Mert", "Satış fırsatları, teklif hattı ve tahmin"),
        ("Ece", "Teklif, fatura ve tahsilat analizi; işlem yapmaz"),
        ("Can", "Görevleri önceliklendirme ve günlük iş planı"),
        ("Gözcü", "Yerel sistem sağlığı, uzmanlara görev dağıtımı ve sonuç denetimi"),
        ("ERP Denetim", "ERP bulguları ve AgentWatcher görevleri")
    ];

    private async Task QueueTaskAsync(string type, string title)
    {
        try
        {
            Directory.CreateDirectory(QueueDirectory);
            string id = Guid.NewGuid().ToString("N");
            WatcherTask task = new()
            {
                Id = id,
                Title = title,
                Type = type,
                Priority = "Normal",
                Provider = "NVIDIA NIM",
                Status = "Bekliyor",
                Created = DateTime.Now
            };
            string path = Path.Combine(QueueDirectory, id + ".task.json");
            await File.WriteAllTextAsync(
                path,
                JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }));
            _status.Text = $"Görev kuyruğa eklendi: {type}. AgentWatcher işlemesi bekleniyor.";
            await RefreshAsync();
        }
        catch (Exception exception)
        {
            _status.Text = "Görev kuyruğa eklenemedi: " + exception.GetBaseException().Message;
        }
    }

    private static DataGrid CreateGrid() => new()
    {
        AutoGenerateColumns = false,
        IsReadOnly = true,
        CanUserAddRows = false,
        CanUserDeleteRows = false,
        CanUserReorderColumns = true,
        CanUserSortColumns = true,
        HeadersVisibility = DataGridHeadersVisibility.Column,
        GridLinesVisibility = DataGridGridLinesVisibility.Horizontal,
        AlternationCount = 2,
        RowHeight = 34,
        ColumnHeaderHeight = 38,
        Background = ThemeBrush("SurfaceBrush"),
        BorderThickness = new Thickness(0)
    };

    private static DataGridTextColumn TextColumn(string header, string binding, double width) =>
        new()
        {
            Header = header,
            Binding = new System.Windows.Data.Binding(binding),
            Width = new DataGridLength(width),
            IsReadOnly = true
        };

    private static Border MetricCard(string title, TextBlock value, Color color)
    {
        StackPanel content = new() { Margin = new Thickness(15) };
        content.Children.Add(new TextBlock { Text = title, Foreground = ThemeBrush("MutedBrush"), FontWeight = FontWeights.SemiBold });
        value.Foreground = new SolidColorBrush(color);
        value.Margin = new Thickness(0, 7, 0, 0);
        content.Children.Add(value);
        return new Border
        {
            Child = content,
            Margin = new Thickness(5),
            Background = ThemeBrush("SurfaceBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
    }

    private static Border Card() => new()
    {
        Margin = new Thickness(6),
        Background = ThemeBrush("SurfaceBrush"),
        BorderBrush = ThemeBrush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8)
    };

    private static TextBlock SectionTitle(string text) => new()
    {
        Text = text,
        FontSize = 16,
        FontWeight = FontWeights.Bold,
        Foreground = ThemeBrush("InkBrush"),
        Margin = new Thickness(0, 0, 0, 6)
    };

    private static TextBlock MetricText() => new() { FontSize = 27, FontWeight = FontWeights.Bold };
    private static TextBlock MutedText() => new() { Foreground = ThemeBrush("MutedBrush"), TextWrapping = TextWrapping.Wrap };

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);

    private static Button Button(string text, Action action)
    {
        Button button = new()
        {
            Content = text,
            Padding = new Thickness(13, 8, 13, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Background = new SolidColorBrush(Accent),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Button(string text, Func<Task> action)
    {
        Button button = new()
        {
            Content = text,
            Padding = new Thickness(13, 8, 13, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Background = new SolidColorBrush(Accent),
            Foreground = Brushes.White,
            BorderThickness = new Thickness(0),
            FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand
        };
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private sealed record AgentSummaryRow(
        string Name,
        string Role,
        int Total,
        int Completed,
        int Successes,
        int Failures,
        string SuccessRate,
        string LastActivity);

    private sealed record ActivityRecord(
        DateTime Timestamp,
        string Agent,
        string Title,
        bool Succeeded,
        string Status,
        string Summary,
        double DurationSeconds,
        bool IsCompleted,
        int CriticalCount,
        int FindingCount)
    {
        public string DisplayTime => Timestamp.ToString("dd.MM.yyyy HH:mm:ss", TurkishCulture);
        public string DurationText => DurationSeconds <= 0 ? "—" : TimeSpan.FromSeconds(DurationSeconds).ToString(@"hh\:mm\:ss");
    }

    private sealed class WatcherTask
    {
        public string Id { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty;
        public string Priority { get; set; } = string.Empty;
        public string Provider { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public DateTime Created { get; set; }
        public DateTime? Started { get; set; }
        public DateTime? Completed { get; set; }
        public string? Report { get; set; }
        public string? Error { get; set; }
        public List<WatcherSubtask> AgentSubtasks { get; set; } = [];
    }

    private sealed class WatcherSubtask
    {
        public string AgentName { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
    }
}
