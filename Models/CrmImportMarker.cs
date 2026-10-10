namespace ArsanGazERP.Models;

internal sealed class CrmImportMarker
{
    public string UserSid { get; set; } = string.Empty;
    public DateTime ImportedAtUtc { get; set; } = DateTime.UtcNow;
}
