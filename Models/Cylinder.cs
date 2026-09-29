using System;
using System.Collections.Generic;
namespace ArsanGazERP.Models;
public sealed class Cylinder
{
 public int Id { get; set; }
 public string SerialNumber { get; set; } = string.Empty;
 public string Barcode { get; set; } = string.Empty;
 public string GasType { get; set; } = string.Empty;
 public decimal Capacity { get; set; }
 public string CapacityUnit { get; set; } = "Litre";
 public CylinderStatus Status { get; set; } = CylinderStatus.Warehouse;
 public int? CurrentCustomerId { get; set; }
 public Customer? CurrentCustomer { get; set; }
 public DateTime? LastMovementAt { get; set; }
 public DateTime? TestDueDate { get; set; }
 public bool IsActive { get; set; } = true;
 public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
 public ICollection<CylinderMovement> Movements { get; set; } = new List<CylinderMovement>();
}
public enum CylinderStatus { Warehouse=1, Vehicle=2, Customer=3, Filling=4, Service=5, Scrap=6 }