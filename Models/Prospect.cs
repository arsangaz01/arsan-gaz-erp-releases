using System;
namespace ArsanGazERP.Models;
public sealed class Prospect
{
    public int Id { get; set; }
    public string Provider { get; set; } = "Google Places";
    public string ProviderPlaceId { get; set; } = string.Empty;
    public string CompanyName { get; set; } = string.Empty;
    public string City { get; set; } = string.Empty;
    public string SectorQuery { get; set; } = string.Empty;
    public string? Address { get; set; }
    public string? Phone { get; set; }
    public string? Website { get; set; }
    public string? MapsUrl { get; set; }
    public string? Categories { get; set; }
    public int OpportunityScore { get; set; }
    public string RecommendedProducts { get; set; } = string.Empty;
    public string Status { get; set; } = "Yeni";
    public DateTime FoundAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenAtUtc { get; set; } = DateTime.UtcNow;
}
