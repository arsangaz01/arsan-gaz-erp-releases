using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed class MailAssistantWindow : Window
{
    private readonly GraphService _graph;
    private readonly CloudAgentClient _ai = new();
    private readonly ListBox _inbox = new() { MinWidth = 300 };
    private readonly Button _refresh = new() { Content = "Gelen kutusunu yenile", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _summarize = new() { Content = "Özetle", Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
    private readonly Button _draft = new() { Content = "Yanıt taslağı hazırla", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0), IsEnabled = false };
    private readonly Button _send = new() { Content = "Yanıtı gönder…", Padding = new Thickness(12, 6, 12, 6), IsEnabled = false };
    private readonly TextBox _original = ReadOnlyBox();
    private readonly TextBox _summary = ReadOnlyBox();
    private readonly TextBox _instruction = new()
    {
        Padding = new Thickness(6, 4, 6, 4),
        ToolTip = "İsteğe bağlı: ör. \"Teklifi kabul ettiğimizi, teslimatın pazartesi olacağını yaz\""
    };
    private readonly TextBox _reply = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 120,
        Padding = new Thickness(6)
    };
    private readonly TextBox _translation = ReadOnlyBox();
    private readonly TextBlock _replyLanguage = new() { FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
    private MailSummary? _selected;
    private string _selectedBody = string.Empty;
    private bool _busy;
    private int _mailLoadSeq;

    public MailAssistantWindow(GraphService graph)
    {
        _graph = graph;
        Title = "Arsan Gaz ERP — Mail Asistanı";
        Width = 1180;
        Height = 820;
        MinWidth = 820;
        MinHeight = 560;
        WindowWorkAreaService.Fit(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrush("CanvasBrush");
        Content = BuildLayout();
        _inbox.SelectionChanged += async (_, _) => await LoadSelectedAsync();
        _refresh.Click += async (_, _) => await LoadInboxAsync();
        _summarize.Click += async (_, _) => await SummarizeAsync();
        _draft.Click += async (_, _) => await DraftAsync();
        _send.Click += async (_, _) => await SendAsync();
        _reply.TextChanged += (_, _) => _send.IsEnabled = !_busy && _selected is not null && _reply.Text.Trim().Length > 0;
        Loaded += async (_, _) => await LoadInboxAsync();
    }

    private UIElement BuildLayout()
    {
        DockPanel root = new() { Margin = new Thickness(18) };
        StackPanel header = new();
        header.Children.Add(new TextBlock
        {
            Text = "Mail Asistanı",
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeBrush("InkBrush")
        });
        header.Children.Add(new TextBlock
        {
            Text = "Outlook gelen kutusunu okur; seçtiğiniz e-postayı özetler ve e-postanın dilinde yanıt taslağı + Türkçe çevirisini hazırlar. " +
                   "Hiçbir yanıt siz onaylamadan gönderilmez. Özet/taslak için e-posta metni Cloudflare Worker'ınıza (Workers AI) gönderilir.",
            Foreground = ThemeBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 4, 0, 10)
        });
        _status.Foreground = ThemeBrush("MutedBrush");
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        DockPanel.SetDock(_status, Dock.Bottom);
        root.Children.Add(_status);

        Grid body = new();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
        body.ColumnDefinitions.Add(new ColumnDefinition());

        DockPanel left = new();
        _refresh.Margin = new Thickness(0, 0, 0, 8);
        DockPanel.SetDock(_refresh, Dock.Top);
        left.Children.Add(_refresh);
        left.Children.Add(_inbox);
        body.Children.Add(left);

        Grid right = new();
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        right.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1.4, GridUnitType.Star) });
        right.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(right, 2);

        AddRow(right, 0, Labeled("Seçili e-posta", _original));

        StackPanel actions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 8) };
        actions.Children.Add(_summarize);
        actions.Children.Add(_draft);
        AddRow(right, 1, actions);
        AddRow(right, 2, Labeled("Türkçe özet", _summary));

        DockPanel instructionRow = new() { Margin = new Thickness(0, 8, 0, 8) };
        TextBlock instructionLabel = new()
        {
            Text = "Yanıt talimatı (isteğe bağlı):",
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            Foreground = ThemeBrush("InkBrush")
        };
        DockPanel.SetDock(instructionLabel, Dock.Left);
        instructionRow.Children.Add(instructionLabel);
        instructionRow.Children.Add(_instruction);
        AddRow(right, 3, instructionRow);

        Grid drafts = new();
        drafts.ColumnDefinitions.Add(new ColumnDefinition());
        drafts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        drafts.ColumnDefinitions.Add(new ColumnDefinition());
        DockPanel replyPanel = new();
        _replyLanguage.Text = "Yanıt taslağı (düzenleyebilirsiniz)";
        _replyLanguage.Foreground = ThemeBrush("InkBrush");
        DockPanel.SetDock(_replyLanguage, Dock.Top);
        replyPanel.Children.Add(_replyLanguage);
        replyPanel.Children.Add(_reply);
        drafts.Children.Add(replyPanel);
        FrameworkElement translation = Labeled("Türkçe çevirisi (taslağın ilk hâli)", _translation);
        Grid.SetColumn(translation, 2);
        drafts.Children.Add(translation);
        AddRow(right, 4, drafts);

        _send.Margin = new Thickness(0, 8, 0, 0);
        _send.HorizontalAlignment = HorizontalAlignment.Right;
        AddRow(right, 5, _send);
        body.Children.Add(right);
        root.Children.Add(body);
        return root;
    }

    private async Task LoadInboxAsync()
    {
        if (_busy) return;
        await RunBusyAsync("Gelen kutusu okunuyor…", async () =>
        {
            IReadOnlyList<MailSummary> mails = await _graph.ListInboxAsync();
            _inbox.Items.Clear();
            foreach (MailSummary mail in mails)
            {
                _inbox.Items.Add(new ListBoxItem
                {
                    Tag = mail,
                    Padding = new Thickness(6, 5, 6, 5),
                    Content = new TextBlock
                    {
                        Text = $"{(mail.IsRead ? "" : "● ")}{mail.Subject}\n{SenderLabel(mail)} · {mail.Received:dd.MM HH:mm}",
                        FontWeight = mail.IsRead ? FontWeights.Normal : FontWeights.SemiBold,
                        TextWrapping = TextWrapping.Wrap
                    }
                });
            }

            return $"{mails.Count} e-posta listelendi · son yenileme {DateTime.Now:HH:mm}";
        }, "Microsoft hesabına giriş yapılamadı veya e-postalar okunamadı (önce sol menüden \"Microsoft Giriş\" yapın): ");
    }

    private async Task LoadSelectedAsync()
    {
        if (_inbox.SelectedItem is not ListBoxItem { Tag: MailSummary mail }) return;
        // Do not gate on _busy: a fast click-through must supersede the previous body load.
        int seq = ++_mailLoadSeq;
        _selected = mail;
        _selectedBody = string.Empty;
        _summary.Clear();
        _reply.Clear();
        _translation.Clear();
        _replyLanguage.Text = "Yanıt taslağı (düzenleyebilirsiniz)";
        _status.Text = "E-posta açılıyor…";
        UpdateButtons();
        try
        {
            string body = await _graph.GetMailBodyTextAsync(mail.Id);
            if (seq != _mailLoadSeq) return;
            if (_inbox.SelectedItem is not ListBoxItem { Tag: MailSummary still } || still.Id != mail.Id) return;
            _selectedBody = body;
            _original.Text = $"Gönderen: {SenderLabel(mail)} <{mail.FromAddress}>\nKonu: {mail.Subject}\nTarih: {mail.Received:dd.MM.yyyy HH:mm}\n\n{_selectedBody.Trim()}";
            _status.Text = "E-posta hazır. Özetleyebilir veya yanıt taslağı isteyebilirsiniz.";
        }
        catch (Exception exception)
        {
            if (seq != _mailLoadSeq) return;
            _status.Text = "E-posta okunamadı: " + exception.GetBaseException().Message;
        }
        finally
        {
            if (seq == _mailLoadSeq)
                UpdateButtons();
        }
    }

    private async Task SummarizeAsync()
    {
        if (_selected is not { } mail) return;
        await RunBusyAsync("Jarvis e-postayı özetliyor…", async () =>
        {
            _summary.Text = await MailAssistantService.SummarizeAsync(
                _ai, $"{SenderLabel(mail)} <{mail.FromAddress}>", mail.Subject, _selectedBody, CancellationToken.None);
            return "Özet hazır.";
        }, "Özet alınamadı: ");
    }

    private async Task DraftAsync()
    {
        if (_selected is not { } mail) return;
        await RunBusyAsync("Jarvis yanıt taslağı hazırlıyor…", async () =>
        {
            MailReplyDraft draft = await MailAssistantService.DraftReplyAsync(
                _ai, $"{SenderLabel(mail)} <{mail.FromAddress}>", mail.Subject, _selectedBody, _instruction.Text, CancellationToken.None);
            _replyLanguage.Text = $"Yanıt taslağı · {draft.Language} (düzenleyebilirsiniz)";
            _reply.Text = draft.Reply;
            _translation.Text = draft.TurkishTranslation;
            return "Taslak hazır. Kontrol edip düzenleyin; göndermek için \"Yanıtı gönder…\" ve ardından onay verin.";
        }, "Taslak hazırlanamadı: ");
    }

    private async Task SendAsync()
    {
        if (_selected is not { } mail) return;
        string reply = _reply.Text.Trim();
        if (reply.Length == 0) return;

        string preview = reply.Length > 400 ? reply[..400] + " …" : reply;
        MessageBoxResult confirmation = MessageBox.Show(
            this,
            $"Bu yanıt şimdi GÖNDERİLECEK:\n\nKime: {SenderLabel(mail)} <{mail.FromAddress}>\nKonu: RE: {mail.Subject}\n\n{preview}\n\nGönderilsin mi?",
            "Yanıtı onayla",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No);
        if (confirmation != MessageBoxResult.Yes)
        {
            _status.Text = "Gönderim iptal edildi; hiçbir e-posta gönderilmedi.";
            return;
        }

        await RunBusyAsync("Yanıt gönderiliyor…", async () =>
        {
            await _graph.ReplyToMailAsync(mail.Id, reply);
            _reply.Clear();
            return $"Yanıt gönderildi: {mail.FromAddress} · Gönderilmiş Öğeler'e kaydedildi.";
        }, "Yanıt gönderilemedi: ");
    }

    private async Task RunBusyAsync(string progress, Func<Task<string>> work, string failurePrefix)
    {
        _busy = true;
        UpdateButtons();
        _status.Text = progress;
        try
        {
            _status.Text = await work();
        }
        catch (Exception exception)
        {
            _status.Text = failurePrefix + exception.GetBaseException().Message;
        }
        finally
        {
            _busy = false;
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        bool hasMail = _selected is not null && _selectedBody.Length > 0;
        bool aiReady = CloudAgentClient.IsConfigured;
        _refresh.IsEnabled = !_busy;
        _inbox.IsEnabled = !_busy;
        _summarize.IsEnabled = !_busy && hasMail && aiReady;
        _draft.IsEnabled = !_busy && hasMail && aiReady;
        _send.IsEnabled = !_busy && _selected is not null && _reply.Text.Trim().Length > 0;
        if (!aiReady)
        {
            _summarize.ToolTip = _draft.ToolTip = "Cloudflare Worker yapılandırılmamış (Ajan Yetkileri ve Bulut).";
        }
    }

    private static string SenderLabel(MailSummary mail) =>
        string.IsNullOrWhiteSpace(mail.FromName) ? mail.FromAddress : mail.FromName;

    private static void AddRow(Grid grid, int row, UIElement element)
    {
        Grid.SetRow(element, row);
        grid.Children.Add(element);
    }

    private static FrameworkElement Labeled(string label, Control control)
    {
        DockPanel panel = new();
        TextBlock title = new()
        {
            Text = label,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrush("InkBrush"),
            Margin = new Thickness(0, 0, 0, 4)
        };
        DockPanel.SetDock(title, Dock.Top);
        panel.Children.Add(title);
        panel.Children.Add(control);
        return panel;
    }

    private static TextBox ReadOnlyBox() => new()
    {
        IsReadOnly = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        Padding = new Thickness(6)
    };

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);
}
