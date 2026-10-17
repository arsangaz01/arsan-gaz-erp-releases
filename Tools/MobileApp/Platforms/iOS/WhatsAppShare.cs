namespace ArsanGazMobile.Services;

public static class WhatsAppShare
{
    public const string Personal = "whatsapp";
    public const string Business = "whatsapp-business";

    public static IReadOnlyList<string> InstalledPackages() => [];

    public static Task<string> StagePdfAsync(FileResult file) =>
        throw new InvalidOperationException("iPhone'da kampanya PDF paylaşımı henüz yok.");

    public static void Open(string package, string? phone, string text, string? pdfPath) =>
        throw new InvalidOperationException("iPhone'da WhatsApp paylaşımı henüz yok. Metni kopyalayıp WhatsApp'tan gönderin.");
}
