using System;
using System.Collections.Generic;
namespace ArsanGazERP.Models;
public sealed class Quote
{
 public int Id { get; set; }
 public string QuoteNumber { get; set; } = string.Empty;
 public int CustomerId { get; set; }
 public Customer Customer { get; set; } = null!;
 public DateTime QuoteDate { get; set; } = DateTime.Today;
 public DateTime? ValidUntil { get; set; }
 public string Status { get; set; } = "Taslak";
 public decimal TotalAmount { get; set; }
 public string Currency { get; set; } = "TRY";
 public DateTime? FollowUpDate { get; set; }
 public ICollection<QuoteLine> Lines { get; set; } = new List<QuoteLine>();
}
public sealed class QuoteLine
{
 public int Id { get; set; }
 public int QuoteId { get; set; }
 public Quote Quote { get; set; } = null!;
 public int ProductId { get; set; }
 public Product Product { get; set; } = null!;
 public decimal Quantity { get; set; }
 public decimal UnitPrice { get; set; }
 public decimal VatRate { get; set; }
 public decimal LineTotal { get; set; }
}
public sealed class Invoice
{
 public int Id { get; set; }
 public string InvoiceNumber { get; set; } = string.Empty;
 public int CustomerId { get; set; }
 public Customer Customer { get; set; } = null!;
 public DateTime InvoiceDate { get; set; } = DateTime.Today;
 public DateTime? DueDate { get; set; }
 public decimal TotalAmount { get; set; }
 public decimal PaidAmount { get; set; }
 public string Status { get; set; } = "Taslak";
 public ICollection<Payment> Payments { get; set; } = new List<Payment>();
}
public sealed class Payment
{
 public int Id { get; set; }
 public int InvoiceId { get; set; }
 public Invoice Invoice { get; set; } = null!;
 public DateTime PaymentDate { get; set; } = DateTime.Today;
 public decimal Amount { get; set; }
 public string Method { get; set; } = "Banka";
 public string? Reference { get; set; }
}
public sealed class ErpTask
{
 public int Id { get; set; }
 public string Title { get; set; } = string.Empty;
 public string? Description { get; set; }
 public DateTime? DueDate { get; set; }
 public string Priority { get; set; } = "Normal";
 public string Status { get; set; } = "Açık";
 public string Category { get; set; } = "Genel";
 public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}