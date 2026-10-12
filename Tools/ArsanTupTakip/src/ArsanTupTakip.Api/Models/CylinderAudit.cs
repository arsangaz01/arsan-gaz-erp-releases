using System;
namespace ArsanTupTakip.Models;
public sealed class CylinderAudit
{
 public int Id { get; set; }
 public int CustomerId { get; set; }
 public string CustomerName { get; set; } = string.Empty;
 public DateTime AuditedAt { get; set; } = DateTime.Now;
 public int ExpectedCount { get; set; }
 public int FoundCount { get; set; }
 public string MissingSerials { get; set; } = string.Empty;
 public string UnexpectedSerials { get; set; } = string.Empty;
 public string UnknownCodes { get; set; } = string.Empty;
 public string? Note { get; set; }
 public string? PerformedBy { get; set; }
}
