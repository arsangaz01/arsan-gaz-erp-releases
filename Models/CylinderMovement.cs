using System;
namespace ArsanGazERP.Models;
public sealed class CylinderMovement
{
 public int Id { get; set; }
 public int CylinderId { get; set; }
 public Cylinder Cylinder { get; set; } = null!;
 public int? CustomerId { get; set; }
 public Customer? Customer { get; set; }
 public CylinderStatus FromStatus { get; set; }
 public CylinderStatus ToStatus { get; set; }
 public string MovementType { get; set; } = string.Empty;
 public string? VehiclePlate { get; set; }
 public bool? IsFull { get; set; }
 public string? DocumentNumber { get; set; }
 public string? Description { get; set; }
 public DateTime MovementDate { get; set; } = DateTime.UtcNow;
 public string? PerformedBy { get; set; }
 /// <summary>Id in the external system named by <see cref="LegacySource"/> (e.g. ArsanTüpTakip movement id).</summary>
 public long? LegacyId { get; set; }
 public string? LegacySource { get; set; }
}