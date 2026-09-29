using System;
using System.Collections.Generic;
namespace ArsanGazERP.Models;
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
 public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
 public DateTime? UpdatedAt { get; set; }
 public ICollection<Cylinder> Cylinders { get; set; } = new List<Cylinder>();
}