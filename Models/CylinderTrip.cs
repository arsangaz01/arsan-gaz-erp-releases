using System;
using System.Collections.Generic;

namespace ArsanGazERP.Models;

public enum CylinderTripStatus
{
    Active = 1,
    Closed = 2
}

public sealed class CylinderTrip
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public CylinderTripStatus Status { get; set; } = CylinderTripStatus.Active;
    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? ClosedAt { get; set; }
    public string? Note { get; set; }
    public string? CreatedBy { get; set; }
    // Account (user name) that closed the trip; used for the "Tamamlanmış seferler" visibility rule.
    public string? ClosedBy { get; set; }
    // Set on trips started since reasons became mandatory; older open trips can still be closed without them.
    public bool RequireReasons { get; set; }
    public ICollection<CylinderTripVehicle> Vehicles { get; set; } = new List<CylinderTripVehicle>();
    public ICollection<CylinderTripCylinder> Cylinders { get; set; } = new List<CylinderTripCylinder>();
}

public sealed class CylinderTripVehicle
{
    public int Id { get; set; }
    public int TripId { get; set; }
    public CylinderTrip Trip { get; set; } = null!;
    public string Plate { get; set; } = string.Empty;
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public DateTime? ReturnedAt { get; set; }
}

public sealed class CylinderTripCylinder
{
    public int Id { get; set; }
    public int TripId { get; set; }
    public CylinderTrip Trip { get; set; } = null!;
    public int CylinderId { get; set; }
    public Cylinder Cylinder { get; set; } = null!;
    public string VehiclePlate { get; set; } = string.Empty;
    public string SerialNumber { get; set; } = string.Empty;
    public string GasType { get; set; } = string.Empty;
    public DateTime LoadedAt { get; set; } = DateTime.Now;
    public DateTime? DeliveredAt { get; set; }
    public DateTime? ReturnedAt { get; set; }
    // An empty taken from a customer during the trip (LoadedAt is the pickup time) instead of a full loaded at the depot.
    public bool IsPickup { get; set; }
    public string? PickupCustomer { get; set; }
    // Why a cylinder that was neither delivered nor returned is missing (key of TripMissingReasons), and who confirmed it.
    public string? MissingReason { get; set; }
    public string? MissingNote { get; set; }
    public DateTime? ReasonAt { get; set; }
    public string? ReasonBy { get; set; }
    public DateTime? CheckedAt { get; set; }
    public string? CheckedBy { get; set; }
}
