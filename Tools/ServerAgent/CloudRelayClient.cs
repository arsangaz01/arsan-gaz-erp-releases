using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.ServerAgent;

internal sealed record RelayDeferredCommand(long UpdateId, long ChatId, string Text, string ReceivedUtc);

internal sealed record RelayHeartbeat(bool RelayConfigured, IReadOnlyList<RelayDeferredCommand> Deferred);

// Same protocol as AgentWatcher's CloudTelegramRelay: while heartbeats arrive the Worker stays silent, and it hands
// back the commands it recorded while no listener was alive.
internal sealed class CloudRelayClient(string endpointUrl, string accessToken) : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(30) };

    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(2);

    public async Task<RelayHeartbeat?> HeartbeatAsync(CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await PostAsync("/watcher/heartbeat", "{}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<RelayHeartbeat>(await response.Content.ReadAsStringAsync(cancellationToken), JsonOptions);
    }

    public async Task AcknowledgeAsync(IReadOnlyCollection<long> updateIds, CancellationToken cancellationToken)
    {
        if (updateIds.Count == 0)
        {
            return;
        }

        using HttpResponseMessage response = await PostAsync("/watcher/ack", JsonSerializer.Serialize(new { updateIds }), cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task<HttpResponseMessage> PostAsync(string path, string body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, endpointUrl + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await http.SendAsync(request, cancellationToken);
    }

    public void Dispose() => http.Dispose();
}
