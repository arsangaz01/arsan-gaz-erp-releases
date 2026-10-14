using System;
using System.Collections.Generic;
namespace ArsanTupTakip.Models;
public sealed class Cylinder
{
 public int Id { get; set; }
 public string SerialNumber { get; set; } = string.Empty;
 public string Barcode { get; set; } = string.Empty;
 public string GasType { get; set; } = string.Empty;
 public decimal Capacity { get; set; }
 public string CapacityUnit { get; set; } = "Litre";
 public CylinderStatus Status { get; set; } = CylinderStatus.Warehouse;
 public bool? IsFull { get; set; }
 public string? VehiclePlate { get; set; }
 public int? CurrentCustomerId { get; set; }
 public Customer? CurrentCustomer { get; set; }
 public DateTime? LastMovementAt { get; set; }
 public DateTime? TestDueDate { get; set; }
 public bool IsActive { get; set; } = true;
 // Set when the cylinder belongs to a customer (müşteri malı) instead of the company.
 public int? OwnerCustomerId { get; set; }
 public Customer? OwnerCustomer { get; set; }
 public string? ManufacturerSerial { get; set; }
 public string? ProductName { get; set; }
 public DateTime? ManufactureDate { get; set; }
 public DateTime? LastTestDate { get; set; }
 // Record id in the system the cylinder was migrated from (LegacySource), e.g. İzciPro tbl_EnvanterKart.ID.
 public int? LegacyId { get; set; }
 public string? LegacySource { get; set; }
 public bool NeedsReview { get; set; }
 public string? ReviewNote { get; set; }
 public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
 // PostgreSQL xmin: changes on every update, used for optimistic concurrency (no real column).
 public uint Version { get; set; }
 public ICollection<CylinderMovement> Movements { get; set; } = new List<CylinderMovement>();
}
public enum CylinderStatus { Warehouse=1, Vehicle=2, Customer=3, Filling=4, Service=5, Scrap=6 }