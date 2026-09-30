using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
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
        _service = service; Title = "NVIDIA NIM Ayarlari"; Width = 590; Height = 390; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.NoResize;
        _model.ItemsSource = new[] { "meta/llama-3.1-8b-instruct", "microsoft/phi-4-mini-instruct", "qwen/qwen2.5-coder-32b-instruct", "nvidia/llama-3.3-nemotron-super-49b-v1.5" }; _model.Text = service.Model;
        StackPanel form = new() { Margin = new Thickness(24) };
        form.Children.Add(new TextBlock { Text = service.IsConfigured ? "NVIDIA NIM (Bagli)" : "NVIDIA NIM (Bagli degil)", Foreground = service.IsConfigured ? System.Windows.Media.Brushes.Green : System.Windows.Media.Brushes.Red, FontWeight = FontWeights.SemiBold });
        form.Children.Add(new TextBlock { Text = "Yeni API anahtari", Margin = new Thickness(0, 16, 0, 0) }); form.Children.Add(_key); form.Children.Add(new TextBlock { Text = "Model" }); form.Children.Add(_model);
        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        Button save = new() { Content = "Kaydet ve Test Et", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        Button test = new() { Content = "Baglantiyi Test Et", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        Button remove = new() { Content = "Anahtari Sil", Padding = new Thickness(16, 8, 16, 8) };
        save.Click += async (_, _) => await SaveAndTestAsync(); test.Click += async (_, _) => await TestAsync(); remove.Click += (_, _) => { _service.Remove(); _status.Text = "NVIDIA anahtari kaldirildi."; };
        buttons.Children.Add(save); buttons.Children.Add(test); buttons.Children.Add(remove); form.Children.Add(buttons); form.Children.Add(_status); Content = form;
    }
    private async Task SaveAndTestAsync() { try { _service.Save(_key.Password, _model.Text); _key.Clear(); _status.Text = "Baglanti basarili: " + await _service.TestAsync(); } catch (Exception ex) { _status.Text = ex.GetBaseException().Message; } }
    private async Task TestAsync() { try { _status.Text = "Test ediliyor..."; _status.Text = "Baglanti basarili: " + await _service.TestAsync(); } catch (Exception ex) { _status.Text = ex.GetBaseException().Message; } }
}
