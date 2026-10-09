using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed class VoiceSettingsWindow : Window
{
    private readonly CheckBox _enabled = new() { Content = "Yanıtları ElevenLabs sesiyle oku (hata/kota dolarsa Windows Türkçe sesi kullanılır)" };
    private readonly PasswordBox _apiKey = new() { Padding = new Thickness(6, 4, 6, 4) };
    private readonly ComboBox _voices = new() { IsEditable = true, Padding = new Thickness(6, 4, 6, 4) };
    private readonly Button _listVoices = new() { Content = "Sesleri listele", Padding = new Thickness(10, 5, 10, 5), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _test = new() { Content = "Dene", Padding = new Thickness(12, 6, 12, 6) };
    private readonly Button _save = new() { Content = "Kaydet", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly Button _remove = new() { Content = "Anahtarı kaldır", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(8, 0, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ElevenLabsSpeechService _player = new();

    public VoiceSettingsWindow()
    {
        Title = "Jarvis Ses Ayarları — ElevenLabs";
        Width = 640;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = ThemeBrush("CanvasBrush");
        Content = BuildLayout();

        ElevenLabsSettings settings = ElevenLabsSettingsStore.Load();
        _enabled.IsChecked = settings.Enabled;
        _voices.Text = settings.VoiceId;
        _status.Text = settings.HasApiKey
            ? "API anahtarı kayıtlı (Windows hesabınıza şifreli). Değiştirmek için yeni anahtar girin."
            : "API anahtarı kayıtlı değil. elevenlabs.io → Profile → API Keys bölümünden oluşturun.";

        _listVoices.Click += async (_, _) => await ListVoicesAsync();
        _test.Click += async (_, _) => await TestAsync();
        _save.Click += (_, _) => Save();
        _remove.Click += (_, _) =>
        {
            ElevenLabsSettingsStore.Remove();
            _enabled.IsChecked = false;
            _apiKey.Clear();
            _voices.Text = string.Empty;
            _status.Text = "ElevenLabs anahtarı ve ayarları silindi; Jarvis Windows sesini kullanacak.";
        };
        Closed += async (_, _) => await _player.DisposeAsync();
    }

    private UIElement BuildLayout()
    {
        StackPanel root = new() { Margin = new Thickness(18) };
        root.Children.Add(new TextBlock
        {
            Text = "ElevenLabs ses seçeneği",
            FontSize = 21,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeBrush("InkBrush")
        });
        root.Children.Add(new TextBlock
        {
            Text = "Daha doğal Jarvis sesi için kendi ElevenLabs hesabınızı bağlayın (Starter ≈ 5-6 $/ay, aylık ~30.000 karakter). " +
                   "Her yanıtın en fazla 900 karakteri okunur. Okunacak metin ElevenLabs'e gönderilir.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("MutedBrush"),
            Margin = new Thickness(0, 4, 0, 12)
        });
        root.Children.Add(Label("API anahtarı (xi-api-key)"));
        root.Children.Add(_apiKey);
        root.Children.Add(Label("Ses (Voice ID) — listeden seçin veya ID yapıştırın"));
        DockPanel voiceRow = new();
        DockPanel.SetDock(_listVoices, Dock.Right);
        voiceRow.Children.Add(_listVoices);
        voiceRow.Children.Add(_voices);
        root.Children.Add(voiceRow);
        _enabled.Margin = new Thickness(0, 12, 0, 0);
        _enabled.Foreground = ThemeBrush("InkBrush");
        root.Children.Add(_enabled);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 0) };
        buttons.Children.Add(_test);
        buttons.Children.Add(_save);
        buttons.Children.Add(_remove);
        root.Children.Add(buttons);
        _status.Foreground = ThemeBrush("MutedBrush");
        root.Children.Add(_status);
        return root;
    }

    private async Task ListVoicesAsync()
    {
        string? key = CurrentKey();
        if (key is null) return;
        _listVoices.IsEnabled = false;
        _status.Text = "Sesler alınıyor…";
        try
        {
            IReadOnlyList<ElevenLabsVoice> voices = await ElevenLabsSpeechService.ListVoicesAsync(key, CancellationToken.None);
            string current = SelectedVoiceId();
            _voices.Items.Clear();
            foreach (ElevenLabsVoice voice in voices)
            {
                _voices.Items.Add(new ComboBoxItem { Content = $"{voice.Name} · {voice.Id}", Tag = voice.Id });
            }

            ComboBoxItem? match = _voices.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == current);
            if (match is not null) _voices.SelectedItem = match;
            _status.Text = $"{voices.Count} ses bulundu. Türkçe için \"multilingual\" destekli herhangi bir ses kullanılabilir.";
        }
        catch (Exception exception)
        {
            _status.Text = "Sesler alınamadı: " + exception.GetBaseException().Message;
        }
        finally
        {
            _listVoices.IsEnabled = true;
        }
    }

    private async Task TestAsync()
    {
        string? key = CurrentKey();
        string voice = SelectedVoiceId();
        if (key is null) return;
        if (voice.Length == 0)
        {
            _status.Text = "Önce bir ses seçin.";
            return;
        }

        _test.IsEnabled = false;
        _status.Text = "Deneme sesi hazırlanıyor…";
        try
        {
            await _player.SpeakAsync("Merhaba, ben Jarvis. Arsan Gaz ERP sesli asistanınız hazır.", voice, key);
            _status.Text = "Deneme başarılı. Beğendiyseniz Kaydet'e basın.";
        }
        catch (Exception exception)
        {
            _status.Text = "Deneme başarısız: " + exception.GetBaseException().Message;
        }
        finally
        {
            _test.IsEnabled = true;
        }
    }

    private void Save()
    {
        try
        {
            string voice = SelectedVoiceId();
            bool enabled = _enabled.IsChecked == true;
            if (enabled && (voice.Length == 0 || (!ElevenLabsSettingsStore.Load().HasApiKey && _apiKey.Password.Trim().Length == 0)))
            {
                _status.Text = "Etkinleştirmek için API anahtarı ve ses seçimi gerekli.";
                return;
            }

            ElevenLabsSettingsStore.Save(enabled, voice, _apiKey.Password);
            _apiKey.Clear();
            _status.Text = enabled
                ? "Kaydedildi. Ajan Sohbeti'nde \"Yanıtları seslendir\" açıkken Jarvis ElevenLabs sesiyle konuşur."
                : "Kaydedildi (ElevenLabs kapalı; Windows sesi kullanılır).";
        }
        catch (Exception exception)
        {
            _status.Text = "Kaydedilemedi: " + exception.GetBaseException().Message;
        }
    }

    private string? CurrentKey()
    {
        string typed = _apiKey.Password.Trim();
        if (typed.Length > 0) return typed;
        try
        {
            return ElevenLabsSettingsStore.GetApiKey();
        }
        catch (InvalidOperationException exception)
        {
            _status.Text = exception.Message + " API anahtarını girin.";
            return null;
        }
    }

    private string SelectedVoiceId() =>
        _voices.SelectedItem is ComboBoxItem { Tag: string id }
            ? id
            : (_voices.Text.Split('·').LastOrDefault() ?? string.Empty).Trim();

    private TextBlock Label(string text) => new()
    {
        Text = text,
        FontWeight = FontWeights.SemiBold,
        Foreground = ThemeBrush("InkBrush"),
        Margin = new Thickness(0, 8, 0, 4)
    };

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);
}
