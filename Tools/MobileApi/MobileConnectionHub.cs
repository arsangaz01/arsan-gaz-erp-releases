using System.Diagnostics;
using System.Text.Json;
using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

internal sealed record MobileUpdateInfo(
    string ApiVersion,
    string PublishedMobileVersion,
    int PublishedMobileBuild,
    bool ApkReady,
    long? ApkSizeBytes,
    string DownloadPath);

internal sealed record MobileHubStatus(
    bool ErpRunning,
    bool WatcherRunning,
    bool AgentHostRunning,
    bool TelegramConfigured,
    bool CursorBridgeActive,
    string? CursorBridgeStage,
    bool MobileTunnelReady,
    string? PublicServerUrl,
    string MobileApiVersion,
    string IntegrationHint);

internal static class MobileConnectionHub
{
    internal static MobileUpdateInfo GetUpdateInfo()
    {
        long? size = File.Exists(MobileApiPaths.ApkFile) ? new FileInfo(MobileApiPaths.ApkFile).Length : null;
        return new MobileUpdateInfo(
            MobileReleaseInfo.ApiVersion,
            MobileReleaseInfo.PublishedMobileVersion,
            MobileReleaseInfo.PublishedMobileBuild,
            size is > 0,
            size,
            "/indir");
    }

    internal static MobileHubStatus GetHubStatus()
    {
        bool erp = IsProcessRunning("ArsanGazERP");
        bool watcher = IsProcessRunning("ArsanGazERP.AgentWatcher");
        bool agentHost = IsProcessRunning("ArsanGazERP.AgentHost");
        bool telegram = AgentChatService.GetTelegramNotificationChatId() is not null;
        (bool bridgeActive, string? stage) = ReadCursorBridgeStatus();
        string? publicUrl = ReadPublicUrl();

        string hint = BuildHint(erp, watcher, telegram, bridgeActive, publicUrl);

        return new MobileHubStatus(
            erp,
            watcher,
            agentHost,
            telegram,
            bridgeActive,
            stage,
            publicUrl is { Length: > 0 },
            publicUrl,
            MobileReleaseInfo.ApiVersion,
            hint);
    }

    internal static string? ReadPublicUrl()
    {
        if (File.Exists(MobileApiPaths.TunnelUrlFile))
        {
            string fromFile = File.ReadAllText(MobileApiPaths.TunnelUrlFile).Trim();
            if (fromFile.Length > 0)
            {
                return fromFile;
            }
        }

        return string.IsNullOrWhiteSpace(TunnelService.CurrentUrl) ? null : TunnelService.CurrentUrl;
    }

    private static string BuildHint(bool erp, bool watcher, bool telegram, bool cursorBridge, string? publicUrl)
    {
        if (publicUrl is null or { Length: 0 })
        {
            return "Uzaktan adres henüz hazır değil. Bilgisayarda MobileApi ve tünel çalışıyor olmalı.";
        }

        if (!telegram)
        {
            return "Telegram sohbeti yapılandırılmamış; PC bildirimleri ve adres paylaşımı sınırlı.";
        }

        if (watcher && cursorBridge)
        {
            return "Telegram ↔ PC (Watcher/Cursor) ↔ mobil ERP hattı aktif. Telegram'dan PC'ye, telefondan Ajan sekmesinden ERP ajanlarına yazabilirsiniz.";
        }

        if (watcher)
        {
            return "Watcher çalışıyor; Telegram PC köprüsü için Cursor/bridge durumunu kontrol edin.";
        }

        if (erp)
        {
            return "ERP açık; Watcher kapalıysa Telegram görev kuyruğu ve PC köprüsü çalışmaz.";
        }

        return "Bilgisayarda ERP ve Watcher kapalı görünüyor; telefon yalnızca sunucu açıkken veri alır.";
    }

    private static (bool Active, string? Stage) ReadCursorBridgeStatus()
    {
        try
        {
            string inbox = Path.Combine(AgentWatcherPaths.DataRoot, "CopilotInbox");
            string statusPath = Path.Combine(inbox, "bridge-status.json");
            if (!File.Exists(statusPath))
            {
                return (false, null);
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(statusPath));
            JsonElement root = document.RootElement;
            string? stage = root.TryGetProperty("stage", out JsonElement stageElement)
                ? stageElement.GetString()
                : root.TryGetProperty("Stage", out JsonElement legacyStage)
                    ? legacyStage.GetString()
                    : null;
            bool active = root.TryGetProperty("connected", out JsonElement connected) && connected.ValueKind == JsonValueKind.True
                || root.TryGetProperty("Connected", out JsonElement legacyConnected) && legacyConnected.ValueKind == JsonValueKind.True
                || !string.IsNullOrWhiteSpace(stage);
            return (active, stage);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            MobileLog.Write("Cursor bridge durumu okunamadı: " + exception.Message);
            return (false, null);
        }
    }

    private static bool IsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Any(process => !process.HasExited);
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }
}
