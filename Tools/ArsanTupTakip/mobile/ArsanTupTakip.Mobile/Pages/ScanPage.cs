using ArsanTupTakip.Mobile.Services;
using ZXing.Net.Maui;
using ZXing.Net.Maui.Controls;

namespace ArsanTupTakip.Mobile.Pages;

public sealed class ScanPage : ContentPage
{
    // Android's photo picker rejects limits above MediaStore.getPickImagesMaxLimit() (100 on most devices).
    private const int MaxGalleryImages = 50;
    private const string AddAllOption = "Tümünü ekle";

    private readonly TaskCompletionSource<IReadOnlyList<string>?> _result = new();
    private readonly CameraBarcodeReaderView _reader;
    private readonly bool _continuous;
    private readonly bool _allowMany;
    private readonly HashSet<string> _known;
    private readonly List<string> _codes = [];
    // Live mode: every new code is sent at once and the returned text is shown; one code at a time.
    private Func<string, Task<string>>? _live;
    private bool _liveBusy;
    private readonly Label _counter = new() { FontSize = 16, TextColor = Colors.White, HorizontalTextAlignment = TextAlignment.Center, Margin = new Thickness(12, 8) };

    private ScanPage(bool continuous, bool allowMany, IEnumerable<string>? known)
    {
        _continuous = continuous;
        _allowMany = allowMany;
        _known = new HashSet<string>(known ?? [], StringComparer.OrdinalIgnoreCase);
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
        }

        _counter.IsVisible = continuous;
        root.Add(_counter, 0, 0);
        root.Add(_reader, 0, 1);
        root.Add(buttons, 0, 2);
        Content = root;
    }

    public static async Task<string?> ScanAsync(INavigation navigation)
    {
        IReadOnlyList<string>? codes = await OpenAsync(navigation, continuous: false, allowMany: false, known: null);
        return codes?.FirstOrDefault();
    }

    // Single scan, but when several codes come from gallery images the user may add them all at once.
    public static async Task<IReadOnlyList<string>> ScanOneOrManyAsync(INavigation navigation, IEnumerable<string>? known = null) =>
        await OpenAsync(navigation, continuous: false, allowMany: true, known) ?? [];

    public static async Task<IReadOnlyList<string>> ScanManyAsync(INavigation navigation, IEnumerable<string>? known = null) =>
        await OpenAsync(navigation, continuous: true, allowMany: true, known) ?? [];

    // Continuous scanning where each code is handled as soon as it is read (e.g. depot count); onCode returns the line to show.
    public static async Task ScanLiveAsync(INavigation navigation, Func<string, Task<string>> onCode, string intro) =>
        await OpenAsync(navigation, continuous: true, allowMany: true, known: null, page => { page._live = onCode; page._counter.Text = intro; });

    private static async Task<IReadOnlyList<string>?> OpenAsync(INavigation navigation, bool continuous, bool allowMany, IEnumerable<string>? known,
        Action<ScanPage>? setup = null)
    {
        PermissionStatus status = await Permissions.RequestAsync<Permissions.Camera>();
        if (status != PermissionStatus.Granted)
        {
            throw new InvalidOperationException("Barkod okutmak için kamera izni gerekli.");
        }

        ScanPage page = new(continuous, allowMany, known);
        setup?.Invoke(page);
        await navigation.PushModalAsync(page);
        return await page._result.Task;
    }

    private async Task SendLiveAsync(IReadOnlyList<string> codes)
    {
        _liveBusy = true;
        try
        {
            foreach (string code in codes)
            {
                _codes.Add(code);
                try
                {
                    _counter.Text = await _live!(code);
                    Buzz();
                }
                catch (Exception exception)
                {
                    _codes.Remove(code);
                    _counter.Text = $"❌ {code}: {exception.Message}";
                }
            }
        }
        finally
        {
            _liveBusy = false;
        }
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
            if (_live is not null)
            {
                if (!_liveBusy && !_codes.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    await SendLiveAsync([value]);
                }

                return;
            }

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
        string previousCounter = _counter.Text ?? string.Empty;
        try
        {
            List<FileResult> files = [];
            if (source == "Fotoğraf çek")
            {
                FileResult? shot = await MediaPicker.Default.CapturePhotoAsync(new MediaPickerOptions { Title = "Barkodun fotoğrafı" });
                if (shot is not null)
                {
                    files.Add(shot);
                }
            }
            else
            {
                List<FileResult>? picked = await MediaPicker.Default.PickPhotosAsync(
                    new MediaPickerOptions { Title = "Barkodlu görüntüler", SelectionLimit = MaxGalleryImages });
                files.AddRange(picked?.OfType<FileResult>() ?? []);
            }

            if (files.Count == 0)
            {
                return;
            }

            if (files.Count == 1)
            {
                await HandleFoundAsync(await ReadImageAsync(files[0]), imageCount: 1, emptyImages: 0);
                return;
            }

            // One image at a time: each decode holds a full-size bitmap, so parallel reads risk running out of memory.
            List<string> found = [];
            int emptyImages = 0;
            _counter.IsVisible = true;
            for (int i = 0; i < files.Count; i++)
            {
                _counter.Text = $"🖼 {i + 1}/{files.Count} resim okunuyor… · {found.Count} kod";
                IReadOnlyList<string> codes;
                try
                {
                    codes = await ReadImageAsync(files[i]);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    codes = [];
                }

                if (codes.Count == 0)
                {
                    emptyImages++;
                }

                found.AddRange(codes);
            }

            _counter.Text = previousCounter;
            _counter.IsVisible = _continuous;
            await HandleFoundAsync(found.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), files.Count, emptyImages);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Görüntü", exception.Message, "Tamam");
        }
        finally
        {
            if (!_result.Task.IsCompleted)
            {
                if (_counter.Text?.Contains("okunuyor", StringComparison.Ordinal) == true)
                {
                    _counter.Text = previousCounter;
                }

                _counter.IsVisible = _continuous;
                _reader.IsDetecting = true;
            }
        }
    }

    private static async Task<IReadOnlyList<string>> ReadImageAsync(FileResult file)
    {
        await using Stream stream = await file.OpenReadAsync();
        return await ImageBarcodeReader.ReadAsync(stream);
    }

    private async Task HandleFoundAsync(IReadOnlyList<string> found, int imageCount, int emptyImages)
    {
        List<string> fresh = found
            .Where(code => !_known.Contains(code) && !_codes.Contains(code, StringComparer.OrdinalIgnoreCase))
            .ToList();
        string summary = $"{imageCount} resimden {found.Count} kod bulundu, {fresh.Count} yeni";
        if (emptyImages > 0)
        {
            summary += $". {emptyImages} resimde okunabilir barkod yok";
        }

        if (found.Count == 0)
        {
            await DisplayAlertAsync("Barkod bulunamadı", imageCount == 1
                ? "Görüntüde okunabilir barkod veya QR kod yok. Barkodun net, düz ve tamamen göründüğü bir fotoğraf deneyin."
                : $"{imageCount} resmin hiçbirinde okunabilir barkod veya QR kod yok. Barkodun net, düz ve tamamen göründüğü fotoğraflar deneyin.", "Tamam");
            return;
        }

        if (_live is not null)
        {
            await SendLiveAsync(fresh);
            return;
        }

        Buzz();
        if (_continuous)
        {
            _codes.AddRange(fresh);
            _counter.Text = $"🖼 {summary} · toplam {_codes.Count} tüp";
            if (imageCount > 1)
            {
                await DisplayAlertAsync("Galeriden okuma", summary + ".", "Tamam");
            }

            return;
        }

        if (found.Count == 1)
        {
            if (emptyImages > 0)
            {
                await DisplayAlertAsync("Galeriden okuma", summary + ".", "Tamam");
            }

            await CloseAsync([found[0]]);
            return;
        }

        string[] options = _allowMany ? found.Prepend(AddAllOption).ToArray() : found.ToArray();
        string? chosen = await DisplayActionSheetAsync(summary, "Vazgeç", null, options);
        if (_allowMany && chosen == AddAllOption)
        {
            await CloseAsync(found);
        }
        else if (chosen is not null && found.Contains(chosen))
        {
            await CloseAsync([chosen]);
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
