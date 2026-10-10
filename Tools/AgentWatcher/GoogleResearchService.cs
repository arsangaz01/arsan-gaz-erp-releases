using System.Net.Http;
using System.Text.Json;
using ArsanGazERP.Services;

namespace ArsanGazERP.AgentWatcher;

internal sealed class GoogleResearchService(
    HttpClient httpClient,
    ProtectedSettingsStore secrets,
    Func<string?> provider,
    Func<string?> searchEngineId)
{
    public const string FreeProviderName = "Ücretsiz arama (Bing/DuckDuckGo + Jina Reader)";
    private const int JinaPreviewCharacters = 1200;

    // The keyless search is always available, so research never stops for a missing paid key.
    public bool IsConfigured => true;

    public bool IsPaidProviderConfigured => string.Equals(provider(), "SerpAPI", StringComparison.OrdinalIgnoreCase)
        ? secrets.HasSerpApiKey
        : secrets.HasGoogleApiKey && !string.IsNullOrWhiteSpace(searchEngineId());

    public string ActiveProviderName => IsPaidProviderConfigured ? provider() ?? "Google" : FreeProviderName;

    public async Task<string> SearchAsync(string query, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (!IsPaidProviderConfigured)
        {
            return await SearchFreeAsync(query, cancellationToken);
        }

        try
        {
            return await SearchPaidAsync(query, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                          exception is InvalidOperationException or HttpRequestException
                                              or TaskCanceledException or JsonException)
        {
            return $"[{provider()} kullanılamadı ({exception.Message}); ücretsiz aramaya geçildi]\n"
                + await SearchFreeAsync(query, cancellationToken);
        }
    }

    private static async Task<string> SearchFreeAsync(string query, CancellationToken cancellationToken)
    {
        List<(string Title, string Url, string Snippet)> items = await WebResearchService.SearchResultsAsync(query, cancellationToken);
        if (items.Count == 0)
        {
            return "Web araması eşleşen sonuç döndürmedi.";
        }

        List<string> results = [];
        for (int index = 0; index < Math.Min(items.Count, 5); index++)
        {
            (string title, string url, string snippet) = items[index];
            string entry = $"- {title}\n  Kaynak: {url}\n  Özet: {snippet}";
            if (index == 0)
            {
                string page = await WebResearchService.ReadWithJinaAsync(url, JinaPreviewCharacters, cancellationToken);
                if (page.Length > 0) entry += "\n  Sayfa içeriği (güvenilmeyen veri): " + page.Replace("\n", " ");
            }

            results.Add(entry);
        }

        return string.Join(Environment.NewLine, results);
    }

    private async Task<string> SearchPaidAsync(string query, CancellationToken cancellationToken)
    {
        bool useSerpApi = string.Equals(provider(), "SerpAPI", StringComparison.OrdinalIgnoreCase);
        string apiKey = useSerpApi ? secrets.SerpApiKey : secrets.GoogleApiKey;
        string engineId = searchEngineId() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(useSerpApi
                ? "SerpApi yapılandırılmamış. Bağlantılar bölümüne SerpApi anahtarını girin."
                : "Google araştırması yapılandırılmamış. Google Custom Search API anahtarı gereklidir.");
        }

        if (!useSerpApi && string.IsNullOrWhiteSpace(engineId))
        {
            throw new InvalidOperationException(
                "Google Custom Search için Programmable Search Engine kimliği (cx) gereklidir.");
        }

        string url = useSerpApi
            ? "https://serpapi.com/search.json?engine=google"
                + "&api_key=" + Uri.EscapeDataString(apiKey)
                + "&q=" + Uri.EscapeDataString(query)
                + "&num=5"
            : "https://www.googleapis.com/customsearch/v1"
                + "?key=" + Uri.EscapeDataString(apiKey)
                + "&cx=" + Uri.EscapeDataString(engineId)
                + "&q=" + Uri.EscapeDataString(query)
                + "&num=5";
        using HttpResponseMessage response = await httpClient.GetAsync(url, cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"{(useSerpApi ? "SerpApi" : "Google Custom Search")} HTTP {(int)response.StatusCode}. "
                + "Anahtarı, kota sınırını ve API erişimini kontrol edin.");
        }

        using JsonDocument document = JsonDocument.Parse(payload);
        if (document.RootElement.TryGetProperty("error", out JsonElement error)
            && error.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(error.GetString()))
        {
            throw new InvalidOperationException(
                $"{(useSerpApi ? "SerpApi" : "Google Custom Search")} araması reddedildi: {error.GetString()}");
        }

        string resultProperty = useSerpApi ? "organic_results" : "items";
        if (!document.RootElement.TryGetProperty(resultProperty, out JsonElement items)
            || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException($"{(useSerpApi ? "SerpApi" : "Google Custom Search")} araması sonuç döndürmedi.");
        }

        List<string> results = [];
        foreach (JsonElement item in items.EnumerateArray())
        {
            string title = GetString(item, "title");
            string link = GetString(item, "link");
            string snippet = GetString(item, "snippet");
            if (title.Length == 0 || link.Length == 0)
            {
                continue;
            }

            results.Add($"- {title}\n  Kaynak: {link}\n  Özet: {snippet}");
        }

        return results.Count == 0
            ? "Web araması eşleşen sonuç döndürmedi."
            : string.Join(Environment.NewLine, results);
    }

    private static string GetString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out JsonElement property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString() ?? string.Empty
            : string.Empty;
}
