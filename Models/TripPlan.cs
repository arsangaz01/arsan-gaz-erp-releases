using System;

namespace ArsanGazERP.Models;

// A day's plan for one vehicle, prepared in the panel: the customers to visit in order and how close the driver must
// come before the phone warns. When a trip with the vehicle is open on that day the stops are copied onto the trip's
// route (TripId is then set) and later changes to the plan are carried over to that route.
public sealed class TripPlan
{
    public int Id { get; set; }
    public DateTime PlanDate { get; set; }
    public string Plate { get; set; } = string.Empty;
    public int AlertMeters { get; set; } = 300;
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string UpdatedBy { get; set; } = string.Empty;
    public int? TripId { get; set; }
    public DateTime? AppliedAt { get; set; }
}

public sealed class TripPlanStop
{
    public int Id { get; set; }
    public int PlanId { get; set; }
    public int CustomerId { get; set; }
    public int Position { get; set; }
    public string? Note { get; set; }
}

// Where a customer is, for the approach warning and the map link. Entered in the panel (map link or coordinates),
// saved by a driver standing at the customer, or found on Google from the customer's name and address. A found one is
// not verified until someone confirms it in the panel or a driver confirms it at the customer.
public sealed class CustomerLocation
{
    public int CustomerId { get; set; }
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public DateTime UpdatedAt { get; set; } = DateTime.Now;
    public string UpdatedBy { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public bool Verified { get; set; } = true;
    public string? Label { get; set; }
}
