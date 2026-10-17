namespace ArsanGazMobile.Services;

// iOS: galeriden barkod — ZXing MAUI kamera yolu tercih; fotoğraf okuma sonraya.
internal static class ImageBarcodeReader
{
    public static Task<IReadOnlyList<string>> ReadAsync(Stream stream) =>
        throw new InvalidOperationException(
            "iPhone'da galeriden barkod okuma henüz yok. Canlı kamera veya elle kod girin.");
}
