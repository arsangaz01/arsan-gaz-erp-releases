using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArsanGazERP.Services;

namespace ArsanGazERP.AgentWatcher;

internal sealed record CloudDeferredCommand(long UpdateId, long ChatId, string Text, string ReceivedUtc);

internal sealed record CloudRelayHeartbeat(bool RelayConfigured, IReadOnlyList<CloudDeferredCommand> Deferred);

internal static class CloudTelegramRelay
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(2);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static bool IsWorkerConfigured
    {
        get
        {
            try
            {
                CloudAgentConnectionSettings connection = CloudAgentSettingsStore.LoadConnection();
                return connection.HasAccessToken && !string.IsNullOrWhiteSpace(connection.EndpointUrl);
            }
            catch (InvalidOperationException)
            {
                return false;
            }
        }
    }

    public static async Task<CloudRelayHeartbeat?> SendHeartbeatAsync(CancellationToken cancellationToken)
    {
        if (!IsWorkerConfigured)
        {
            return null;
        }

        using HttpResponseMessage response = await SendAsync("/watcher/heartbeat", "{}", cancellationToken);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        return JsonSerializer.Deserialize<CloudRelayHeartbeat>(
                   await response.Content.ReadAsStringAsync(cancellationToken),
                   JsonOptions)
               ?? throw new InvalidDataException("7/24 röle yanıtı boş.");
    }

    public static async Task AcknowledgeAsync(IReadOnlyCollection<long> updateIds, CancellationToken cancellationToken)
    {
        if (updateIds.Count == 0)
        {
            return;
        }

        using HttpResponseMessage response = await SendAsync(
            "/watcher/ack",
            JsonSerializer.Serialize(new { updateIds }),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    public static async Task<string> ConfigureWorkerSecretsAsync(
        string botToken,
        string chatId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(botToken);
        ArgumentException.ThrowIfNullOrWhiteSpace(chatId);
        string workerDirectory = Path.Combine(
            AgentWatcherWorkspace.FindSourceRoot(AppContext.BaseDirectory)
                ?? throw new DirectoryNotFoundException("ArsanGazERP proje klasörü bulunamadı."),
            "CloudAgentWorker");
        if (!File.Exists(Path.Combine(workerDirectory, "wrangler.jsonc")))
        {
            throw new FileNotFoundException("CloudAgentWorker yapılandırması bulunamadı.", workerDirectory);
        }

        await PutSecretAsync(workerDirectory, "TELEGRAM_BOT_TOKEN", botToken, cancellationToken);
        await PutSecretAsync(workerDirectory, "TELEGRAM_CHAT_ID", chatId, cancellationToken);
        return "7/24 bulut asistanı etkinleştirildi. Bilgisayar 5 dakikadan uzun süre çevrimdışı kalırsa Telegram mesajlarını " +
               "Cloudflare Worker yanıtlar; komutlar bilgisayar açılınca burada çalıştırılır. Sabah özeti her gün 08:00'de gelir.";
    }

    private static async Task PutSecretAsync(
        string workerDirectory,
        string name,
        string value,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("cmd.exe")
        {
            WorkingDirectory = workerDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("npx");
        startInfo.ArgumentList.Add("--yes");
        startInfo.ArgumentList.Add("wrangler");
        startInfo.ArgumentList.Add("secret");
        startInfo.ArgumentList.Add("put");
        startInfo.ArgumentList.Add(name);

        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("wrangler başlatılamadı.");
        Task<string> output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.StandardInput.WriteLineAsync(value.AsMemory(), cancellationToken);
        process.StandardInput.Close();
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await process.WaitForExitAsync(timeout.Token);
        string details = (await output) + "\n" + (await error);
        if (process.ExitCode != 0)
        {
            string redacted = details.Replace(value, "[GİZLİ]", StringComparison.Ordinal);
            throw new InvalidOperationException(
                $"{name} Cloudflare'e kaydedilemedi (çıkış {process.ExitCode}). wrangler oturumunu kontrol edin: " +
                redacted.Trim()[..Math.Min(400, redacted.Trim().Length)]);
        }
    }

    private static async Task<HttpResponseMessage> SendAsync(string path, string body, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, CloudAgentSettingsStore.GetEndpointUrl() + path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", CloudAgentSettingsStore.GetAccessToken());
        return await Http.SendAsync(request, cancellationToken);
    }
}
