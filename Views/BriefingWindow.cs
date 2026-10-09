using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed class BriefingWindow : Window
{
    private readonly TextBox _city = new() { Text = DailyBriefingService.DefaultCity, Width = 160, Padding = new Thickness(6, 4, 6, 4) };
    private readonly Button _refresh = new() { Content = "Yenile", Padding = new Thickness(14, 6, 14, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { Margin = new Thickness(0, 8, 0, 8), TextWrapping = TextWrapping.Wrap };
    private readonly WrapPanel _cards = new();
    private CancellationTokenSource? _loading;

    public BriefingWindow()
    {
        Title = "Arsan Gaz ERP — Gündem";
        Width = 1040;
        Height = 760;
        MinWidth = 640;
        MinHeight = 480;
        WindowWorkAreaService.Fit(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrush("CanvasBrush");
        Content = BuildLayout();
        _refresh.Click += async (_, _) => await LoadAsync();
        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _loading?.Cancel();
    }

    private UIElement BuildLayout()
    {
        DockPanel root = new() { Margin = new Thickness(18) };

        StackPanel header = new();
        header.Children.Add(new TextBlock
        {
            Text = "Günün Gündemi",
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeBrush("InkBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = "Bugünkü ERP işleri, hava durumu, TCMB kurları ve haber başlıkları tek ekranda. " +
                   "Jarvis'e \"günaydın\" veya \"günün özeti\" yazarak da alabilirsiniz; bulut asistanı etkinse her sabah Telegram'a gelir.",
            Foreground = ThemeBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });

        StackPanel toolbar = new() { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(new TextBlock
        {
            Text = "Hava durumu ili:",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ThemeBrush("InkBrush")
        });
        toolbar.Children.Add(_city);
        toolbar.Children.Add(_refresh);
        header.Children.Add(toolbar);
        _status.Foreground = ThemeBrush("MutedBrush");
        header.Children.Add(_status);
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        root.Children.Add(new ScrollViewer
        {
            Content = _cards,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        });
        return root;
    }

    private async Task LoadAsync()
    {
        _loading?.Cancel();
        CancellationTokenSource loading = new(TimeSpan.FromSeconds(45));
        _loading = loading;
        _refresh.IsEnabled = false;
        _status.Text = "Gündem hazırlanıyor…";
        try
        {
            string? city = LiveInfoService.FindCity(_city.Text) ?? (_city.Text.Trim().Length > 0 ? _city.Text.Trim() : null);
            IReadOnlyList<BriefingSection> sections = await DailyBriefingService.BuildAsync(city, loading.Token);
            if (!ReferenceEquals(_loading, loading)) return;
            _cards.Children.Clear();
            foreach (BriefingSection section in sections)
            {
                _cards.Children.Add(BuildCard(section));
            }

            int failed = sections.Count(section => !section.Succeeded);
            _status.Text = $"Son güncelleme {DateTime.Now:HH:mm}" +
                           (failed == 0 ? string.Empty : $" · {failed} bölüm alınamadı (ayrıntı kartta)");
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_loading, loading)) _status.Text = "Gündem zaman aşımına uğradı; Yenile ile tekrar deneyin.";
        }
        finally
        {
            if (ReferenceEquals(_loading, loading))
            {
                _refresh.IsEnabled = true;
                _loading = null;
            }

            loading.Dispose();
        }
    }

    private static Border BuildCard(BriefingSection section)
    {
        StackPanel content = new();
        content.Children.Add(new TextBlock
        {
            Text = section.Title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = section.Succeeded ? ThemeBrush("InkBrush") : Brushes.DarkRed,
            Margin = new Thickness(0, 0, 0, 6)
        });
        content.Children.Add(new TextBox
        {
            Text = section.Body,
            IsReadOnly = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("InkBrush")
        });
        return new Border
        {
            Width = 470,
            Margin = new Thickness(0, 0, 12, 12),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(10),
            Background = ThemeBrush("SurfaceBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            Child = content
        };
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);
}
