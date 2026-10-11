using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace ArsanGazMobile.Pages;

public sealed class ScanPage : ContentPage
{
    private readonly TaskCompletionSource<IReadOnlyList<string>?> _result = new();
    private readonly CameraBarcodeReaderView _reader;
    private readonly bool _continuous;
    private readonly List<string> _codes = [];
    private readonly Label _counter = new() { FontSize = 16, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(12, 8) };

    private ScanPage(bool continuous)
    {
        _continuous = continuous;
        Title = "Barkod okut";
        _reader = new CameraBarcodeReaderView
        {
            Options = new BarcodeReaderOptions
            {
                Formats = BarcodeFormat.QrCode | BarcodeFormat.Code128 | BarcodeFormat.Code39 | BarcodeFormat.Ean13 | BarcodeFormat.Ean8 | BarcodeFormat.DataMatrix,
                AutoRotate = true,
                Multiple = false
            }
        };
        _reader.BarcodesDetected += OnDetected;

        Button cancel = new() { Text = continuous ? "Bitti" : "Vazgeç", Margin = new Thickness(16) };
        cancel.Clicked += async (_, _) => await CloseAsync(continuous ? _codes : null);
        Button torch = new() { Text = "🔦 Fener", Margin = new Thickness(16) };
        torch.Clicked += (_, _) => _reader.IsTorchOn = !_reader.IsTorchOn;

        Grid root = new()
        {
            RowDefinitions = [new RowDefinition(GridLength.Auto), new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)],
            BackgroundColor = Colors.Black
        };
        if (continuous)
        {
            _counter.Text = "Tüpleri sırayla okutun. Bitince \"Bitti\"ye basın.";
            root.Add(_counter, 0, 0);
        }

        root.Add(_reader, 0, 1);
        root.Add(new HorizontalStackLayout { HorizontalOptions = LayoutOptions.Center, Children = { torch, cancel } }, 0, 2);
        Content = root;
    }

    public static async Task<string?> ScanAsync(INavigation navigation)
    {
        IReadOnlyList<string>? codes = await OpenAsync(navigation, continuous: false);
        return codes?.FirstOrDefault();
    }

    public static async Task<IReadOnlyList<string>> ScanManyAsync(INavigation navigation) =>
        await OpenAsync(navigation, continuous: true) ?? [];

    private static async Task<IReadOnlyList<string>?> OpenAsync(INavigation navigation, bool continuous)
    {
        PermissionStatus status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("Barkod okutmak için kamera izni gerekli.");
        }

        ScanPage page = new(continuous);
        await navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        string? value = e.Results?.FirstOrDefault()?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(value) || _result.Task.IsCompleted)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (_continuous)
            {
                if (_codes.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    return;
                }

                _codes.Add(value);
                _counter.Text = $"✅ {_codes.Count} tüp okundu · son: {value}";
                Buzz();
                return;
            }

            Buzz();
            await CloseAsync([value]);
        });
    }

    private static void Buzz()
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(80));
        }
        catch (FeatureNotSupportedException)
        {
        }
    }

    private async Task CloseAsync(IReadOnlyList<string>? value)
    {
        if (!_result.TrySetResult(value))
        {
            return;
        }

        _reader.IsDetecting = false;
        await Navigation.PopModalAsync();
    }

    protected override bool OnBackButtonPressed()
    {
        _ = CloseAsync(_continuous ? _codes : null);
        return true;
    }
}
