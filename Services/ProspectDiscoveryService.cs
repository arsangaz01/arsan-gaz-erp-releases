using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed class ProspectDiscoveryService
{
    private const string Endpoint = "https://overpass-api.de/api/interpreter";
    private static readonly HttpClient Http = CreateClient();

    public static readonly string[] DefaultCities =
    {
        "Adana", "Mersin", "Hatay", "Osmaniye", "Gaziantep", "ÅanlÄ±urfa"
    };

    public async Task<ProspectScanResult> ScanAsync(IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        int created = 0;
        int updated = 0;
        int requestCount = 0;
        await using ArsanGazDbContext db = new();

        foreach (string city in DefaultCities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            requestCount++;
            progress?.Report($"{requestCount}/{DefaultCities.Length} Â· {city}");
            IReadOnlyList<OsmElement> elements = await SearchCityAsync(city, cancellationToken);

            foreach (OsmElement element in elements)
            {
                Dictionary<string, string> tags = element.Tags ?? new();
                if (!tags.TryGetValue("name", out string? companyName) || string.IsNullOrWhiteSpace(companyName)) continue;

                string providerId = $"{element.Type}/{element.Id}";
                Prospect? prospect = await db.Prospects.FirstOrDefaultAsync(
                    item => item.Provider == "OpenStreetMap" && item.ProviderPlaceId == providerId,
                    cancellationToken);
                bool isNew = prospect is null;
                prospect ??= new Prospect
                {
                    Provider = "OpenStreetMap",
                    ProviderPlaceId = providerId,
                    FoundAtUtc = DateTime.UtcNow
                };

                string sector = DetectSector(tags);
                prospect.CompanyName = companyName.Trim();
                prospect.City = city;
                prospect.SectorQuery = sector;
                prospect.Address = BuildAddress(tags);
                prospect.Phone = First(tags, "contact:phone", "phone");
                prospect.Website = First(tags, "contact:website", "website");
                prospect.MapsUrl = $"https://www.openstreetmap.org/{element.Type}/{element.Id}";
                prospect.Categories = string.Join(", ", tags
                    .Where(pair => pair.Key is "industrial" or "craft" or "healthcare" or "amenity" or "office" or "shop" or "man_made")
                    .Select(pair => $"{pair.Key}={pair.Value}"));
                prospect.OpportunityScore = CalculateScore(sector, prospect.Phone, prospect.Website, tags);
                prospect.RecommendedProducts = RecommendProducts(sector);
                prospect.LastSeenAtUtc = DateTime.UtcNow;

                if (isNew) { db.Prospects.Add(prospect); created++; }
                else { updated++; }
            }

            await db.SaveChangesAsync(cancellationToken);
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
        }

        return new ProspectScanResult(requestCount, created, updated, DateTime.UtcNow, "OpenStreetMap / Overpass API");
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(90) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP-ProspectHunter/5.5.2");
        return client;
    }

    private static async Task<IReadOnlyList<OsmElement>> SearchCityAsync(string city, CancellationToken cancellationToken)
    {
        string safeCity = city.Replace("\\", "\\\\").Replace("\"", "\\\"");
        string query =
            "[out:json][timeout:60];\n" +
            "area[\"boundary\"=\"administrative\"][\"name\"=\"" + safeCity + "\"]->.searchArea;\n" +
            "(\n" +
            "nwr[\"man_made\"=\"works\"](area.searchArea);\n" +
            "nwr[\"industrial\"](area.searchArea);\n" +
            "nwr[\"craft\"~\"welder|metal_construction|metal_works|industrial_equipment|laboratory|hvac|car_repair\"](area.searchArea);\n" +
            "nwr[\"healthcare\"](area.searchArea);\n" +
            "nwr[\"amenity\"~\"hospital|clinic|laboratory|veterinary\"](area.searchArea);\n" +
            "nwr[\"office\"~\"company|research|logistics\"](area.searchArea);\n" +
            "nwr[\"shop\"~\"party|medical_supply|car_repair|trade\"](area.searchArea);\n" +
            ");\n" +
            "out tags center;";

        using HttpRequestMessage request = new(HttpMethod.Post, Endpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["data"] = query })
        };
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Overpass API hatasÄ± {(int)response.StatusCode}: {Shorten(json)}");

        OsmResponse? result = JsonSerializer.Deserialize<OsmResponse>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
        return result?.Elements ?? new List<OsmElement>();
    }

    private static string DetectSector(IReadOnlyDictionary<string, string> tags)
    {
        string value = string.Join(" ", new[]
        {
            First(tags, "industrial", "craft", "healthcare", "amenity", "office", "shop", "man_made") ?? string.Empty,
            First(tags, "name") ?? string.Empty,
            First(tags, "description") ?? string.Empty
        }).ToLowerInvariant();

        if (value.Contains("hospital") || value.Contains("clinic") || value.Contains("health")) return "SaÄŸlÄ±k kuruluÅŸu";
        if (value.Contains("laboratory") || value.Contains("research")) return "Laboratuvar / araÅŸtÄ±rma";
        if (value.Contains("metal") || value.Contains("welder")) return "Metal iÅŸleme / kaynak";
        if (value.Contains("food") || value.Contains("beverage")) return "GÄ±da Ã¼retimi";
        if (value.Contains("glass")) return "Cam Ã¼retimi";
        if (value.Contains("chemical")) return "Kimya sanayi";
        if (value.Contains("party")) return "Organizasyon / balon";
        if (value.Contains("logistics") || value.Contains("warehouse")) return "Lojistik / depo";
        if (value.Contains("car") || value.Contains("automotive")) return "Otomotiv";
        return "Sanayi / ticari iÅŸletme";
    }

    private static string? BuildAddress(IReadOnlyDictionary<string, string> tags)
    {
        string? full = First(tags, "addr:full");
        if (!string.IsNullOrWhiteSpace(full)) return full;
        string[] parts =
        {
            First(tags, "addr:street") ?? string.Empty,
            First(tags, "addr:housenumber") ?? string.Empty,
            First(tags, "addr:district", "addr:suburb") ?? string.Empty,
            First(tags, "addr:city") ?? string.Empty
        };
        string result = string.Join(" ", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
        return string.IsNullOrWhiteSpace(result) ? null : result;
    }

    private static string? First(IReadOnlyDictionary<string, string> tags, params string[] keys)
    {
        foreach (string key in keys)
            if (tags.TryGetValue(key, out string? value) && !string.IsNullOrWhiteSpace(value)) return value.Trim();
        return null;
    }

    private static int CalculateScore(string sector, string? phone, string? website, IReadOnlyDictionary<string, string> tags)
    {
        int score = 35;
        if (!string.IsNullOrWhiteSpace(phone)) score += 20;
        if (!string.IsNullOrWhiteSpace(website)) score += 15;
        if (sector.Contains("Metal") || sector.Contains("Kimya") || sector.Contains("Cam")) score += 20;
        if (sector.Contains("SaÄŸlÄ±k") || sector.Contains("Laboratuvar")) score += 15;
        if (tags.ContainsKey("opening_hours")) score += 5;
        return Math.Min(score, 100);
    }

    private static string RecommendProducts(string sector) => sector switch
    {
        var value when value.Contains("Metal") => "Oksijen, Argon, Asetilen, Azot",
        var value when value.Contains("GÄ±da") || value.Contains("depo") => "Azot, Karbondioksit, Kuru Hava",
        var value when value.Contains("SaÄŸlÄ±k") || value.Contains("Laboratuvar") => "Oksijen, Azot, Helyum, Kuru Hava",
        var value when value.Contains("balon") => "Balon GazÄ±, Helyum",
        var value when value.Contains("Kimya") => "Azot, Hidrojen, Argon, Kuru Hava",
        _ => "Azot, Oksijen, Argon"
    };

    private static string Shorten(string text) => text.Length <= 300 ? text : text[..300];
    private sealed class OsmResponse { public List<OsmElement>? Elements { get; set; } }
    private sealed class OsmElement
    {
        public string Type { get; set; } = "node";
        public long Id { get; set; }
        public Dictionary<string, string>? Tags { get; set; }
    }
}

public sealed record ProspectScanResult(int RequestCount, int CreatedCount, int UpdatedCount, DateTime CompletedAtUtc, string Provider)
{
    public override string ToString() =>
        $"Ãœcretsiz tarama tamamlandÄ± Â· kaynak {Provider} Â· sorgu {RequestCount} Â· yeni aday {CreatedCount} Â· gÃ¼ncellenen {UpdatedCount}";
}