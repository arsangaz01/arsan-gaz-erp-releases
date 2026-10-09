using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ArsanGazERP.Services;

public sealed class CloudAgentClient
{
    private const int MaximumMessageLength = AgentChatPromptBuilder.MaximumMessageLength;
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(75)
    };

    public static bool IsConfigured
    {
        get
        {
            CloudAgentConnectionSettings settings = CloudAgentSettingsStore.LoadConnection();
            return !string.IsNullOrWhiteSpace(settings.EndpointUrl) && settings.HasAccessToken;
        }
    }

    public async Task<string> CompleteAsync(
        string agentId,
        string message,
        CancellationToken cancellationToken = default,
        string? searchQuery = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(agentId);
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        if (message.Length > MaximumMessageLength)
        {
            throw new ArgumentException(
                $"Mesaj en fazla {MaximumMessageLength:N0} karakter olabilir.",
                nameof(message));
        }
        if (searchQuery is { Length: > 180 })
        {
            throw new ArgumentException("Kamuya açık arama sorgusu en fazla 180 karakter olabilir.", nameof(searchQuery));
        }

        using HttpRequestMessage request = CreateRequest(
            HttpMethod.Post,
            "/api/chat");
        request.Content = JsonContent.Create(new
        {
            agentId,
            message = message.Trim(),
            searchQuery = string.IsNullOrWhiteSpace(searchQuery) ? null : searchQuery.Trim()
        });

        using HttpResponseMessage response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryGetError(payload) ?? response.ReasonPhrase ?? "Bilinmeyen hata";
            throw new InvalidOperationException(
                $"Cloudflare Workers AI HTTP {(int)response.StatusCode}: {detail}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            if (!document.RootElement.TryGetProperty("response", out JsonElement responseValue) ||
                responseValue.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(responseValue.GetString()))
            {
                throw new InvalidDataException("Cloudflare Worker geçerli bir yanıt döndürmedi.");
            }

            return responseValue.GetString()!;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Cloudflare Worker yanıtı geçerli JSON değil.", exception);
        }
    }

    public const int MaximumAgentStepLength = JarvisAgentProtocol.MaximumTranscriptLength;

    public async Task<string> CompleteAgentStepAsync(string transcript, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(transcript);
        if (transcript.Length > MaximumAgentStepLength)
        {
            throw new ArgumentException($"Ajan adımı en fazla {MaximumAgentStepLength:N0} karakter olabilir.", nameof(transcript));
        }

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "/api/chat");
        request.Content = JsonContent.Create(new { agentId = "jarvis", message = transcript, mode = "agent" });
        using HttpResponseMessage response = await Http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryGetError(payload) ?? response.ReasonPhrase ?? "Bilinmeyen hata";
            throw new InvalidOperationException($"Cloudflare Workers AI HTTP {(int)response.StatusCode}: {detail}");
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("response", out JsonElement value) &&
                   value.ValueKind == JsonValueKind.String &&
                   !string.IsNullOrWhiteSpace(value.GetString())
                ? value.GetString()!
                : throw new InvalidDataException("Cloudflare Worker geçerli bir ajan adımı döndürmedi.");
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException("Cloudflare Worker yanıtı geçerli JSON değil.", exception);
        }
    }

    public async Task<string> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "/health");
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            string detail = TryGetError(payload) ?? response.ReasonPhrase ?? "Bilinmeyen hata";
            throw new InvalidOperationException(
                $"Cloudflare Worker bağlantı testi HTTP {(int)response.StatusCode}: {detail}");
        }

        return "Cloudflare Worker erişilebilir. Bu test model çağrısı yapmadı ve AI kotası kullanmadı.";
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        Uri endpoint = new(CloudAgentSettingsStore.GetEndpointUrl() + path);
        HttpRequestMessage request = new(method, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer",
            CloudAgentSettingsStore.GetAccessToken());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return request;
    }

    private static string? TryGetError(string payload)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(payload);
            return document.RootElement.TryGetProperty("error", out JsonElement error) &&
                   error.ValueKind == JsonValueKind.String
                ? error.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
