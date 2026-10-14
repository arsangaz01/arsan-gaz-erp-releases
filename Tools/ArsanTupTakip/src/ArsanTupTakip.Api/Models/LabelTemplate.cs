using System;
namespace ArsanTupTakip.Models;
// A saved label product for the Barkod Yazdır tab: the code format (PREFIX + zero-padded number) and the label layout
// of a barcode that already prints and scans well, so a new label only needs the next serial number.
public sealed class LabelTemplate
{
 public int Id { get; set; }
 public string Name { get; set; } = string.Empty;
 public string GasText { get; set; } = string.Empty;
 public string Prefix { get; set; } = string.Empty;
 public int Digits { get; set; } = 6;
 public string? SampleCode { get; set; }
 public string? TestText { get; set; }
 public string Title { get; set; } = string.Empty;
 public int WidthMm { get; set; } = 50;
 public int HeightMm { get; set; } = 30;
 public int Copies { get; set; } = 1;
 public bool ShowGas { get; set; } = true;
 public bool ShowSerial { get; set; } = true;
 public bool ShowTest { get; set; } = true;
 public bool ShowText { get; set; } = true;
 public DateTime CreatedAt { get; set; } = DateTime.Now;
 public string CreatedBy { get; set; } = string.Empty;
 public DateTime UpdatedAt { get; set; } = DateTime.Now;
 public string UpdatedBy { get; set; } = string.Empty;
}
