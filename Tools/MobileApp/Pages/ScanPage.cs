using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace ArsanGazMobile.Pages;

public sealed class ScanPage : ContentPage
{
    private readonly TaskCompletionSource<string?> _result = new();
    private readonly CameraBarcodeReaderView _reader;

    public ScanPage()
    {
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

        Button cancel = new() { Text = "Vazgeç", Margin = new Thickness(16) };
        cancel.Clicked += async (_, _) => await CloseAsync(null);
        Button torch = new() { Text = "🔦 Fener", Margin = new Thickness(16) };
        torch.Clicked += (_, _) => _reader.IsTorchOn = !_reader.IsTorchOn;

        Grid root = new() { RowDefinitions = [new RowDefinition(GridLength.Star), new RowDefinition(GridLength.Auto)] };
        root.Add(_reader, 0, 0);
        root.Add(new HorizontalStackLayout { HorizontalOptions = LayoutOptions.Center, Children = { torch, cancel } }, 0, 1);
        Content = root;
    }

    public Task<string?> Result => _result.Task;

    public static async Task<string?> ScanAsync(INavigation navigation)
    {
        PermissionStatus status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("Barkod okutmak için kamera izni gerekli.");
        }

        ScanPage page = new();
        await navigation.PushModalAsync(page);
        return await page.Result;
    }

    private void OnDetected(object? sender, BarcodeDetectionEventArgs e)
    {
        string? value = e.Results?.FirstOrDefault()?.Value;
        if (string.IsNullOrWhiteSpace(value) || _result.Task.IsCompleted)
        {
            return;
        }

        MainThread.BeginInvokeOnMainThread(async () =>
        {
            try
            {
                Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(80));
            }
            catch (FeatureNotSupportedException)
            {
            }

            await CloseAsync(value.Trim());
        });
    }

    private async Task CloseAsync(string? value)
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
        _ = CloseAsync(null);
        return true;
    }
}
