using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed partial class AgentControlCenterWindow
{
    private readonly DataGrid _cursorJobGrid = CreateGrid();
    private readonly TextBlock _cursorJobSummary = MutedText();
    private DispatcherTimer? _cursorJobTimer;

    private UIElement BuildCursorJobs()
    {
        Grid layout = new() { Margin = new Thickness(10) };
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());

        StackPanel toolbar = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 10) };
        toolbar.Children.Add(Button("Seçili işi durdur", StopSelectedCursorJob));
        Button stopAll = Button("Acil durdur (tümü)", StopAllCursorJobs);
        stopAll.Background = new SolidColorBrush(Red);
        toolbar.Children.Add(stopAll);
        _cursorJobSummary.VerticalAlignment = VerticalAlignment.Center;
        _cursorJobSummary.Margin = new Thickness(8, 0, 0, 8);
        toolbar.Children.Add(_cursorJobSummary);
        layout.Children.Add(toolbar);

        _cursorJobGrid.Columns.Add(TextColumn("Zaman", nameof(CursorJobRow.Time), 140));
        _cursorJobGrid.Columns.Add(TextColumn("Kaynak", nameof(CursorJobRow.Source), 110));
        _cursorJobGrid.Columns.Add(TextColumn("Durum", nameof(CursorJobRow.Status), 120));
        _cursorJobGrid.Columns.Add(TextColumn("Süre", nameof(CursorJobRow.Duration), 80));
        _cursorJobGrid.Columns.Add(TextColumn("Son adım", nameof(CursorJobRow.LastStep), 260));
        DataGridTextColumn summary = TextColumn("Sonuç / açıklama", nameof(CursorJobRow.Summary), 520);
        summary.ElementStyle = new Style(typeof(TextBlock))
        {
            Setters =
            {
                new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap),
                new Setter(FrameworkElement.MarginProperty, new Thickness(4, 3, 4, 3))
            }
        };
        _cursorJobGrid.Columns.Add(summary);
        _cursorJobGrid.RowHeight = double.NaN;
        _cursorJobGrid.MinRowHeight = 34;
        _cursorJobGrid.SelectionMode = DataGridSelectionMode.Single;
        _cursorJobGrid.LoadingRow += (_, args) =>
        {
            if (args.Row.Item is CursorJobRow { NeedsAttention: true })
            {
                args.Row.Background = new SolidColorBrush(Color.FromArgb(48, Red.R, Red.G, Red.B));
            }
            else
            {
                args.Row.ClearValue(BackgroundProperty);
            }
        };
        Grid.SetRow(_cursorJobGrid, 1);
        layout.Children.Add(_cursorJobGrid);

        _cursorJobTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _cursorJobTimer.Tick += (_, _) => RefreshCursorJobs();
        layout.IsVisibleChanged += (_, args) =>
        {
            if (args.NewValue is true)
            {
                RefreshCursorJobs();
                _cursorJobTimer.Start();
            }
            else
            {
                _cursorJobTimer.Stop();
            }
        };
        Closed += (_, _) => _cursorJobTimer.Stop();
        return layout;
    }

    private void RefreshCursorJobs()
    {
        try
        {
            string? selectedId = (_cursorJobGrid.SelectedItem as CursorJobRow)?.Id;
            IReadOnlyList<CursorJob> jobs = CursorJobMonitor.Read(CursorJobMonitor.DefaultInbox, DateTime.UtcNow);
            List<CursorJobRow> rows = jobs.Select(job => new CursorJobRow(
                job.Id,
                job.CreatedLocal.ToString("dd.MM.yyyy HH:mm:ss", TurkishCulture),
                job.Source,
                job.NeedsAttention ? "⚠ " + job.Status : job.Status,
                job.Duration <= TimeSpan.Zero ? "—" : job.Duration.ToString(job.Duration.TotalHours >= 1 ? @"h\:mm\:ss" : @"m\:ss"),
                job.LastStep,
                job.Summary,
                job.NeedsAttention,
                job.IsActive)).ToList();
            _cursorJobGrid.ItemsSource = rows;
            _cursorJobGrid.SelectedItem = rows.FirstOrDefault(row => row.Id == selectedId);
            int active = jobs.Count(job => job.IsActive);
            int attention = jobs.Count(job => job.NeedsAttention);
            _cursorJobSummary.Text = $"Etkin: {active} · Dikkat gereken: {attention} · Son {jobs.Count} iş · {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception exception)
        {
            _cursorJobSummary.Text = "Cursor işleri okunamadı: " + exception.GetBaseException().Message;
        }
    }

    private void StopSelectedCursorJob()
    {
        if (_cursorJobGrid.SelectedItem is not CursorJobRow row)
        {
            _cursorJobSummary.Text = "Önce listeden durdurulacak işi seçin.";
            return;
        }

        if (!row.IsActive)
        {
            _cursorJobSummary.Text = "Seçili iş zaten bitmiş.";
            return;
        }

        if (MessageBox.Show(this, $"{row.Source} işi durdurulsun mu?\n\n{row.Id}", "Cursor işini durdur",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _cursorJobSummary.Text = CursorJobMonitor.RequestCancel(CursorJobMonitor.DefaultInbox, row.Id)
            ? "Durdurma isteği gönderildi; köprü birkaç saniye içinde işi durduracak."
            : "Bu iş durdurulamıyor (bitmiş veya IDE sohbetinde çalışıyor).";
        RefreshCursorJobs();
    }

    private void StopAllCursorJobs()
    {
        if (MessageBox.Show(this, "Çalışan ve kuyrukta bekleyen tüm Cursor işleri durdurulsun mu?", "Acil durdurma",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        int count = CursorJobMonitor.CancelAllActive(CursorJobMonitor.DefaultInbox, DateTime.UtcNow);
        _cursorJobSummary.Text = count == 0 ? "Durdurulacak etkin iş yok." : $"{count} iş için durdurma isteği gönderildi.";
        RefreshCursorJobs();
    }

    private sealed record CursorJobRow(
        string Id,
        string Time,
        string Source,
        string Status,
        string Duration,
        string LastStep,
        string Summary,
        bool NeedsAttention,
        bool IsActive);
}
