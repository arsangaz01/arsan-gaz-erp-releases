using System;

namespace ArsanTupTakip.Models;

// A request a customer sends from the portal: collect the empties ("bos-alim") or bring full cylinders ("siparis").
// Status: "acik" until the staff closes it as "tamamlandi" or "iptal" (the customer may cancel an open one too).
public sealed class CustomerRequest
{
    public int Id { get; set; }
    public int CustomerId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public int? Quantity { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string CreatedBy { get; set; } = string.Empty;
    public string Status { get; set; } = "acik";
    public DateTime? HandledAt { get; set; }
    public string? HandledBy { get; set; }
    public string? HandledNote { get; set; }
}
