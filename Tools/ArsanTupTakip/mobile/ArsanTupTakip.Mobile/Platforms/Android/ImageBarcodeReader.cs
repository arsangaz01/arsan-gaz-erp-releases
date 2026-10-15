using Android.Graphics;
using ZXing;
using ZXing.Common;

namespace ArsanTupTakip.Mobile.Services;

// Finds barcodes in a still photo (gallery or camera) instead of the live camera preview.
internal static class ImageBarcodeReader
{
    // Curved glossy cylinder stickers need more pixels; memory stays bounded by one bitmap at a time.
    private const int MaxSide = 3200;

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

        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        foreach (string text in DecodeBitmap(bitmap))
        {
            found.Add(text);
        }

        // Center crops help when the sticker is small or glare sits at the edges.
        foreach ((float left, float top, float right, float bottom) in new (float, float, float, float)[]
                 {
                     (0.10f, 0.10f, 0.90f, 0.90f),
                     (0.18f, 0.18f, 0.82f, 0.82f),
                     (0.25f, 0.20f, 0.75f, 0.70f)
                 })
        {
            int x = (int)(bitmap.Width * left);
            int y = (int)(bitmap.Height * top);
            int w = Math.Max(32, (int)(bitmap.Width * (right - left)));
            int h = Math.Max(32, (int)(bitmap.Height * (bottom - top)));
            if (x + w > bitmap.Width)
            {
                w = bitmap.Width - x;
            }

            if (y + h > bitmap.Height)
            {
                h = bitmap.Height - y;
            }

            if (w < 32 || h < 32)
            {
                continue;
            }

            using Bitmap crop = Bitmap.CreateBitmap(bitmap, x, y, w, h);
            foreach (string text in DecodeBitmap(crop))
            {
                found.Add(text);
            }

            if (found.Count > 0)
            {
                break;
            }
        }

        return found.ToList();
    }

    private static IEnumerable<string> DecodeBitmap(Bitmap bitmap)
    {
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
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = Formats,
                PureBarcode = false
            }
        };

        foreach (byte[] luminance in BuildLuminanceVariants(gray))
        {
            LuminanceSource source = new RGBLuminanceSource(luminance, width, height, RGBLuminanceSource.BitmapFormat.Gray8);
            Result[]? results = reader.DecodeMultiple(source);
            if (results is null or { Length: 0 })
            {
                Result? single = reader.Decode(source);
                results = single is null ? [] : [single];
            }

            foreach (Result result in results)
            {
                string text = (result.Text ?? string.Empty).Trim();
                if (text.Length > 0)
                {
                    yield return text;
                }
            }
        }
    }

    // Extra passes for glare / underexposure on blue cylinders under clear tape.
    private static IEnumerable<byte[]> BuildLuminanceVariants(byte[] gray)
    {
        yield return gray;

        byte[] inverted = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            inverted[i] = (byte)(255 - gray[i]);
        }

        yield return inverted;

        byte[] stretched = new byte[gray.Length];
        byte min = 255, max = 0;
        foreach (byte value in gray)
        {
            if (value < min)
            {
                min = value;
            }

            if (value > max)
            {
                max = value;
            }
        }

        int span = Math.Max(1, max - min);
        for (int i = 0; i < gray.Length; i++)
        {
            stretched[i] = (byte)(((gray[i] - min) * 255) / span);
        }

        yield return stretched;
    }
}
