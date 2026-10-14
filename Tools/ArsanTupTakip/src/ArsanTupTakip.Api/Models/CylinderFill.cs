using System;

namespace ArsanTupTakip.Models;

// One filling of a cylinder: who filled it, when, with which lot/batch. The cylinder itself becomes full in the depot.
public sealed class CylinderFill
{
    public int Id { get; set; }
    public int CylinderId { get; set; }
    public string SerialNumber { get; set; } = string.Empty;
    public string GasType { get; set; } = string.Empty;
    public DateTime FilledAt { get; set; } = DateTime.Now;
    public string FilledBy { get; set; } = string.Empty;
    public string? LotNumber { get; set; }
    public string? Note { get; set; }
    // Filled although the periodic test date had passed (confirmed by the user).
    public bool TestOverdue { get; set; }
}
