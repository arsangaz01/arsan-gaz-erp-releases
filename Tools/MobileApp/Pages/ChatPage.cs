using System.Collections.ObjectModel;
using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class ChatMessage
{
    public required string Sender { get; init; }
    public required string Text { get; init; }
    public bool IsUser { get; init; }
    public LayoutOptions Alignment => IsUser ? LayoutOptions.End : LayoutOptions.Start;
    public Color Background => IsUser ? Color.FromArgb("#FFEDD5") : Color.FromArgb("#F3F4F6");
}

public sealed class ChatPage : ContentPage
{
    private readonly ObservableCollection<ChatMessage> _messages = [];
    private readonly List<ChatTurn> _history = [];
    private readonly CollectionView _list;
    private readonly Editor _input;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly Switch _speak = new() { IsToggled = Preferences.Default.Get("speak_replies", true), OnColor = Ui.Accent };
    private readonly Picker _target = new()
    {
        ItemsSource = new[] { "ERP ajanları", "Cursor (bilgisayar)" },
        SelectedIndex = Preferences.Default.Get("chat_target", 0),
        WidthRequest = 170
    };
    private readonly Button _send;
    private readonly Button _mic;
    private readonly Button _attach;
    private readonly Label _attachments;
    private readonly List<string> _pendingAttachments = [];
    private CancellationTokenSource? _speech;

    public ChatPage()
    {
        Title = "Ajan";
        _list = new CollectionView
        {
            ItemsSource = _messages,
            ItemTemplate = new DataTemplate(() =>
            {
                Label sender = new() { FontSize = 12, FontAttributes = FontAttributes.Bold, TextColor = Ui.Accent };
                sender.SetBinding(Label.TextProperty, nameof(ChatMessage.Sender));
                Label text = new() { FontSize = 15 };
                text.SetBinding(Label.TextProperty, nameof(ChatMessage.Text));
                Border bubble = new()
                {
                    Padding = new Thickness(10, 6),
                    Margin = new Thickness(6, 3),
                    StrokeThickness = 0,
                    StrokeShape = new Microsoft.Maui.Controls.Shapes.RoundRectangle { CornerRadius = 12 },
                    MaximumWidthRequest = 340,
                    Content = new VerticalStackLayout { Children = { sender, text } }
                };
                bubble.SetBinding(View.HorizontalOptionsProperty, nameof(ChatMessage.Alignment));
                bubble.SetBinding(BackgroundColorProperty, nameof(ChatMessage.Background));
                return bubble;
            })
        };

        _input = new Editor
        {
            Placeholder = "Mesaj yazın veya 🎤 ile konuşun",
            AutoSize = EditorAutoSizeOption.TextChanges,
            MaximumHeightRequest = 120,
            HorizontalOptions = LayoutOptions.Fill
        };
        _mic = new Button { Text = "🎤", FontSize = 22, WidthRequest = 58, BackgroundColor = Ui.Accent, TextColor = Colors.White };
        _mic.Clicked += async (_, _) => await ListenAsync();
        _attach = new Button { Text = "📎", FontSize = 20, WidthRequest = 52 };
        _attach.Clicked += async (_, _) => await PickAttachmentAsync();
        _attachments = new Label { FontSize = 12, TextColor = Ui.Muted, LineBreakMode = LineBreakMode.WordWrap };
        _send = new Button { Text = "Gönder", WidthRequest = 90 };
        _send.Clicked += async (_, _) => await SendAsync(_input.Text);
        _speak.Toggled += (_, e) => Preferences.Default.Set("speak_replies", e.Value);
        _target.SelectedIndexChanged += (_, _) => Preferences.Default.Set("chat_target", _target.SelectedIndex);

        Button clear = new() { Text = "Yeni sohbet", FontSize = 12, Padding = new Thickness(8, 2) };
        clear.Clicked += (_, _) =>
        {
            _messages.Clear();
            _history.Clear();
            AddWelcome();
        };

        Grid inputRow = new()
        {
            ColumnDefinitions =
            [
                new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto),
                new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Auto)
            ],
            ColumnSpacing = 6,
            Padding = new Thickness(8, 4, 8, 8)
        };
        inputRow.Add(_input, 0);
        inputRow.Add(_attach, 1);
        inputRow.Add(_send, 2);
        inputRow.Add(_mic, 3);

        HorizontalStackLayout options = new()
        {
            Spacing = 8,
            Padding = new Thickness(10, 4),
            Children =
            {
                new Label { Text = "Yanıtları sesli oku", VerticalOptions = LayoutOptions.Center },
                _speak,
                _busy,
                clear
            }
        };
        HorizontalStackLayout targetRow = new()
        {
            Spacing = 8,
            Padding = new Thickness(10, 0),
            Children = { new Label { Text = "Kime:", VerticalOptions = LayoutOptions.Center }, _target }
        };

        Grid root = new()
        {
            RowDefinitions =
            [
                new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto),
                new RowDefinition(GridLength.Auto)
            ]
        };
        root.Add(options, 0, 0);
        root.Add(targetRow, 0, 1);
        root.Add(_list, 0, 2);
        root.Add(_attachments, 0, 3);
        root.Add(inputRow, 0, 4);
        Content = root;
        AddWelcome();
    }

    private void AddWelcome() => _messages.Add(new ChatMessage
    {
        Sender = "Jarvis",
        Text = "Merhaba! ERP ajanlarıyla konuşabilirsiniz. Örnekler: \"Bugün kimi aramalıyım?\", \"Adana'daki hastaneleri listele\", " +
               "\"@Defne /not Firma adı | Not\", \"@Defne /takip Firma adı | 2026-10-15\". 🎤 düğmesine basıp sesli komut verebilirsiniz.\n" +
               "\"Kime: Cursor (bilgisayar)\" seçerseniz istek bilgisayardaki Cursor ajanına gider; yanıt hem burada hem Telegram'da görünür.\n" +
               "📎 ile fotoğraf, PDF veya video ekleyebilirsiniz (en fazla 5 dosya, dosya başına 15 MB; ekler Cursor hedefinde)."
    });

    private void RefreshAttachmentLabel()
    {
        _attachments.Text = _pendingAttachments.Count == 0
            ? string.Empty
            : "Ekler: " + string.Join(", ", _pendingAttachments.Select(Path.GetFileName))
              + "  (temizlemek için uzun basın)";
    }

    private async Task PickAttachmentAsync()
    {
        if (_pendingAttachments.Count >= 5)
        {
            await DisplayAlertAsync("Ek", "En fazla 5 dosya ekleyebilirsiniz.", "Tamam");
            return;
        }

        string? choice = await DisplayActionSheetAsync("Dosya ekle", "İptal", "Ekleri temizle", "Fotoğraf", "Video", "PDF / dosya");
        if (choice is null or "İptal")
        {
            return;
        }

        if (choice == "Ekleri temizle")
        {
            _pendingAttachments.Clear();
            RefreshAttachmentLabel();
            return;
        }

        try
        {
            FileResult? result = choice switch
            {
                "Fotoğraf" => await MediaPicker.Default.PickPhotoAsync(new MediaPickerOptions { Title = "Fotoğraf seç" }),
                "Video" => await MediaPicker.Default.PickVideoAsync(new MediaPickerOptions { Title = "Video seç" }),
                _ => await FilePicker.Default.PickAsync(new PickOptions
                {
                    PickerTitle = "PDF veya dosya",
                    FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
                    {
                        [DevicePlatform.Android] = ["application/pdf", "image/*", "video/*"],
                        [DevicePlatform.iOS] = ["com.adobe.pdf", "public.image", "public.movie"],
                        [DevicePlatform.WinUI] = [".pdf", ".jpg", ".jpeg", ".png", ".mp4"]
                    })
                })
            };

            if (result?.FullPath is not { Length: > 0 } path)
            {
                return;
            }

            _pendingAttachments.Add(path);
            RefreshAttachmentLabel();
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Ek", exception.Message, "Tamam");
        }
    }

    private async Task SendToCursorAsync(string message, IReadOnlyList<string>? attachments)
    {
        string id = await ApiClient.Instance.SendToCursorAsync(message, attachments);
        _messages.Add(new ChatMessage { Sender = "Cursor", Text = "İstek bilgisayara iletildi. Yanıt hazır olunca burada görünecek (birkaç dakika sürebilir)." });
        ScrollToEnd();
        _ = PollCursorAsync(id);
    }

    private async Task PollCursorAsync(string id)
    {
        string? lastStage = null;
        DateTime deadline = DateTime.UtcNow.AddMinutes(45);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(10));
            CursorStatus status;
            try
            {
                status = await ApiClient.Instance.CursorStatusAsync(id);
            }
            catch (Exception)
            {
                continue;
            }

            if (status.Report is { Length: > 0 } report)
            {
                MainThread.BeginInvokeOnMainThread(async () =>
                {
                    _messages.Add(new ChatMessage { Sender = "Cursor", Text = report });
                    ScrollToEnd();
                    if (_speak.IsToggled)
                    {
                        await SpeakAsync(report);
                    }
                });
                return;
            }

            if (status.Stage is { Length: > 0 } stage && stage != lastStage)
            {
                lastStage = stage;
                MainThread.BeginInvokeOnMainThread(() =>
                {
                    _messages.Add(new ChatMessage { Sender = "Cursor", Text = "⏳ " + stage });
                    ScrollToEnd();
                });
            }
        }

        MainThread.BeginInvokeOnMainThread(() =>
            _messages.Add(new ChatMessage { Sender = "Cursor", Text = "Yanıt 45 dakikada gelmedi; sonuç hazır olunca Telegram'a da gönderilecek." }));
    }

    private async Task ListenAsync()
    {
        try
        {
            PermissionStatus status = await Permissions.RequestAsync<Permissions.Microphone>();
            if (status != PermissionStatus.Granted)
            {
                await DisplayAlertAsync("Mikrofon", "Sesli komut için mikrofon izni gerekli.", "Tamam");
                return;
            }

            _speech?.Cancel();
            string? text = await SpeechInput.ListenAsync();
            if (!string.IsNullOrWhiteSpace(text))
            {
                await SendAsync(text);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Sesli komut", exception.Message, "Tamam");
        }
    }

    private async Task SendAsync(string? raw)
    {
        string message = (raw ?? string.Empty).Trim();
        if ((message.Length == 0 && _pendingAttachments.Count == 0) || _busy.IsRunning)
        {
            return;
        }

        if (_pendingAttachments.Count > 0 && _target.SelectedIndex != 1)
        {
            await DisplayAlertAsync("Ek", "Fotoğraf/PDF/video ekleri yalnızca \"Cursor (bilgisayar)\" hedefine gönderilir.", "Tamam");
            return;
        }

        string[] attachments = _pendingAttachments.ToArray();
        string display = message.Length > 0 ? message : "(ek dosyalar)";
        if (attachments.Length > 0)
        {
            display += "\n📎 " + string.Join(", ", attachments.Select(Path.GetFileName));
        }

        _input.Text = string.Empty;
        _messages.Add(new ChatMessage { Sender = "Siz", Text = display, IsUser = true });
        ScrollToEnd();
        _send.IsEnabled = _mic.IsEnabled = _attach.IsEnabled = false;
        if (_target.SelectedIndex == 1)
        {
            await Ui.RunAsync(this, _busy, async () =>
            {
                await SendToCursorAsync(
                    message.Length > 0 ? message : "Mobil ekleri incele.",
                    attachments.Length > 0 ? attachments : null);
                _pendingAttachments.Clear();
                RefreshAttachmentLabel();
            });
            _send.IsEnabled = _mic.IsEnabled = _attach.IsEnabled = true;
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            List<AgentReply> replies = await ApiClient.Instance.ChatAsync(message, _history.TakeLast(12));
            _history.Add(new ChatTurn("user", message));
            foreach (AgentReply reply in replies)
            {
                _messages.Add(new ChatMessage { Sender = reply.Agent, Text = reply.Text });
                if (reply.Ok)
                {
                    _history.Add(new ChatTurn("assistant", $"{reply.Agent}: {reply.Text}"));
                }
            }

            ScrollToEnd();
            if (_speak.IsToggled && replies.FirstOrDefault(r => r.Ok) is { } first)
            {
                await SpeakAsync(first.Text);
            }
        });
        _send.IsEnabled = _mic.IsEnabled = _attach.IsEnabled = true;
    }

    private async Task SpeakAsync(string text)
    {
        string spoken = text.StartsWith('[') && text.IndexOf(']') is > 0 and var end ? text[(end + 1)..] : text;
        spoken = spoken.Split("\n\n— ", 2)[0].Replace("*", string.Empty).Replace("#", string.Empty).Trim();
        if (spoken.Length > 900)
        {
            spoken = spoken[..900];
        }

        _speech = new CancellationTokenSource();
        try
        {
            IEnumerable<Locale> locales = await TextToSpeech.Default.GetLocalesAsync();
            Locale? turkish = locales.FirstOrDefault(l => l.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase));
            await TextToSpeech.Default.SpeakAsync(spoken, new SpeechOptions { Locale = turkish }, _speech.Token);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void ScrollToEnd()
    {
        if (_messages.Count > 0)
        {
            _list.ScrollTo(_messages[^1], position: ScrollToPosition.End, animate: true);
        }
    }
}
