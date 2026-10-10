using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed class NvidiaNimWindow : Window
{
    private readonly NvidiaNimService _service;
    private readonly PasswordBox _key = new() { Margin = new Thickness(0, 6, 0, 12) };
    private readonly ComboBox _model = new() { Margin = new Thickness(0, 6, 0, 12), IsEditable = true };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };

    public NvidiaNimWindow(NvidiaNimService service)
    {
        _service = service; Title = "NVIDIA NIM ve Ücretsiz Yedek Sağlayıcılar"; Width = 640; Height = 760; MinWidth = 520; MinHeight = 420; WindowWorkAreaService.Fit(this); WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        _model.ItemsSource = new[] { "deepseek-ai/deepseek-v4.1-flash", "microsoft/phi-4-mini-instruct", "qwen/qwen2.5-coder-32b-instruct", "nvidia/llama-3.3-nemotron-super-49b-v1.5" }; _model.Text = service.Model;
        StackPanel form = new() { Margin = new Thickness(24) };
        form.Children.Add(new TextBlock { Text = service.IsConfigured ? "NVIDIA NIM (Bağlı)" : "NVIDIA NIM (Bağlı değil)", Foreground = (Brush)Application.Current.FindResource(service.IsConfigured ? "SuccessBrush" : "DangerBrush"), FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = "Yeni API anahtarı", Margin = new Thickness(0, 16, 0, 0) }); form.Children.Add(_key); form.Children.Add(new TextBlock { Text = "Model" }); form.Children.Add(_model);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        Button save = new() { Content = "Kaydet ve Test Et", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        Button test = new() { Content = "Bağlantıyı Test Et", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        Button remove = new() { Content = "Anahtarı Sil", Padding = new Thickness(16, 8, 16, 8), Style = (Style)Application.Current.FindResource("DangerButton") };
        save.Click += async (_, _) => await SaveAndTestAsync(); test.Click += async (_, _) => await TestAsync(); remove.Click += (_, _) => { _service.Remove(); _status.Text = "NVIDIA anahtarı kaldırıldı."; };
        buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(remove); form.Children.Add(buttons); form.Children.Add(_status);

        form.Children.Add(new TextBlock { Text = "Ücretsiz yedek sağlayıcılar", FontWeight = FontWeights.SemiBold, FontSize = 15, Margin = new Thickness(0, 28, 0, 4) });
        form.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Text = "Cloudflare ve NVIDIA yanıt vermezse sırayla denenir. Anahtarlar bu Windows kullanıcısı için şifreli saklanır ve Agent Control Center ile ortak kullanılır." });
        foreach (FreeLlmProvider provider in FreeLlmChain.Providers) form.Children.Add(BuildProviderPanel(provider));
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static Border BuildProviderPanel(FreeLlmProvider provider)
    {
        TextBlock header = new() { FontWeight = FontWeights.SemiBold };
        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
        PasswordBox key = new() { Margin = new Thickness(0, 6, 0, 8) };
        void Refresh()
        {
            bool configured = FreeLlmChain.HasKey(provider.Id);
            header.Text = $"{provider.Name} ({(configured ? "Bağlı · " + FreeLlmChain.GetModel(provider.Id) : "Bağlı değil")})";
            header.Foreground = (Brush)Application.Current.FindResource(configured ? "SuccessBrush" : "DangerBrush");
        }

        async Task RunTestAsync()
        {
            status.Text = "Test ediliyor...";
            try
            {
                using CancellationTokenSource timeout = new(TimeSpan.FromMinutes(2));
                status.Text = await FreeLlmChain.TestAsync(provider.Id, timeout.Token);
            }
            catch (Exception exception)
            {
                status.Text = exception.GetBaseException().Message;
            }

            Refresh();
        }

        Button save = new() { Content = "Kaydet ve Test Et", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        save.Click += async (_, _) =>
        {
            try
            {
                FreeLlmChain.SaveKey(provider.Id, key.Password);
                key.Clear();
            }
            catch (Exception exception)
            {
                status.Text = exception.GetBaseException().Message;
                return;
            }

            await RunTestAsync();
        };
        Button test = new() { Content = "Test Et", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        test.Click += async (_, _) => await RunTestAsync();
        Button remove = new() { Content = "Sil", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("DangerButton") };
        remove.Click += (_, _) => { FreeLlmChain.RemoveKey(provider.Id); status.Text = "Anahtar kaldırıldı."; Refresh(); };
        Button getKey = new() { Content = "Ücretsiz anahtar al", Padding = new Thickness(12, 6, 12, 6), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        getKey.Click += (_, _) => Process.Start(new ProcessStartInfo(provider.KeyUrl) { UseShellExecute = true });

        StackPanel actions = new() { Orientation = Orientation.Horizontal };
        actions.Children.Add(save); actions.Children.Add(test); actions.Children.Add(remove); actions.Children.Add(getKey);
        StackPanel panel = new();
        panel.Children.Add(header);
        panel.Children.Add(new TextBlock { Text = provider.Note, TextWrapping = TextWrapping.Wrap, Opacity = 0.8, Margin = new Thickness(0, 2, 0, 0) });
        panel.Children.Add(key);
        panel.Children.Add(actions);
        panel.Children.Add(status);
        Refresh();
        return new Border
        {
            Child = panel,
            Padding = new Thickness(12),
            Margin = new Thickness(0, 6, 0, 6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.FindResource("BorderBrush"),
            CornerRadius = new CornerRadius(6),
        };
    }

    private async Task SaveAndTestAsync() { try { _service.Save(_key.Password, _model.Text); _key.Clear(); _status.Text = "Bağlantı başarılı: " + await _service.TestAsync(); } catch (Exception ex) { _status.Text = ex.GetBaseException().Message; } }
    private async Task TestAsync() { try { _status.Text = "Test ediliyor..."; _status.Text = "Bağlantı başarılı: " + await _service.TestAsync(); } catch (Exception ex) { _status.Text = ex.GetBaseException().Message; } }
}
