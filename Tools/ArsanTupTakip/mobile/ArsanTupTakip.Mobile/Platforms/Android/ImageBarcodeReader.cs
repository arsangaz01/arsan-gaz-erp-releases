using Android.Graphics;
using ZXing;
using ZXing.Common;

namespace ArsanTupTakip.Mobile.Services;

// Finds barcodes in a still photo (gallery or camera) instead of the live camera preview.
internal static class ImageBarcodeReader
{
    // Larger photos are downsampled; this keeps memory reasonable while small labels stay readable.
    private const int MaxSide = 2400;

    private static readonly IList<BarcodeFormat> Formats =
    [
        BarcodeFormat.QR_CODE, BarcodeFormat.CODE_128, BarcodeFormat.CODE_39,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.DATA_MATRIX
    ];

    public static Task<IReadOnlyList<string>> ReadAsync(Stream stream) => Task.Run(() => Read(stream));

    private static IReadOnlyList<string> Read(Stream stream)
    {
        byte[] data;
        using (MemoryStream copy = new())
        {
            stream.CopyTo(copy);
            data = copy.ToArray();
        }

        BitmapFactory.Options bounds = new() { InJustDecodeBounds = true };
        BitmapFactory.DecodeByteArray(data, 0, data.Length, bounds);
        if (bounds.OutWidth <= 0 || bounds.OutHeight <= 0)
        {
            throw new InvalidOperationException("Görüntü açılamadı. JPG veya PNG bir fotoğraf seçin.");
        }

        int sample = 1;
        while (Math.Max(bounds.OutWidth, bounds.OutHeight) / sample > MaxSide)
        {
            sample *= 2;
        }

        using Bitmap bitmap = BitmapFactory.DecodeByteArray(data, 0, data.Length, new BitmapFactory.Options { InSampleSize = sample })
            ?? throw new InvalidOperationException("Görüntü açılamadı.");
        int width = bitmap.Width;
        int height = bitmap.Height;
        int[] pixels = new int[width * height];
        bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);

        byte[] gray = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            int pixel = pixels[i];
            gray[i] = (byte)((((pixel >> 16) & 0xFF) * 299 + ((pixel >> 8) & 0xFF) * 587 + (pixel & 0xFF) * 114) / 1000);
        }

        BarcodeReaderGeneric reader = new()
        {
            AutoRotate = true,
            Options = new DecodingOptions { TryHarder = true, TryInverted = true, PossibleFormats = Formats }
        };
        LuminanceSource source = new RGBLuminanceSource(gray, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
        Result[]? results = reader.DecodeMultiple(source);
        if (results is null or { Length: 0 })
        {
            Result? single = reader.Decode(source);
            results = single is null ? [] : [single];
        }

        return results
            .Select(result => (result.Text ?? string.Empty).Trim())
            .Where(text => text.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
