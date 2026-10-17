using System;
namespace ArsanGazERP.Models;
// A depot count: phones scan the cylinders in the warehouse one by one; when it is closed the cylinders the system
// listed as "Depoda" are frozen into ExpectedSnapshot so the report does not change afterwards.
public sealed class StockCount
{
 public int Id { get; set; }
 public string Location { get; set; } = "Ana depo";
 public DateTime StartedAt { get; set; } = DateTime.Now;
 public string StartedBy { get; set; } = string.Empty;
 public DateTime? ClosedAt { get; set; }
 public string? ClosedBy { get; set; }
 public string? Note { get; set; }
 public int ExpectedCount { get; set; }
 public int MatchedCount { get; set; }
 public int MissingCount { get; set; }
 public int ExtraCount { get; set; }
 public int UnknownCount { get; set; }
 // Comma separated cylinder ids that were in the warehouse (by the records) when the count was closed.
 public string? ExpectedSnapshot { get; set; }
}
public sealed class StockCountItem
{
 public int Id { get; set; }
 public int CountId { get; set; }
 public int? CylinderId { get; set; }
 public string Code { get; set; } = string.Empty;
 public DateTime ScannedAt { get; set; } = DateTime.Now;
 public string ScannedBy { get; set; } = string.Empty;
 // Cylinder status and place by the records at the moment of the scan; null for an unknown code.
 public int? StatusAtScan { get; set; }
 public string? PlaceAtScan { get; set; }
}
