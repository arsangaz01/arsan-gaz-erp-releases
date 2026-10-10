using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.Win32;

namespace ArsanGazERP.Views;

public sealed class CylinderLabelWindow : Window
{
    private readonly Border _label;
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly string _code;

    public CylinderLabelWindow(Cylinder cylinder)
    {
        _code = string.IsNullOrWhiteSpace(cylinder.Barcode) ? cylinder.SerialNumber : cylinder.Barcode;
        Title = $"Tüp etiketi · {cylinder.SerialNumber}";
        Width = 520; Height = 560; MinWidth = 420; MinHeight = 460;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowWorkAreaService.Fit(this);

        StackPanel details = new() { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        details.Children.Add(new TextBlock { Text = "ARSAN GAZ", FontWeight = FontWeights.Bold, FontSize = 16 });
        details.Children.Add(new TextBlock { Text = $"Seri no: {cylinder.SerialNumber}", Margin = new Thickness(0, 6, 0, 0) });
        details.Children.Add(new TextBlock { Text = $"Gaz: {cylinder.GasType}" });
        details.Children.Add(new TextBlock { Text = $"Kapasite: {cylinder.Capacity:0.##} {cylinder.CapacityUnit}" });
        if (cylinder.TestDueDate is DateTime testDue)
        {
            details.Children.Add(new TextBlock { Text = $"Test tarihi: {testDue:dd.MM.yyyy}" });
        }

        Grid top = new();
        top.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        top.ColumnDefinitions.Add(new ColumnDefinition());
        Image qr = new() { Width = 150, Height = 150, Source = CylinderLabelService.QrImage(_code) };
        RenderOptions.SetBitmapScalingMode(qr, BitmapScalingMode.NearestNeighbor);
        top.Children.Add(qr);
        Grid.SetColumn(details, 1);
        top.Children.Add(details);

        Image barcode = new() { Height = 70, Stretch = Stretch.Uniform, Margin = new Thickness(0, 12, 0, 0), Source = CylinderLabelService.Code128Image(_code) };
        RenderOptions.SetBitmapScalingMode(barcode, BitmapScalingMode.NearestNeighbor);
        StackPanel labelContent = new();
        labelContent.Children.Add(top);
        labelContent.Children.Add(barcode);
        labelContent.Children.Add(new TextBlock { Text = _code, HorizontalAlignment = HorizontalAlignment.Center, FontFamily = new FontFamily("Consolas"), FontSize = 14 });
        _label = new Border
        {
            Background = Brushes.White,
            BorderBrush = Brushes.Black,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(14),
            Child = labelContent,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        TextElement.SetForeground(_label, Brushes.Black);

        Button print = new() { Content = "Yazdır", Padding = new Thickness(16, 8, 16, 8), Margin = new Thickness(0, 0, 8, 0) };
        Button save = new() { Content = "PNG olarak kaydet", Padding = new Thickness(16, 8, 16, 8), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        print.Click += (_, _) => Print();
        save.Click += (_, _) => SavePng();
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(print);
        buttons.Children.Add(save);

        StackPanel root = new() { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "QR ve barkod aynı kodu taşır; okuyucuyla \"Barkodla hızlı hareket\" kutusuna okutulabilir.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        root.Children.Add(_label);
        root.Children.Add(buttons);
        root.Children.Add(_status);
        Content = new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private void Print()
    {
        PrintDialog dialog = new();
        if (dialog.ShowDialog() != true)
        {
            return;
        }

        dialog.PrintVisual(_label, $"Tüp etiketi {_code}");
        _status.Text = "Etiket yazıcıya gönderildi.";
    }

    private void SavePng()
    {
        SaveFileDialog dialog = new() { Filter = "PNG resmi|*.png", FileName = $"tup-etiketi-{SafeName(_code)}.png" };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        _label.UpdateLayout();
        const double scale = 3;
        RenderTargetBitmap bitmap = new(
            (int)Math.Ceiling(_label.ActualWidth * scale),
            (int)Math.Ceiling(_label.ActualHeight * scale),
            96 * scale,
            96 * scale,
            PixelFormats.Pbgra32);
        bitmap.Render(_label);
        PngBitmapEncoder encoder = new();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using FileStream file = File.Create(dialog.FileName);
        encoder.Save(file);
        _status.Text = "Etiket kaydedildi: " + dialog.FileName;
    }

    private static string SafeName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '-');
        }

        return value;
    }
}
