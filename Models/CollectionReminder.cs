using System;

namespace ArsanGazERP.Models;

public sealed class CollectionReminder
{
    public const string PendingStatus = "Bekliyor";
    public const string CollectedStatus = "Tahsil edildi";
    public const string CancelledStatus = "İptal";

    public int Id { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "TRY";
    public DateTime DueDate { get; set; } = DateTime.Today;
    public int RemindDaysBefore { get; set; } = 1;
    public bool RepeatMonthly { get; set; }
    public string? Note { get; set; }
    public string Status { get; set; } = PendingStatus;
    public DateTime? LastNotifiedOn { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CollectedAtUtc { get; set; }
}
