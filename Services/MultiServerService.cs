using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ArsanGazERP.Models;

namespace ArsanGazERP.Services;

public sealed class MultiServerService : IDisposable
{
    private readonly MultiServerOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _blockedUntil = new(StringComparer.OrdinalIgnoreCase);
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false
    };

    public string? ActiveServerName { get; private set; }
    public event EventHandler<string>? ActiveServerChanged;

    public MultiServerService(string? configPath = null)
    {
        configPath ??= Path.Combine(AppContext.BaseDirectory, "Config", "servers.json");
        _options = LoadOptions(configPath);

        SocketsHttpHandler handler = new()
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            MaxConnectionsPerServer = 20,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };

        _httpClient = new HttpClient(handler, disposeHandler: true);
        _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP/5.5.4");

        string? apiKey = Environment.GetEnvironmentVariable(_options.ApiKeyEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _httpClient.DefaultRequestHeaders.TryAddWithoutValidation("X-Api-Key", apiKey);
        }
    }

    public async Task<IReadOnlyList<ServerHealthResult>> CheckAllAsync(CancellationToken cancellationToken = default)
    {
        List<ServerEndpoint> servers = OrderedServers().ToList();
        ServerHealthResult[] results = await Task.WhenAll(servers.Select(x => CheckAsync(x, cancellationToken)));
        return results;
    }

    public Task<T?> GetAsync<T>(string relativePath, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Get, relativePath, null, cancellationToken);

    public Task<T?> PostAsync<T>(string relativePath, object body, CancellationToken cancellationToken = default) =>
        SendAsync<T>(HttpMethod.Post, relativePath, body, cancellationToken);

    public async Task<T?> SendAsync<T>(HttpMethod method, string relativePath, object? body = null,
        CancellationToken cancellationToken = default)
    {
        List<Exception> failures = new();

        foreach (ServerEndpoint server in OrderedServers())
        {
            if (IsCoolingDown(server))
            {
                continue;
            }

            try
            {
                using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, 3, 120)));

                Uri requestUri = new(server.GetBaseUri(), relativePath.TrimStart('/'));
                using HttpRequestMessage request = new(method, requestUri);
                if (body is not null)
                {
                    request.Content = new StringContent(JsonSerializer.Serialize(body, _json), Encoding.UTF8, "application/json");
                }

                using HttpResponseMessage response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                string payload = await response.Content.ReadAsStringAsync(timeout.Token);
                if (!response.IsSuccessStatusCode)
                {
                    throw new HttpRequestException($"{server.Name} HTTP {(int)response.StatusCode}: {Limit(payload, 500)}");
                }

                SetActive(server.Name);
                if (typeof(T) == typeof(string))
                {
                    return (T)(object)payload;
                }

                return string.IsNullOrWhiteSpace(payload) ? default : JsonSerializer.Deserialize<T>(payload, _json);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                failures.Add(new InvalidOperationException($"{server.Name}: {ex.Message}", ex));
                Block(server);
            }
        }

        throw new AggregateException("Kullanılabilir ERP sunucusu bulunamadı.", failures);
    }

    private async Task<ServerHealthResult> CheckAsync(ServerEndpoint server, CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        try
        {
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(server.TimeoutSeconds, 3, 120)));
            Uri uri = new(server.GetBaseUri(), server.HealthPath.TrimStart('/'));
            using HttpResponseMessage response = await _httpClient.GetAsync(uri, timeout.Token);
            watch.Stop();
            bool ok = response.IsSuccessStatusCode;
            if (ok) _blockedUntil.TryRemove(server.Name, out _);
            return new(server.Name, server.BaseUrl, ok, (int)response.StatusCode, watch.ElapsedMilliseconds,
                ok ? "Hazır" : $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            watch.Stop();
            Block(server);
            return new(server.Name, server.BaseUrl, false, null, watch.ElapsedMilliseconds, ex.Message);
        }
    }

    private IEnumerable<ServerEndpoint> OrderedServers() =>
        _options.Servers.Where(x => x.Enabled).OrderBy(x => x.Priority).ThenBy(x => x.Name);

    private bool IsCoolingDown(ServerEndpoint server) =>
        _blockedUntil.TryGetValue(server.Name, out DateTimeOffset until) && until > DateTimeOffset.UtcNow;

    private void Block(ServerEndpoint server) =>
        _blockedUntil[server.Name] = DateTimeOffset.UtcNow.AddSeconds(Math.Clamp(_options.FailureCooldownSeconds, 5, 600));

    private void SetActive(string name)
    {
        if (string.Equals(ActiveServerName, name, StringComparison.OrdinalIgnoreCase)) return;
        ActiveServerName = name;
        ActiveServerChanged?.Invoke(this, name);
    }

    private static MultiServerOptions LoadOptions(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Çoklu sunucu yapılandırması bulunamadı.", path);
        MultiServerOptions? options = JsonSerializer.Deserialize<MultiServerOptions>(File.ReadAllText(path),
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { PropertyNameCaseInsensitive = true });
        if (options is null || options.Servers.Count == 0) throw new InvalidOperationException("En az bir sunucu tanımlanmalıdır.");
        _ = options.Servers.Select(x => x.GetBaseUri()).ToList();
        return options;
    }

    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "...";
    public void Dispose() => _httpClient.Dispose();
}
