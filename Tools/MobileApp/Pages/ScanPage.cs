using ArsanGazMobile.Services;
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

        Button cancel = new() { Text = continuous ? "Bitti" : "Vazgeç" };
        cancel.Clicked += async (_, _) => await CloseAsync(continuous ? _codes : null);
        Button torch = new() { Text = "🔦 Fener" };
        torch.Clicked += (_, _) => _reader.IsTorchOn = !_reader.IsTorchOn;
        Button photo = new() { Text = "🖼 Fotoğraftan" };
        photo.Clicked += async (_, _) => await ReadFromPhotoAsync();

        Grid buttons = new()
        {
            Padding = new Thickness(12, 12, 12, 20),
            ColumnSpacing = 8,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)]
        };
        buttons.Add(torch, 0, 0);
        buttons.Add(photo, 1, 0);
        buttons.Add(cancel, 2, 0);

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
        root.Add(buttons, 0, 2);
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

    private async Task ReadFromPhotoAsync()
    {
        string? source = await DisplayActionSheetAsync("Barkodu görüntüden oku", "Vazgeç", null, "Galeriden seç", "Fotoğraf çek");
        if (source is not ("Galeriden seç" or "Fotoğraf çek"))
        {
            return;
        }

        _reader.IsDetecting = false;
        try
        {
            FileResult? file = source == "Fotoğraf çek"
                ? await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { Title = "Barkodun fotoğrafı" })
                : (await MediaPicker.Default.PickPhotosAsync(new MediaPickerOptions { Title = "Barkodlu görüntü", SelectionLimit = 1 }))?.FirstOrDefault();
            if (file is null)
            {
                return;
            }

            IReadOnlyList<string> found;
            await using (Stream stream = await file.OpenReadAsync())
            {
                found = await ImageBarcodeReader.ReadAsync(stream);
            }

            if (found.Count == 0)
            {
                await DisplayAlertAsync("Barkod bulunamadı",
                    "Görüntüde okunabilir barkod veya QR kod yok. Barkodun net, düz ve tamamen göründüğü bir fotoğraf deneyin.", "Tamam");
                return;
            }

            Buzz();
            if (_continuous)
            {
                List<string> fresh = found.Where(code => !_codes.Contains(code, StringComparer.OrdinalIgnoreCase)).ToList();
                _codes.AddRange(fresh);
                _counter.Text = $"🖼 Görüntüden {found.Count} kod bulundu, {fresh.Count} yeni · toplam {_codes.Count} tüp";
                return;
            }

            string? chosen = found.Count == 1
                ? found[0]
                : await DisplayActionSheetAsync("Görüntüde birden çok kod var", "Vazgeç", null, found.ToArray());
            if (chosen is not null && found.Contains(chosen))
            {
                await CloseAsync([chosen]);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            await DisplayAlertAsync("Görüntü", exception.Message, "Tamam");
        }
        finally
        {
            if (!_result.Task.IsCompleted)
            {
                _reader.IsDetecting = true;
            }
        }
    }

    private static void Buzz()
    {
        try
        {
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(80));
        }
        catch (Exception exception) when (exception is FeatureNotSupportedException or PermissionException)
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
