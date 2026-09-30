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
    public static readonly string[] DefaultCities = { "Adana", "Mersin", "Hatay", "Osmaniye", "Gaziantep", "Şanlıurfa" };
    public static readonly string[] DefaultSectors =
    {
        "metal işleme fabrikası", "kaynak kesim sanayi", "gıda üretim tesisi",
        "soğuk hava deposu", "hastane", "laboratuvar", "otomotiv yan sanayi",
        "kimya fabrikası", "cam üretim tesisi", "balon organizasyon firması"
    };
    public async Task<string> ScanAsync(IEnumerable<string>? cities = null, IEnumerable<string>? sectors = null, CancellationToken cancellationToken = default)
    {
        string apiKey = Environment.GetEnvironmentVariable("ARSAN_GOOGLE_PLACES_API_KEY", EnvironmentVariableTarget.User)
            ?? Environment.GetEnvironmentVariable("ARSAN_GOOGLE_PLACES_API_KEY")
            ?? throw new InvalidOperationException("ARSAN_GOOGLE_PLACES_API_KEY kullanıcı ortam değişkeni tanımlı değil.");
        int added = 0, updated = 0, requests = 0;
        await using ArsanGazDbContext db = new();
        foreach (string city in cities ?? DefaultCities)
        foreach (string sector in sectors ?? DefaultSectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            List<PlaceResult> places = await SearchAsync(apiKey, $"{sector} {city} Türkiye", cancellationToken);
            requests++;
            foreach (PlaceResult place in places)
            {
                if (string.IsNullOrWhiteSpace(place.Id) || string.IsNullOrWhiteSpace(place.DisplayName?.Text)) continue;
                Prospect? prospect = await db.Prospects.FirstOrDefaultAsync(x => x.ProviderPlaceId == place.Id, cancellationToken);
                bool isNew = prospect is null;
                prospect ??= new Prospect { ProviderPlaceId = place.Id };
                prospect.CompanyName = place.DisplayName!.Text!;
                prospect.City = city;
                prospect.SectorQuery = sector;
                prospect.Address = place.FormattedAddress;
                prospect.Phone = place.NationalPhoneNumber;
                prospect.Website = place.WebsiteUri;
                prospect.MapsUrl = place.GoogleMapsUri;
                prospect.Categories = place.Types is null ? null : string.Join(", ", place.Types);
                prospect.OpportunityScore = Score(sector, place);
                prospect.RecommendedProducts = ProductsFor(sector);
                prospect.LastSeenAtUtc = DateTime.UtcNow;
                if (isNew) { db.Prospects.Add(prospect); added++; } else updated++;
            }
            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(250, cancellationToken);
        }
        return $"Müşteri Avcısı tamamlandı. Sorgu: {requests}, yeni aday: {added}, güncellenen: {updated}.";
    }
    private static async Task<List<PlaceResult>> SearchAsync(string apiKey, string query, CancellationToken token)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint);
        request.Headers.Add("X-Goog-Api-Key", apiKey);
        request.Headers.Add("X-Goog-FieldMask", "places.id,places.displayName,places.formattedAddress,places.nationalPhoneNumber,places.websiteUri,places.googleMapsUri,places.types");
        request.Content = new StringContent(JsonSerializer.Serialize(new { textQuery = query, languageCode = "tr", regionCode = "TR", pageSize = 20 }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.SendAsync(request, token);
        string json = await response.Content.ReadAsStringAsync(token);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"Google Places hatası {(int)response.StatusCode}: {json}");
        return JsonSerializer.Deserialize<SearchResponse>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.Places ?? new();
    }
    private static int Score(string sector, PlaceResult p)
    {
        int score = 45;
        if (!string.IsNullOrWhiteSpace(p.PhoneOrEmpty)) score += 15;
        if (!string.IsNullOrWhiteSpace(p.WebsiteUri)) score += 15;
        if (sector.Contains("metal") || sector.Contains("kaynak") || sector.Contains("kimya") || sector.Contains("cam")) score += 20;
        if (sector.Contains("hastane") || sector.Contains("laboratuvar")) score += 15;
        return Math.Min(score, 100);
    }
    private static string ProductsFor(string sector) => sector switch
    {
        var s when s.Contains("metal") || s.Contains("kaynak") => "Oksijen, Argon, Asetilen, Azot",
        var s when s.Contains("gıda") || s.Contains("soğuk") => "Azot, Karbondioksit, Kuru Hava",
        var s when s.Contains("hastane") || s.Contains("laboratuvar") => "Oksijen, Azot, Helyum, Kuru Hava",
        var s when s.Contains("balon") => "Balon Gazı, Helyum",
        var s when s.Contains("kimya") => "Azot, Hidrojen, Argon, Kuru Hava",
        _ => "Azot, Oksijen, Argon"
    };
    private sealed class SearchResponse { public List<PlaceResult>? Places { get; set; } }
    private sealed class PlaceResult
    {
        public string? Id { get; set; }
        public DisplayNameValue? DisplayName { get; set; }
        public string? FormattedAddress { get; set; }
        public string? NationalPhoneNumber { get; set; }
        public string? WebsiteUri { get; set; }
        public string? GoogleMapsUri { get; set; }
        public List<string>? Types { get; set; }
        public string PhoneOrEmpty => NationalPhoneNumber ?? string.Empty;
    }
    private sealed class DisplayNameValue { public string? Text { get; set; } }
}
