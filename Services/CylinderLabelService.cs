using System;
using System.IO;
using System.Windows.Media.Imaging;
using CodeGlyphX;
using CodeGlyphX.Rendering;

namespace ArsanGazERP.Services;

public static class CylinderLabelService
{
    public static BitmapImage QrImage(string code) => Render(stream =>
        QR.Save(Require(code), stream, OutputFormat.Png, new QrRenderOptions { ModuleSize = 8, QuietZone = 2 }));

    public static BitmapImage Code128Image(string code) => Render(stream =>
        Barcode.Save(SymbolFormat.Code128, Require(code), stream, OutputFormat.Png, new BarcodeOptions { ModuleSize = 2, HeightModules = 40 }));

    private static string Require(string code) =>
        string.IsNullOrWhiteSpace(code) ? throw new ArgumentException("Etiket için barkod veya seri numarası gerekli.", nameof(code)) : code.Trim();

    private static BitmapImage Render(Action<Stream> write)
    {
        using MemoryStream stream = new();
        write(stream);
        stream.Position = 0;
        BitmapImage image = new();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }
}
