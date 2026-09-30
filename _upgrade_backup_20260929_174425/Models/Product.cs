using System;
namespace ArsanGazERP.Models;
public sealed class Product
{
 public int Id { get; set; }
 public string Code { get; set; } = string.Empty;
 public string Name { get; set; } = string.Empty;
 public string Unit { get; set; } = "Adet";
 public decimal SalesPrice { get; set; }
 public string Currency { get; set; } = "TRY";
 public decimal VatRate { get; set; } = 20;
 public decimal CriticalStock { get; set; }
 public bool IsActive { get; set; } = true;
 public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}