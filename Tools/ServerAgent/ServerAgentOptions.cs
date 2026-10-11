using System.Globalization;

namespace ArsanGazERP.ServerAgent;

// Everything comes from the systemd EnvironmentFile (/etc/arsangaz/serveragent.env, 0600 root). Secrets are never logged.
internal sealed record ServerAgentOptions(
    string? BotToken,
    long ChatId,
    bool PollingEnabled,
    bool AllowPollingWithoutRelay,
    bool OutboxEnabled,
    bool RemindersEnabled,
    bool CampaignsEnabled,
    string? RelayUrl,
    string? RelayToken)
{
    public bool HasTelegram => BotToken is { Length: > 0 } && ChatId != 0;

    public bool HasRelay => RelayUrl is { Length: > 0 } && RelayToken is { Length: > 0 };

    // Telegram allows one getUpdates consumer. Without the Worker heartbeat the 7/24 relay would also poll and
    // steal messages, so polling needs the relay unless explicitly overridden.
    public bool CanPoll => PollingEnabled && HasTelegram && (HasRelay || AllowPollingWithoutRelay);

    public static ServerAgentOptions FromEnvironment()
    {
        long chatId = long.TryParse(Read("TELEGRAM_CHAT_ID"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long parsed)
            ? parsed
            : 0;
        return new ServerAgentOptions(
            Read("TELEGRAM_BOT_TOKEN"),
            chatId,
            Flag("SERVERAGENT_TELEGRAM_POLLING", false),
            Flag("SERVERAGENT_POLL_WITHOUT_RELAY", false),
            Flag("SERVERAGENT_OUTBOX", true),
            Flag("SERVERAGENT_REMINDERS", true),
            Flag("SERVERAGENT_CAMPAIGNS", false),
            Read("CLOUD_RELAY_URL")?.TrimEnd('/'),
            Read("CLOUD_RELAY_TOKEN"));
    }

    public string Describe() =>
        $"telegram={(HasTelegram ? "var" : "yok")} dinleme={(CanPoll ? "açık" : PollingEnabled ? "istendi-ama-eksik" : "kapalı")} " +
        $"röle={(HasRelay ? "var" : "yok")} gidenKutusu={(OutboxEnabled ? "açık" : "kapalı")} " +
        $"tahsilat={(RemindersEnabled ? "açık" : "kapalı")} kampanya={(CampaignsEnabled ? "açık" : "kapalı")}";

    private static string? Read(string name) =>
        Environment.GetEnvironmentVariable(name) is { } value && value.Trim() is { Length: > 0 } trimmed ? trimmed : null;

    private static bool Flag(string name, bool fallback) =>
        Read(name)?.ToLowerInvariant() switch
        {
            "1" or "true" or "yes" or "evet" => true,
            "0" or "false" or "no" or "hayir" or "hayır" => false,
            _ => fallback
        };
}
