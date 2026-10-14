using System;

namespace ArsanTupTakip.Models;

// A customer the vehicle visits on a trip, in the driver's order. A stop counts as visited when something was delivered
// to or collected from the customer on the trip, or when the driver marks it done.
public sealed class CylinderTripStop
{
    public int Id { get; set; }
    public int TripId { get; set; }
    public string Plate { get; set; } = string.Empty;
    public int CustomerId { get; set; }
    public int Position { get; set; }
    public DateTime AddedAt { get; set; } = DateTime.Now;
    public string AddedBy { get; set; } = string.Empty;
    public DateTime? DoneAt { get; set; }
    public string? Note { get; set; }
}
