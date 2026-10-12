using System;

namespace ArsanTupTakip.Models;

// Registered company vehicle. Trips and cylinders reference vehicles by normalized plate text, so the plate of a
// saved vehicle is never changed; vehicles are deactivated instead of deleted.
public sealed class Vehicle
{
    public int Id { get; set; }
    public string Plate { get; set; } = string.Empty;
    public string? Name { get; set; }
    public string? VehicleType { get; set; }
    public string? DriverName { get; set; }
    public int? CylinderCapacity { get; set; }
    public string? Note { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public DateTime? UpdatedAt { get; set; }
    public uint Version { get; set; }
}
