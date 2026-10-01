using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services.V9;

public sealed class NvidiaNimProductionService : INvidiaNimProductionService, IDisposable
{
    private readonly NvidiaNimOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public NvidiaNimProductionService() : this(new NvidiaNimOptions(), null) { }

    public NvidiaNimProductionService(NvidiaNimOptions options, HttpClient? httpClient)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _ownsClient = httpClient is null;
        _http = httpClient ?? new HttpClient();
        _http.BaseAddress = new Uri(_options.BaseUrl.TrimEnd('/') + "/", UriKind.Absolute);
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 15, 600));
    }

    public async Task<NvidiaNimHealth> CheckHealthAsync(CancellationToken cancellationToken = default)
    {
        string? key = Environment.GetEnvironmentVariable(NvidiaNimOptions.ApiKeyEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(key))
            return new(false, false, _options.Model,
                $"{NvidiaNimOptions.ApiKeyEnvironmentVariable} tanımlı değil", DateTimeOffset.UtcNow);

        using HttpRequestMessage request = CreateRequest(HttpMethod.Get, "v1/models", key);
        try
        {
            using HttpResponseMessage response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return new(true, response.IsSuccessStatusCode, _options.Model,
                response.IsSuccessStatusCode ? "NVIDIA NIM hazır" : $"NVIDIA HTTP {(int)response.StatusCode}",
                DateTimeOffset.UtcNow);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new(true, false, _options.Model, ex.Message, DateTimeOffset.UtcNow);
        }
    }

    public async Task<NvidiaNimResult> CompleteAsync(
        string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userPrompt))
            throw new ArgumentException("Kullanıcı istemi boş olamaz.", nameof(userPrompt));

        string key = Environment.GetEnvironmentVariable(NvidiaNimOptions.ApiKeyEnvironmentVariable)
            ?? throw new InvalidOperationException(
                $"{NvidiaNimOptions.ApiKeyEnvironmentVariable} ortam değişkeni tanımlı değil.");

        NvidiaChatRequest body = new()
        {
            Model = _options.Model,
            Temperature = _options.Temperature,
            MaxTokens = _options.MaxTokens,
            Stream = false,
            Messages =
            [
                new NvidiaChatMessage("system",
                    string.IsNullOrWhiteSpace(systemPrompt) ? "Arsan Gaz ERP iş asistanısın." : systemPrompt),
                new NvidiaChatMessage("user", userPrompt)
            ]
        };

        using HttpRequestMessage request = CreateRequest(HttpMethod.Post, "v1/chat/completions", key);
        request.Content = JsonContent.Create(body, options: JsonOptions);
        using HttpResponseMessage response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            return new(false, string.Empty, _options.Model, 0,
                $"NVIDIA HTTP {(int)response.StatusCode}: {payload}");

        NvidiaChatResponse? result = JsonSerializer.Deserialize<NvidiaChatResponse>(payload, JsonOptions);
        string? content = result?.Choices.FirstOrDefault()?.Message?.Content;
        return string.IsNullOrWhiteSpace(content)
            ? new(false, string.Empty, _options.Model, result?.Usage?.TotalTokens ?? 0,
                "NVIDIA yanıt içeriği boş.")
            : new(true, content, _options.Model, result?.Usage?.TotalTokens ?? 0);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string key)
    {
        HttpRequestMessage request = new(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("ArsanGazERP/9.0");
        return request;
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}
