using Android.Graphics;
using ZXing;
using ZXing.Common;

namespace ArsanTupTakip.Mobile.Services;

// Finds barcodes in a still photo (gallery or camera). Multiple luminance / crop / upscale
// passes automatically "correct" glare and soft focus that defeat a single ZXing decode.
internal static class ImageBarcodeReader
{
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
        Collect(bitmap, found);
        if (found.Count > 0)
        {
            return found.ToList();
        }

        // Center crops: sticker fills more of the frame; glare at edges is cut away.
        foreach ((float left, float top, float right, float bottom) in new (float, float, float, float)[]
                 {
                     (0.08f, 0.08f, 0.92f, 0.92f),
                     (0.15f, 0.15f, 0.85f, 0.85f),
                     (0.22f, 0.18f, 0.78f, 0.72f),
                     (0.30f, 0.22f, 0.70f, 0.65f)
                 })
        {
            using Bitmap? crop = TryCrop(bitmap, left, top, right, bottom);
            if (crop is null)
            {
                continue;
            }

            Collect(crop, found);
            if (found.Count > 0)
            {
                break;
            }
        }

        return found.ToList();
    }

    private static void Collect(Bitmap bitmap, HashSet<string> found)
    {
        foreach (string text in DecodeBitmap(bitmap))
        {
            found.Add(text);
        }

        if (found.Count > 0)
        {
            return;
        }

        foreach (Bitmap variant in BuildBitmapVariants(bitmap))
        {
            using (variant)
            {
                foreach (string text in DecodeBitmap(variant))
                {
                    found.Add(text);
                }

                if (found.Count > 0)
                {
                    return;
                }
            }
        }
    }

    private static Bitmap? TryCrop(Bitmap bitmap, float left, float top, float right, float bottom)
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

        return w < 32 || h < 32 ? null : Bitmap.CreateBitmap(bitmap, x, y, w, h);
    }

    private static IEnumerable<Bitmap> BuildBitmapVariants(Bitmap source)
    {
        int width = source.Width;
        int height = source.Height;
        int[] pixels = new int[width * height];
        source.GetPixels(pixels, 0, width, 0, 0, width, height);
        byte[] gray = ToGray(pixels);

        yield return FromGray(ContrastStretch(gray), width, height);
        yield return FromGray(Unsharp(gray, width, height), width, height);

        if (width * height <= 1_200_000)
        {
            yield return FromGray(AdaptiveThreshold(gray, width, height, block: 31, bias: 8), width, height);
        }

        byte[] stretched = ContrastStretch(gray);
        int bigW = width * 2;
        int bigH = height * 2;
        if (bigW * bigH <= 4_000_000)
        {
            yield return FromGray(NearestUpscale2x(stretched, width, height), bigW, bigH);
        }
    }

    private static IEnumerable<string> DecodeBitmap(Bitmap bitmap)
    {
        int width = bitmap.Width;
        int height = bitmap.Height;
        int[] pixels = new int[width * height];
        bitmap.GetPixels(pixels, 0, width, 0, 0, width, height);
        byte[] gray = ToGray(pixels);

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

        foreach (byte[] luminance in BuildLuminanceVariants(gray, width, height))
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

    private static IEnumerable<byte[]> BuildLuminanceVariants(byte[] gray, int width, int height)
    {
        yield return gray;

        byte[] inverted = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            inverted[i] = (byte)(255 - gray[i]);
        }

        yield return inverted;
        yield return ContrastStretch(gray);
        yield return Unsharp(gray, width, height);
        if (width * height <= 1_200_000)
        {
            yield return AdaptiveThreshold(gray, width, height, block: 31, bias: 8);
        }
    }

    private static byte[] ToGray(int[] pixels)
    {
        byte[] gray = new byte[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            int pixel = pixels[i];
            gray[i] = (byte)((((pixel >> 16) & 0xFF) * 299 + ((pixel >> 8) & 0xFF) * 587 + (pixel & 0xFF) * 114) / 1000);
        }

        return gray;
    }

    private static byte[] ContrastStretch(byte[] gray)
    {
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
        byte[] result = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            result[i] = (byte)(((gray[i] - min) * 255) / span);
        }

        return result;
    }

    private static byte[] AdaptiveThreshold(byte[] gray, int width, int height, int block, int bias)
    {
        byte[] result = new byte[gray.Length];
        int radius = Math.Max(1, block / 2);
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(width - 1, x + radius);
                long sum = 0;
                int count = 0;
                for (int yy = y0; yy <= y1; yy++)
                {
                    int row = yy * width;
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        sum += gray[row + xx];
                        count++;
                    }
                }

                int mean = (int)(sum / Math.Max(1, count));
                result[y * width + x] = gray[y * width + x] < mean - bias ? (byte)0 : (byte)255;
            }
        }

        return result;
    }

    private static byte[] Unsharp(byte[] gray, int width, int height)
    {
        byte[] blur = BoxBlur(gray, width, height, radius: 1);
        byte[] result = new byte[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            result[i] = (byte)Math.Clamp(gray[i] * 2 - blur[i], 0, 255);
        }

        return result;
    }

    private static byte[] BoxBlur(byte[] gray, int width, int height, int radius)
    {
        byte[] result = new byte[gray.Length];
        for (int y = 0; y < height; y++)
        {
            int y0 = Math.Max(0, y - radius);
            int y1 = Math.Min(height - 1, y + radius);
            for (int x = 0; x < width; x++)
            {
                int x0 = Math.Max(0, x - radius);
                int x1 = Math.Min(width - 1, x + radius);
                long sum = 0;
                int count = 0;
                for (int yy = y0; yy <= y1; yy++)
                {
                    int row = yy * width;
                    for (int xx = x0; xx <= x1; xx++)
                    {
                        sum += gray[row + xx];
                        count++;
                    }
                }

                result[y * width + x] = (byte)(sum / Math.Max(1, count));
            }
        }

        return result;
    }

    private static byte[] NearestUpscale2x(byte[] gray, int width, int height)
    {
        byte[] result = new byte[width * height * 4];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                byte value = gray[y * width + x];
                int dx = x * 2;
                int dy = y * 2;
                int row0 = dy * (width * 2);
                int row1 = (dy + 1) * (width * 2);
                result[row0 + dx] = value;
                result[row0 + dx + 1] = value;
                result[row1 + dx] = value;
                result[row1 + dx + 1] = value;
            }
        }

        return result;
    }

    private static Bitmap FromGray(byte[] gray, int width, int height)
    {
        int[] pixels = new int[gray.Length];
        for (int i = 0; i < gray.Length; i++)
        {
            int g = gray[i];
            pixels[i] = unchecked((int)0xFF000000) | (g << 16) | (g << 8) | g;
        }

        Bitmap bitmap = Bitmap.CreateBitmap(width, height, Bitmap.Config.Argb8888!);
        bitmap.SetPixels(pixels, 0, width, 0, 0, width, height);
        return bitmap;
    }
}
