using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services;

public sealed record FreeLlmProvider(
    string Id,
    string Name,
    string BaseUrl,
    string DefaultModel,
    string ModelHint,
    string KeyUrl,
    string Note);

/// <summary>
/// OpenAI-compatible free-tier providers tried in order after the primary providers fail.
/// Keys are DPAPI-protected per Windows user and shared by the ERP and AgentWatcher.
/// </summary>
public static class FreeLlmChain
{
    public static IReadOnlyList<FreeLlmProvider> Providers { get; } =
    [
        new("groq", "Groq", "https://api.groq.com/openai/v1/", "openai/gpt-oss-120b", "gpt-oss-120b",
            "https://console.groq.com/keys", "Ücretsiz: günde yaklaşık 1000 istek."),
        new("openrouter", "OpenRouter", "https://openrouter.ai/api/v1/", "openrouter/free", "openrouter/free",
            "https://openrouter.ai/settings/keys", "Ücretsiz modeller: günde 50 istek."),
        new("gemini", "Google Gemini", "https://generativelanguage.googleapis.com/v1beta/openai/", "gemini-3.5-flash-lite", "flash-lite",
            "https://aistudio.google.com/app/apikey", "Ücretsiz katmanda gönderilen içerik Google ürünlerini geliştirmek için kullanılabilir."),
    ];

    private const int MaximumErrorLength = 160;
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "FreeLlm");
    private static readonly string ModelsFile = Path.Combine(Folder, "models.json");
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };
    private static readonly object ModelsLock = new();

    public static bool AnyConfigured => Providers.Any(provider => HasKey(provider.Id));

    public static bool HasKey(string providerId) => File.Exists(KeyFile(providerId));

    public static void SaveKey(string providerId, string apiKey)
    {
        string key = apiKey?.Trim() ?? string.Empty;
        if (key.Length < 10) throw new ArgumentException("API anahtarı geçersiz görünüyor.", nameof(apiKey));
        Find(providerId);
        Directory.CreateDirectory(Folder);
        LocalDataProtector.WriteAllBytes(KeyFile(providerId),
            LocalDataProtector.Protect(Encoding.UTF8.GetBytes(key)));
    }

    public static void RemoveKey(string providerId)
    {
        string path = KeyFile(providerId);
        if (File.Exists(path)) File.Delete(path);
    }

    public static string GetModel(string providerId)
    {
        FreeLlmProvider provider = Find(providerId);
        return LoadModels().TryGetValue(providerId, out string? model) && !string.IsNullOrWhiteSpace(model)
            ? model
            : provider.DefaultModel;
    }

    public static async Task<(string Provider, string Text)> CompleteAsync(
        string? systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        List<string> errors = [];
        foreach (FreeLlmProvider provider in Providers)
        {
            string? key = ReadKey(provider.Id);
            if (key is null) continue;
            try
            {
                string text = await SendAsync(provider, key, GetModel(provider.Id), systemPrompt, userPrompt, maxTokens, cancellationToken);
                return (provider.Name, text);
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested && IsProviderFailure(exception))
            {
                errors.Add($"{provider.Name}: {Shorten(exception.Message)}");
            }
        }

        throw new InvalidOperationException(errors.Count == 0
            ? "Ücretsiz yedek sağlayıcılardan hiçbiri yapılandırılmamış."
            : "Ücretsiz yedek sağlayıcılar yanıt vermedi: " + string.Join(" | ", errors));
    }

    /// <summary>Picks an available model from the provider's model list, saves it and sends a short test prompt.</summary>
    public static async Task<string> TestAsync(string providerId, CancellationToken cancellationToken)
    {
        FreeLlmProvider provider = Find(providerId);
        string key = ReadKey(providerId) ?? throw new InvalidOperationException($"{provider.Name} API anahtarı kayıtlı değil.");
        string model = await ResolveModelAsync(provider, key, cancellationToken);
        SaveModel(providerId, model);
        string answer = await SendAsync(provider, key, model, null, "Yalnızca TAMAM yaz.", 256, cancellationToken);
        return $"{provider.Name} çalışıyor ({model}): {Shorten(answer.Trim())}";
    }

    private static async Task<string> ResolveModelAsync(FreeLlmProvider provider, string key, CancellationToken cancellationToken)
    {
        string current = GetModel(provider.Id);
        if (provider.Id == "openrouter") return current;
        using HttpRequestMessage request = new(HttpMethod.Get, provider.BaseUrl + "models");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"{provider.Name} model listesi alınamadı: HTTP {(int)response.StatusCode} {Shorten(raw)}");
        using JsonDocument document = JsonDocument.Parse(raw);
        List<string> ids = document.RootElement.TryGetProperty("data", out JsonElement data) && data.ValueKind == JsonValueKind.Array
            ? data.EnumerateArray()
                .Select(item => item.TryGetProperty("id", out JsonElement id) ? id.GetString() ?? string.Empty : string.Empty)
                .Select(id => id.StartsWith("models/", StringComparison.Ordinal) ? id["models/".Length..] : id)
                .Where(id => id.Length > 0)
                .ToList()
            : [];
        if (ids.Contains(current, StringComparer.OrdinalIgnoreCase)) return current;
        return ids
            .Where(id => id.Contains(provider.ModelHint, StringComparison.OrdinalIgnoreCase)
                && !id.Contains("preview", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(id => id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"{provider.Name} hesabında '{provider.ModelHint}' içeren bir model bulunamadı.");
    }

    private static async Task<string> SendAsync(
        FreeLlmProvider provider,
        string key,
        string model,
        string? systemPrompt,
        string userPrompt,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        List<object> messages = [];
        if (!string.IsNullOrWhiteSpace(systemPrompt)) messages.Add(new { role = "system", content = systemPrompt });
        messages.Add(new { role = "user", content = userPrompt });
        Dictionary<string, object> body = new()
        {
            ["model"] = model,
            ["messages"] = messages,
            ["temperature"] = 0.2,
            // Reasoning models spend part of the budget on hidden thinking tokens.
            ["max_tokens"] = Math.Max(maxTokens, 1024),
            ["stream"] = false,
        };
        if (provider.Id == "groq") body["reasoning_effort"] = "low";

        using HttpRequestMessage request = new(HttpMethod.Post, provider.BaseUrl + "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        if (provider.Id == "openrouter")
        {
            request.Headers.TryAddWithoutValidation("HTTP-Referer", "https://github.com/arsangaz01/arsan-gaz-erp-releases");
            request.Headers.TryAddWithoutValidation("X-Title", "Arsan Gaz ERP");
        }

        request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string raw = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode} {Shorten(raw)}");
        using JsonDocument document = JsonDocument.Parse(raw);
        if (!document.RootElement.TryGetProperty("choices", out JsonElement choices)
            || choices.ValueKind != JsonValueKind.Array
            || choices.GetArrayLength() == 0)
            throw new InvalidOperationException("Yanıt seçeneği dönmedi.");
        JsonElement message = choices[0].GetProperty("message");
        string text = message.TryGetProperty("content", out JsonElement content) && content.ValueKind == JsonValueKind.String
            ? content.GetString() ?? string.Empty
            : string.Empty;
        if (string.IsNullOrWhiteSpace(text)) throw new InvalidOperationException("Boş yanıt döndü.");
        return text;
    }

    private static bool IsProviderFailure(Exception exception) =>
        exception is HttpRequestException or InvalidOperationException or TaskCanceledException
            or JsonException or KeyNotFoundException or CryptographicException;

    private static FreeLlmProvider Find(string providerId) =>
        Providers.FirstOrDefault(provider => provider.Id == providerId)
        ?? throw new ArgumentException("Bilinmeyen sağlayıcı: " + providerId, nameof(providerId));

    private static string KeyFile(string providerId) => Path.Combine(Folder, providerId + ".key.bin");

    private static string? ReadKey(string providerId)
    {
        string path = KeyFile(providerId);
        if (!File.Exists(path)) return null;
        try
        {
            return Encoding.UTF8.GetString(LocalDataProtector.Unprotect(File.ReadAllBytes(path)));
        }
        catch (CryptographicException)
        {
            return null;
        }
    }

    private static Dictionary<string, string> LoadModels()
    {
        lock (ModelsLock)
        {
            try
            {
                return File.Exists(ModelsFile)
                    ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ModelsFile)) ?? []
                    : [];
            }
            catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];
            }
        }
    }

    private static void SaveModel(string providerId, string model)
    {
        lock (ModelsLock)
        {
            Dictionary<string, string> models = LoadModels();
            models[providerId] = model;
            Directory.CreateDirectory(Folder);
            File.WriteAllText(ModelsFile, JsonSerializer.Serialize(models, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
    }

    private static string Shorten(string text)
    {
        string single = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return single.Length <= MaximumErrorLength ? single : single[..MaximumErrorLength] + "...";
    }
}
