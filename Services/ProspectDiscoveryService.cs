using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class ProspectDiscoveryService
{
    private const string Endpoint = "https://places.googleapis.com/v1/places:searchText";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(45) };

    public static readonly string[] DefaultCities =
    {
        "Adana", "Mersin", "Hatay", "Osmaniye", "Gaziantep", "Şanlıurfa"
    };

    public static readonly string[] DefaultSectors =
    {
        "metal işleme fabrikası",
        "kaynak kesim sanayi",
        "gıda üretim tesisi",
        "soğuk hava deposu",
        "hastane",
        "laboratuvar",
        "otomotiv yan sanayi",
        "kimya fabrikası",
        "cam üretim tesisi",
        "balon organizasyon firması"
    };

    public async Task<ProspectScanResult> ScanAsync(
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        string apiKey = GetApiKey();
        int created = 0;
        int updated = 0;
        int requestCount = 0;
        int total = DefaultCities.Length * DefaultSectors.Length;

        await using ArsanGazDbContext db = new();

        foreach (string city in DefaultCities)
        {
            foreach (string sector in DefaultSectors)
            {
                cancellationToken.ThrowIfCancellationRequested();
                requestCount++;
                progress?.Report($"{requestCount}/{total} · {city} · {sector}");

                IReadOnlyList<PlaceResult> places = await SearchAsync(
                    apiKey,
                    $"{sector} {city} Türkiye",
                    cancellationToken);

                foreach (PlaceResult place in places)
                {
                    string? placeId = place.Id;
                    string? companyName = place.DisplayName?.Text;
                    if (string.IsNullOrWhiteSpace(placeId) || string.IsNullOrWhiteSpace(companyName))
                    {
                        continue;
                    }

                    Prospect? prospect = await db.Prospects
                        .FirstOrDefaultAsync(item => item.ProviderPlaceId == placeId, cancellationToken);
                    bool isNew = prospect is null;
                    prospect ??= new Prospect
                    {
                        Provider = "Google Places",
                        ProviderPlaceId = placeId,
                        FoundAtUtc = DateTime.UtcNow
                    };

                    prospect.CompanyName = companyName;
                    prospect.City = city;
                    prospect.SectorQuery = sector;
                    prospect.Address = place.FormattedAddress;
                    prospect.Phone = place.NationalPhoneNumber;
                    prospect.Website = place.WebsiteUri;
                    prospect.MapsUrl = place.GoogleMapsUri;
                    prospect.Categories = place.Types is null ? null : string.Join(", ", place.Types);
                    prospect.OpportunityScore = CalculateScore(sector, place);
                    prospect.RecommendedProducts = RecommendProducts(sector);
                    prospect.LastSeenAtUtc = DateTime.UtcNow;

                    if (isNew)
                    {
                        db.Prospects.Add(prospect);
                        created++;
                    }
                    else
                    {
                        updated++;
                    }
                }

                await db.SaveChangesAsync(cancellationToken);
                await Task.Delay(200, cancellationToken);
            }
        }

        return new ProspectScanResult(requestCount, created, updated, DateTime.UtcNow);
    }

    public static string GetApiKey() =>
        Environment.GetEnvironmentVariable("ARSAN_GOOGLE_PLACES_API_KEY", EnvironmentVariableTarget.User)
        ?? Environment.GetEnvironmentVariable("ARSAN_GOOGLE_PLACES_API_KEY")
        ?? throw new InvalidOperationException("Google Places API anahtarı kayıtlı değil. Müşteri Avcısı ekranından anahtarı kaydedin.");

    public static void SaveApiKey(string apiKey)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("API anahtarı boş olamaz.", nameof(apiKey));
        }
        Environment.SetEnvironmentVariable("ARSAN_GOOGLE_PLACES_API_KEY", apiKey.Trim(), EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("ARSAN_PROSPECT_AUTO_SCAN", "1", EnvironmentVariableTarget.User);
    }

    private static async Task<IReadOnlyList<PlaceResult>> SearchAsync(
        string apiKey,
        string textQuery,
        CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            "places.id,places.displayName,places.formattedAddress,places.nationalPhoneNumber,places.websiteUri,places.googleMapsUri,places.types");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                textQuery,
                languageCode = "tr",
                regionCode = "TR",
                pageSize = 20
            }),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Google Places hatası {(int)response.StatusCode}: {json}");
        }

        SearchResponse? result = JsonSerializer.Deserialize<SearchResponse>(
            json,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        return result?.Places ?? new List<PlaceResult>();
    }

    private static int CalculateScore(string sector, PlaceResult place)
    {
        int score = 40;
        if (!string.IsNullOrWhiteSpace(place.NationalPhoneNumber)) score += 15;
        if (!string.IsNullOrWhiteSpace(place.WebsiteUri)) score += 15;
        if (sector.Contains("metal") || sector.Contains("kaynak") || sector.Contains("kimya") || sector.Contains("cam")) score += 20;
        if (sector.Contains("hastane") || sector.Contains("laboratuvar")) score += 10;
        return Math.Min(score, 100);
    }

    private static string RecommendProducts(string sector) => sector switch
    {
        var value when value.Contains("metal") || value.Contains("kaynak") => "Oksijen, Argon, Asetilen, Azot",
        var value when value.Contains("gıda") || value.Contains("soğuk") => "Azot, Karbondioksit, Kuru Hava",
        var value when value.Contains("hastane") || value.Contains("laboratuvar") => "Oksijen, Azot, Helyum, Kuru Hava",
        var value when value.Contains("balon") => "Balon Gazı, Helyum",
        var value when value.Contains("kimya") => "Azot, Hidrojen, Argon, Kuru Hava",
        _ => "Azot, Oksijen, Argon"
    };

    private sealed class SearchResponse
    {
        public List<PlaceResult>? Places { get; set; }
    }

    private sealed class PlaceResult
    {
        public string? Id { get; set; }
        public DisplayNameValue? DisplayName { get; set; }
        public string? FormattedAddress { get; set; }
        public string? NationalPhoneNumber { get; set; }
        public string? WebsiteUri { get; set; }
        public string? GoogleMapsUri { get; set; }
        public List<string>? Types { get; set; }
    }

    private sealed class DisplayNameValue
    {
        public string? Text { get; set; }
    }
}

public sealed record ProspectScanResult(int RequestCount, int CreatedCount, int UpdatedCount, DateTime CompletedAtUtc)
{
    public override string ToString() =>
        $"Tarama tamamlandı · sorgu {RequestCount} · yeni aday {CreatedCount} · güncellenen {UpdatedCount}";
}
