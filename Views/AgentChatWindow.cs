using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using ArsanGazERP.Services;
using IoPath = System.IO.Path;

namespace ArsanGazERP.Views;

public sealed class AgentChatWindow : Window
{
    private readonly AgentChatService _service = new();
    private readonly List<ErpChatTurn> _history = [];
    private readonly StackPanel _messages = new() { Margin = new Thickness(14) };
    private readonly ScrollViewer _chatScroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        Content = null
    };
    private readonly TextBox _message = new()
    {
        AcceptsReturn = true,
        TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        MinHeight = 66,
        MaxHeight = 140,
        Padding = new Thickness(10),
        FontSize = 14
    };
    private readonly TextBlock _status = new()
    {
        Foreground = ThemeBrush("MutedBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.Wrap
    };
    private readonly Button _send = new() { Content = "Gönder", MinWidth = 100, Padding = new Thickness(14, 8, 14, 8) };
    private readonly Button _cancel = new() { Content = "Yanıtı durdur", Padding = new Thickness(10, 8, 10, 8), IsEnabled = false };
    private readonly Button _microphone = new() { Content = "Mikrofonu aç", Padding = new Thickness(10, 8, 10, 8) };
    private readonly CheckBox _speakReplies = new() { Content = "Yanıtları seslendir", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _wakeWord = new()
    {
        Content = "\"Hey Jarvis\" ile uyandır",
        IsChecked = true,
        VerticalAlignment = VerticalAlignment.Center,
        Margin = new Thickness(16, 0, 0, 0),
        ToolTip = "Mikrofon açıkken yalnızca \"Jarvis …\" veya \"Hey Jarvis\" ile başlayan konuşmalar komut olarak alınır. " +
                  "Yalnızca \"Hey Jarvis\" derseniz sonraki 10 saniyedeki cümleniz gönderilir."
    };
    private readonly Button _voiceSettings = new()
    {
        Content = "Ses ayarları…",
        Padding = new Thickness(10, 3, 10, 3),
        Margin = new Thickness(16, 0, 0, 0)
    };
    private ElevenLabsSpeechService? _cloudVoice;
    private PiperSpeechService? _piperVoice;
    private DateTime _wakeArmedUntil = DateTime.MinValue;
    private readonly WrapPanel _agentButtons = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly HolographicFace _face = new()
    {
        Width = 130,
        Height = 150,
        Margin = new Thickness(12, 0, 0, 0),
        HorizontalAlignment = HorizontalAlignment.Right,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly Queue<string> _pendingMessages = new();
    private readonly Queue<string> _pendingSpeech = new();
    private readonly System.Windows.Threading.DispatcherTimer _researchReportTimer = new()
    {
        Interval = TimeSpan.FromSeconds(30)
    };
    private readonly string _researchReportCursorPath = IoPath.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "Supervisor",
        "last-research-report.txt");
    private LocalSpeechOutputService? _speechOutput;
    private bool _isSending;
    private bool _hasTurkishVoice;
    private string _speechStatus = "Türkçe ses denetlenmedi.";
    private bool _isSpeaking;
    private bool _researchReportCheckInProgress;
    private string? _lastImportedResearchReportPath;
    private LocalSpeechInputService? _speechInput;
    private CancellationTokenSource? _microphoneStartCancellation;
    private Task? _microphoneStartTask;
    private CancellationTokenSource? _activeRequest;
    private const int MaximumQueuedMessages = 3;
    private bool _allowClose;

    public AgentChatWindow()
    {
        Title = "Arsan Gaz ERP — Ajan Sohbeti";
        Width = 880;
        Height = 700;
        MinWidth = Math.Min(640, Width);
        MinHeight = Math.Min(500, Height);
        WindowWorkAreaService.Fit(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrush("CanvasBrush");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_message, "AgentChat.Message");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_send, "AgentChat.Send");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_cancel, "AgentChat.Cancel");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_microphone, "AgentChat.Microphone");
        System.Windows.Automation.AutomationProperties.SetAutomationId(_speakReplies, "AgentChat.SpeakReplies");
        _chatScroll.Content = _messages;
        Content = BuildLayout();

        _send.Click += async (_, _) => await SendAsync();
        _cancel.Click += (_, _) => _activeRequest?.Cancel();
        _microphone.Click += async (_, _) => await ToggleMicrophoneAsync();
        _message.PreviewKeyDown += Message_PreviewKeyDown;
        _researchReportTimer.Tick += async (_, _) => await ImportLatestResearchReportAsync();
        _hasTurkishVoice = TryConfigureTurkishVoice();
        _speakReplies.IsChecked = false;
        ConfigureCloudVoice();
        _voiceSettings.Click += (_, _) =>
        {
            new VoiceSettingsWindow { Owner = this }.ShowDialog();
            ConfigureCloudVoice();
        };
        Loaded += async (_, _) =>
        {
            if (_messages.Children.Count == 0)
            {
                try
                {
                    _status.Text = "Gözcü güncel yerel sağlık kontrolünü yapıyor…";
                    await SystemSupervisorService.CheckNowAsync();
                    string briefing = await _service.RespondAsync(
                        AgentChatService.Profiles.Single(profile => profile.Id == "supervisor"),
                        [],
                        "Açılış sağlık özeti");
                    AddMessage("Gözcü", "Sistem sağlık raporu", briefing, false);
                }
                catch (Exception exception)
                {
                    AddMessage(
                        "Gözcü",
                        "Sistem sağlık raporu alınamadı",
                        exception.GetBaseException().Message,
                        false);
                }
            }

            if (!_researchReportTimer.IsEnabled)
            {
                _researchReportTimer.Start();
            }
            await ImportLatestResearchReportAsync();
            _status.Text = "Mikrofon kapalı · İsterseniz elle açın. Görev dağıtımı için @Gözcü /denetle yazın.";
            SetOrbMode("IDLE");
            _message.Focus();
        };
        Closed += async (_, _) =>
        {
            _researchReportTimer.Stop();
            _activeRequest?.Cancel();
            await StopMicrophoneAsync(false);
            _pendingSpeech.Clear();
            _speechOutput?.Stop();
            _cloudVoice?.Stop();
            _piperVoice?.Stop();
            if (_speechOutput is not null) await _speechOutput.DisposeAsync();
            if (_cloudVoice is not null) await _cloudVoice.DisposeAsync();
            if (_piperVoice is not null) await _piperVoice.DisposeAsync();
            await _service.DisposeAsync();
        };
        Closing += (_, eventArgs) =>
        {
            if (_allowClose) return;
            eventArgs.Cancel = true;
            Hide();
        };
    }

    public void CloseForOwnerShutdown()
    {
        _allowClose = true;
        Close();
    }

    private UIElement BuildLayout()
    {
        Grid root = new() { Margin = new Thickness(18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        Grid heading = new()
        {
            Margin = new Thickness(0, 0, 0, 8),
            ColumnDefinitions =
            {
                new ColumnDefinition(),
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        StackPanel headingText = new() { VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(new TextBlock
        {
            Text = "ERP Ajan Ekibi",
            FontSize = 25,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeBrush("InkBrush")
        });
        headingText.Children.Add(new TextBlock
        {
            Text = "Gözcü yerel sistem durumunu raporlar; görevlerde uzman ajanlara iş dağıtır ve sonuçları denetler.",
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = ThemeBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        heading.Children.Add(headingText);
        UIElement orb = BuildJarvisOrb();
        Grid.SetColumn(orb, 1);
        heading.Children.Add(orb);
        root.Children.Add(heading);

        Border statusPanel = new()
        {
            Background = ThemeBrush("SurfaceBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(10),
            Margin = new Thickness(0, 0, 0, 10),
            Child = _status
        };
        Grid.SetRow(statusPanel, 1);
        root.Children.Add(statusPanel);

        Border chatPanel = new()
        {
            Background = ThemeBrush("SurfaceBrush"),
            BorderBrush = ThemeBrush("BorderBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Child = _chatScroll,
            Margin = new Thickness(0, 0, 0, 10)
        };
        Grid.SetRow(chatPanel, 2);
        root.Children.Add(chatPanel);

        DockPanel composer = new() { LastChildFill = true, Margin = new Thickness(0, 0, 0, 4) };
        _send.IsDefault = true;
        _send.VerticalAlignment = VerticalAlignment.Bottom;
        _send.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_send, Dock.Right);
        composer.Children.Add(_send);
        _cancel.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_cancel, Dock.Right);
        composer.Children.Add(_cancel);
        _microphone.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(_microphone, Dock.Right);
        composer.Children.Add(_microphone);
        _message.ToolTip = "Etiketsiz soruları Jarvis yanıtlar. Belirli bir uzman için @ajan adıyla başlayın; yalnızca o ajan yanıt verir. Kamuya açık araştırma için @Ar-Ge Araştırmacısı'nı çağırın. Yerel sistem raporu için @Gözcü'ye sorun. Kişisel veri, parola veya gizli bilgi girmeyin.";
        composer.Children.Add(_message);
        Grid.SetRow(composer, 3);
        root.Children.Add(composer);

        StackPanel voiceOptions = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 4) };
        voiceOptions.Children.Add(_speakReplies);
        voiceOptions.Children.Add(_wakeWord);
        voiceOptions.Children.Add(_voiceSettings);
        Grid.SetRow(voiceOptions, 4);
        root.Children.Add(voiceOptions);

        StackPanel mentionArea = new() { Margin = new Thickness(0, 4, 0, 4) };
        mentionArea.Children.Add(new TextBlock
        {
            Text = "Etiketsiz mesajları Jarvis yanıtlar. Uzman yanıtı veya kamuya açık araştırma için ilgili ajanı etiketleyin; yalnızca seçilen ajan yanıt verir:",
            Foreground = ThemeBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap
        });
        foreach (ErpAgentProfile profile in AgentChatService.Profiles)
        {
            Button button = new()
            {
                Content = profile.Name,
                ToolTip = $"Mesajın başına @{profile.Name} ekle",
                Padding = new Thickness(11, 5, 11, 5),
                Margin = new Thickness(0, 5, 7, 0),
                Background = ThemeBrush("SurfaceBrush"),
                BorderBrush = ThemeBrush("BorderBrush"),
                Foreground = ThemeBrush("InkBrush"),
                Cursor = Cursors.Hand
            };
            System.Windows.Automation.AutomationProperties.SetAutomationId(
                button,
                $"AgentChat.Agent.{profile.Id}");
            button.Click += (_, _) => InsertMention(profile);
            _agentButtons.Children.Add(button);
        }
        mentionArea.Children.Add(_agentButtons);
        Grid.SetRow(mentionArea, 5);
        root.Children.Add(mentionArea);

        DockPanel footer = new() { Margin = new Thickness(0, 5, 0, 0) };
        TextBlock hint = new()
        {
            Text = "Enter gönderir · Shift+Enter yeni satır · Varsayılan: Gözcü · Doğal komut: @Defne not ekle Firma | Not · @Can görev oluştur Açıklama · Gözcü görevi: @Gözcü uzmanlara görev dağıt Açıklama · /komutlar",
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ThemeBrush("MutedBrush"),
            TextWrapping = TextWrapping.Wrap
        };
        footer.Children.Add(hint);
        Grid.SetRow(footer, 6);
        root.Children.Add(footer);
        return root;
    }

    private UIElement BuildJarvisOrb() => _face;

    private void SetOrbMode(string mode)
    {
        if (_face.Mode != mode) _face.SetMode(mode);
    }

    private void InsertMention(ErpAgentProfile profile)
    {
        string currentText = _message.Text;
        if (AgentChatService.TryParseMention(currentText, out _, out string remainingText))
        {
            currentText = remainingText;
        }

        string prefix = "@" + profile.Name + " ";
        _message.Text = prefix + currentText.TrimStart();
        _message.CaretIndex = prefix.Length;
        _message.Focus();
    }

    private void Message_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async Task SendAsync()
    {
        string text = _message.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        if (_pendingMessages.Count >= MaximumQueuedMessages)
        {
            _status.Text = $"En fazla {MaximumQueuedMessages} bekleyen mesaj tutulabilir. Kuyruk boşalınca tekrar gönderin.";
            return;
        }

        if (AgentChatService.TryParseMention(text, out _, out string addressedMessage) &&
            string.IsNullOrWhiteSpace(addressedMessage))
        {
            _status.Text = "Ajan adından sonra mesajınızı yazın.";
            _message.Focus();
            return;
        }

        _pendingMessages.Enqueue(text);
        _message.Clear();
        AddMessage("Siz", _isSending ? "Kuyruğa alındı" : "Kullanıcı", text, true);

        if (_isSending)
        {
            _status.Text = $"Mesaj sıraya alındı · bekleyen: {_pendingMessages.Count}";
            return;
        }

        await ProcessMessageQueueAsync();
    }

    private async Task ProcessMessageQueueAsync()
    {
        if (_isSending) return;
        _isSending = true;
        _send.IsEnabled = true;
        try
        {
            while (_pendingMessages.TryDequeue(out string? text))
            {
                SetOrbMode("THINKING");
                _activeRequest = new CancellationTokenSource();
                _cancel.IsEnabled = true;
                _status.Text = _pendingMessages.Count == 0
                    ? "Mesaj tüm ajanlara iletiliyor…"
                    : $"Ajanlar yanıtlıyor · sırada {_pendingMessages.Count} mesaj var";
                try
                {
                    IReadOnlyList<ErpAgentReply> replies = await _service.RespondToAllAsync(
                        _history.ToArray(),
                        text,
                        _activeRequest.Token,
                        onReply: reply => Dispatcher.BeginInvoke(() =>
                        {
                            AddMessage(reply.Agent.Name, reply.Agent.Role, reply.Content, false);
                            if (!reply.IsProgress && reply.Succeeded && _speakReplies.IsChecked == true)
                            {
                                QueueSpeech($"{reply.Agent.Name}. {reply.Content}");
                            }
                        }));

                    _history.Add(new ErpChatTurn("user", text));
                    foreach (ErpAgentReply reply in replies)
                    {
                        _history.Add(new ErpChatTurn("assistant", $"[{reply.Agent.Name}] {reply.Content}"));
                    }

                    int succeeded = replies.Count(reply => reply.Succeeded);
                    _status.Text = succeeded == replies.Count
                        ? $"{succeeded} ajan yanıtladı"
                        : $"{succeeded}/{replies.Count} ajan yanıtladı · başarısız yanıtlar sohbet alanında gösterildi";
                    if (!CanSpeak || _speakReplies.IsChecked != true)
                    {
                        SetOrbMode(_speechInput is null ? "IDLE" : "LISTENING");
                    }
                }
                catch (Exception exception)
                {
                    string error = "Yanıt oluşturulamadı: " + exception.GetBaseException().Message;
                    AddMessage("ERP Ajan Ekibi", "İşlem başarısız", error, false);
                    _history.Add(new ErpChatTurn("user", text));
                    _history.Add(new ErpChatTurn("assistant", error));
                    _status.Text = "İşlem başarısız · hata ayrıntısı sohbet alanında";
                }
                finally
                {
                            _activeRequest?.Dispose();
                    _activeRequest = null;
                    _cancel.IsEnabled = false;
                }
            }
        }
        finally
        {
            _isSending = false;
            if (!_isSpeaking)
            {
                SetOrbMode(_speechInput is null ? "IDLE" : "LISTENING");
            }
            _message.Focus();
        }
    }

    private async Task ToggleMicrophoneAsync()
    {
        if (_speechInput is not null)
        {
            await StopMicrophoneAsync(true);
            return;
        }

        await StartMicrophoneAsync();
    }

    private Task StartMicrophoneAsync()
    {
        if (_speechInput is not null) return Task.CompletedTask;
        if (_microphoneStartTask is not null) return _microphoneStartTask;

        LocalSpeechInputService speechInput = new();
        CancellationTokenSource startCancellation = new();
        _speechInput = speechInput;
        _microphoneStartCancellation = startCancellation;
        _microphoneStartTask = StartMicrophoneCoreAsync(speechInput, startCancellation.Token);
        return _microphoneStartTask;
    }

    private async Task StartMicrophoneCoreAsync(LocalSpeechInputService speechInput, CancellationToken cancellationToken)
    {
        speechInput.TranscriptReceived += transcript => Dispatcher.BeginInvoke(
            new Action(() => _ = HandleVoiceTranscriptAsync(transcript)));
        speechInput.Error += exception => Dispatcher.BeginInvoke(new Action(() =>
        {
            if (IsLoaded)
            {
                _status.Text = "Yerel mikrofon/konuşma hatası: " + exception.GetBaseException().Message;
            }
        }));
        _microphone.IsEnabled = false;
        _microphone.Content = "Türkçe model yükleniyor…";
        _status.Text = "Yerel Türkçe konuşma modeli hazırlanıyor. İlk açılışta yaklaşık 75 MB indirilir; ses kaydı cihazdan çıkmaz.";
        SetOrbMode("THINKING");
        try
        {
            await speechInput.StartAsync(cancellationToken);
            if (IsLoaded)
            {
                _microphone.Content = "Mikrofonu kapat";
                _status.Text = "Mikrofon açık · Türkçe konuşma cihazda çevrimdışı yazıya çevrilir; yalnızca metin ajanlara gönderilir.";
                SetOrbMode("LISTENING");
            }
        }
        catch (Exception exception)
        {
            await speechInput.DisposeAsync();
            if (ReferenceEquals(_speechInput, speechInput)) _speechInput = null;
            if (IsLoaded)
            {
                _microphone.Content = "Mikrofonu yeniden dene";
                _status.Text = "Mikrofon başlatılamadı: " + exception.GetBaseException().Message;
                SetOrbMode("IDLE");
            }
        }
        finally
        {
            _microphoneStartCancellation?.Dispose();
            _microphoneStartCancellation = null;
            _microphoneStartTask = null;
            if (IsLoaded) _microphone.IsEnabled = true;
        }
    }

    private async Task StopMicrophoneAsync(bool updateStatus)
    {
        _microphoneStartCancellation?.Cancel();
        Task? microphoneStartTask = _microphoneStartTask;
        if (microphoneStartTask is not null) await microphoneStartTask;

        LocalSpeechInputService? speechInput = _speechInput;
        _speechInput = null;
        if (speechInput is not null)
        {
            await speechInput.DisposeAsync();
        }

        if (updateStatus && IsLoaded)
        {
            _microphone.Content = "Mikrofonu aç";
            _status.Text = "Mikrofon kapalı.";
            SetOrbMode("IDLE");
        }
    }

    private async Task HandleVoiceTranscriptAsync(string transcript)
    {
        string normalized = transcript.Trim();
        if (normalized.Length == 0) return;
        if (_wakeWord.IsChecked == true)
        {
            if (WakeWordDetector.TryMatch(normalized, out string command))
            {
                if (command.Length == 0)
                {
                    _wakeArmedUntil = DateTime.UtcNow + WakeWordDetector.ArmedWindow;
                    _status.Text = "Jarvis dinliyor… komutunuzu söyleyin (10 sn).";
                    SetOrbMode("LISTENING");
                    if (_speakReplies.IsChecked == true) QueueSpeech("Efendim?");
                    return;
                }

                normalized = command;
            }
            else if (DateTime.UtcNow <= _wakeArmedUntil)
            {
                _wakeArmedUntil = DateTime.MinValue;
            }
            else
            {
                _status.Text = "Uyandırma kelimesi bekleniyor: \"Hey Jarvis\" deyip komutunuzu söyleyin.";
                return;
            }
        }

        if (!IsVisible)
        {
            Show();
            Activate();
        }

        string lower = normalized.ToLowerInvariant();
        if (lower.Contains("sesli cevap verme", StringComparison.Ordinal) ||
            lower.Contains("sesli yanıt verme", StringComparison.Ordinal) ||
            lower.Contains("sessiz mod", StringComparison.Ordinal))
        {
            _speakReplies.IsChecked = false;
            _status.Text = "Sesli yanıt kapalı.";
            return;
        }

        if (lower.Contains("sesli cevap ver", StringComparison.Ordinal) ||
            lower.Contains("sesli yanıt ver", StringComparison.Ordinal) ||
            lower.Contains("yanıtları seslendir", StringComparison.Ordinal) ||
            lower.Contains("cevabı sesli oku", StringComparison.Ordinal))
        {
            _speakReplies.IsChecked = CanSpeak;
            _status.Text = CanSpeak
                ? "Sesli yanıt açıldı; ajan yanıtları yerel Türkçe sesle okunacak."
                : _speechStatus;
            return;
        }

        _message.Text = string.IsNullOrWhiteSpace(_message.Text)
            ? normalized
            : _message.Text.TrimEnd() + " " + normalized;
        _message.CaretIndex = _message.Text.Length;
        await SendAsync();
    }

    private bool TryConfigureTurkishVoice()
    {
        _speechOutput = LocalSpeechOutputService.TryCreate(out _speechStatus);
        return _speechOutput is not null;
    }

    private bool CanSpeak => _speechOutput is not null || _cloudVoice is not null || _piperVoice is not null;

    private void ConfigureCloudVoice()
    {
        if (PiperSpeechService.IsActive)
        {
            _piperVoice ??= new PiperSpeechService();
        }
        else if (_piperVoice is not null)
        {
            _piperVoice.Stop();
            _ = _piperVoice.DisposeAsync();
            _piperVoice = null;
        }

        if (ElevenLabsSettingsStore.IsActive)
        {
            _cloudVoice ??= new ElevenLabsSpeechService();
        }
        else if (_cloudVoice is not null)
        {
            _cloudVoice.Stop();
            _ = _cloudVoice.DisposeAsync();
            _cloudVoice = null;
        }

        _speakReplies.IsEnabled = CanSpeak;
        _speakReplies.ToolTip = _piperVoice is not null
            ? "Ücretsiz yerel Piper sesi etkin (hata durumunda: " + _speechStatus + ")"
            : _cloudVoice is not null
                ? "ElevenLabs sesi etkin (hata/kota durumunda: " + _speechStatus + ")"
                : _speechStatus;
    }

    private static string PrepareSpeech(string text)
    {
        string cleaned = System.Text.RegularExpressions.Regex.Replace(text, @"\[[^\]\r\n]{1,60}\]\s*", string.Empty);
        return cleaned.Replace("•", ",", StringComparison.Ordinal)
                      .Replace("–", ",", StringComparison.Ordinal)
                      .Replace("▸", string.Empty, StringComparison.Ordinal)
                      .Trim();
    }

    private void QueueSpeech(string text)
    {
        if (!CanSpeak) return;
        _pendingSpeech.Enqueue(text);
        if (!_isSpeaking) _ = ProcessSpeechQueueAsync();
    }

    private async Task SpeakOneAsync(string text)
    {
        string spoken = PrepareSpeech(text);
        if (_piperVoice is not null)
        {
            try
            {
                await _piperVoice.SpeakAsync(spoken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _status.Text = "Piper sesi kullanılamadı; yedek sese geçildi: " + exception.GetBaseException().Message;
            }
        }

        if (_cloudVoice is not null)
        {
            try
            {
                await _cloudVoice.SpeakAsync(spoken);
                return;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                _status.Text = "ElevenLabs kullanılamadı; Windows sesine geçildi: " + exception.GetBaseException().Message;
            }
        }

        if (_speechOutput is not null) await _speechOutput.SpeakAsync(spoken);
    }

    private async Task ProcessSpeechQueueAsync()
    {
        if (_isSpeaking || !CanSpeak) return;
        _isSpeaking = true;
        LocalSpeechInputService? pausedInput = _speechInput;
        try
        {
            if (pausedInput?.IsListening == true)
            {
                await pausedInput.PauseAsync();
            }

            while (_pendingSpeech.TryDequeue(out string? text))
            {
                SetOrbMode("SPEAKING");
                try
                {
                    await SpeakOneAsync(text);
                }
                catch (OperationCanceledException)
                {
                    if (!IsLoaded) return;
                }
                catch (Exception exception)
                {
                    _status.Text = "Türkçe seslendirme başarısız: " +
                        exception.GetBaseException().Message;
                }
            }
        }
        catch (Exception exception)
        {
            _pendingSpeech.Clear();
            _status.Text = "Mikrofon seslendirme için güvenli biçimde duraklatılamadı; sesli yanıt başlatılmadı: " +
                exception.GetBaseException().Message;
        }
        finally
        {
            if (ReferenceEquals(_speechInput, pausedInput) && pausedInput is not null)
            {
                try
                {
                    pausedInput.Resume();
                }
                catch (Exception exception)
                {
                    _status.Text = "Sesli yanıt sonrası mikrofon yeniden başlatılamadı: " +
                        exception.GetBaseException().Message;
                }
            }

            _isSpeaking = false;
            if (IsLoaded) SetOrbMode(_speechInput?.IsListening == true ? "LISTENING" : "IDLE");
        }
    }

    private void AddMessage(string author, string role, string content, bool isUser)
    {
        StackPanel bubbleContent = new();
        bubbleContent.Children.Add(new TextBlock
        {
            Text = isUser ? author : $"{author} · {role}",
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(isUser
                ? Color.FromRgb(0, 194, 242)
                : Color.FromRgb(43, 227, 160)),
            Margin = new Thickness(0, 0, 0, 4)
        });
        bubbleContent.Children.Add(new TextBlock
        {
            Text = content,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("InkBrush")
        });

        Border bubble = new()
        {
            Child = bubbleContent,
            Background = new SolidColorBrush(isUser
                ? Color.FromRgb(12, 38, 58)
                : Color.FromRgb(10, 32, 36)),
            BorderBrush = new SolidColorBrush(isUser
                ? Color.FromRgb(0, 120, 160)
                : Color.FromRgb(22, 110, 90)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 4, 0, 4),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        _messages.Children.Add(bubble);
        _chatScroll.ScrollToEnd();
    }

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);

    private async Task ImportLatestResearchReportAsync()
    {
        if (_researchReportCheckInProgress || !Directory.Exists(AgentWatcherPaths.ReportsDirectory))
        {
            return;
        }

        _researchReportCheckInProgress = true;
        try
        {
            string? latestReport = Directory.EnumerateFiles(AgentWatcherPaths.ReportsDirectory, "*.txt")
                .Where(path => IoPath.GetFileName(path).Contains("-erp-research-", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (latestReport is null)
            {
                return;
            }

            if (string.Equals(
                    _lastImportedResearchReportPath,
                    latestReport,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string report = await File.ReadAllTextAsync(latestReport);
            const int maximumReportLength = 12000;
            if (report.Length > maximumReportLength)
            {
                report = report[..maximumReportLength] + "\n\n[Rapor sohbet görünümü için kısaltıldı; tam rapor Raporlar klasöründe.]";
            }

            string directory = IoPath.GetDirectoryName(_researchReportCursorPath)
                ?? throw new InvalidOperationException("Gözcü rapor işaretçisi için yerel klasör belirlenemedi.");
            DateTime reportTime = File.GetLastWriteTime(latestReport);
            AddMessage(
                "Gözcü",
                $"Yeni ERP araştırma raporu · {reportTime:dd.MM.yyyy HH:mm}",
                report,
                false);
            _history.Add(new ErpChatTurn("assistant", $"[Gözcü Ar-Ge raporu]\n{report}"));
            _lastImportedResearchReportPath = latestReport;

            try
            {
                Directory.CreateDirectory(directory);
                string temporaryPath = _researchReportCursorPath + ".tmp";
                await File.WriteAllTextAsync(temporaryPath, latestReport);
                File.Move(temporaryPath, _researchReportCursorPath, true);
            }
            catch (Exception exception)
            {
                _status.Text = "Gözcü raporu sohbet alanına aktarıldı; yinelenmeyi önleme kaydı yazılamadı: "
                    + exception.GetBaseException().Message;
                return;
            }

            _status.Text = "Gözcü yeni Ar-Ge raporunu Ajan Sohbeti'ne aktardı.";
        }
        catch (Exception exception)
        {
            _status.Text = "Gözcü Ar-Ge raporu Ajan Sohbeti'ne aktarılamadı: "
                + exception.GetBaseException().Message;
        }
        finally
        {
            _researchReportCheckInProgress = false;
        }
    }
}
