using System;
using System.Collections.Generic;
namespace ArsanTupTakip.Models;
public sealed class Customer
{
 public int Id { get; set; }
 public string Code { get; set; } = string.Empty;
 public string CompanyName { get; set; } = string.Empty;
 public string? ContactName { get; set; }
 public string? Phone { get; set; }
 public string? Email { get; set; }
 public string? City { get; set; }
 public string? District { get; set; }
 public string? Address { get; set; }
 public string? TaxOffice { get; set; }
 public string? TaxNumber { get; set; }
 public bool IsActive { get; set; } = true;
 public string? Phone2 { get; set; }
 public string? CustomerType { get; set; }
 public string? Note { get; set; }
 public int? LegacyId { get; set; }
 public string? LegacySource { get; set; }
 /// <summary>Partner workspace (ör. yalcin). Null = genel listelerde.</summary>
 public string? PartnerKey { get; set; }
 public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
 public DateTime? UpdatedAt { get; set; }
 public uint Version { get; set; }
 public ICollection<Cylinder> Cylinders { get; set; } = new List<Cylinder>();
}