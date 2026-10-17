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
        MobileApkVersion published = MobileApkPublishInfo.ResolvePublishedVersion();
        // Absolute URL when the public host is known so agents/phones never confuse ERP /indir with TupTakip /tuptakip/indir.
        string download = AbsoluteDownloadUrl("/indir");
        return new MobileUpdateInfo(
            MobileReleaseInfo.ApiVersion,
            published.VersionName,
            published.VersionCode,
            size is > 0,
            size,
            download);
    }

    internal static string AbsoluteDownloadUrl(string path)
    {
        path = path.StartsWith('/') ? path : "/" + path;
        string? root = ReadPublicUrl()?.TrimEnd('/');
        return string.IsNullOrWhiteSpace(root) ? path : root + path;
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

    internal static string? ReadPublicUrl() => MobilePublicUrl.ReadPreferred();

    private static string BuildHint(bool erp, bool watcher, bool telegram, bool cursorBridge, string? publicUrl)
    {
        if (!OperatingSystem.IsWindows())
        {
            return "Sunucu (Linux) sürümü: bilgisayar kontrol komutları kapalı; ERP, Watcher ve Cursor köprüsü ofis bilgisayarında çalışır ve buradan izlenemez.";
        }

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
            return "Telegram ↔ PC (Watcher/Cursor) ↔ mobil ERP hattı aktif. Ajan sekmesinde \"Kime\" ile ERP ajanlarına veya Cursor'a yazabilirsiniz.";
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
            // ide-watch.ps1 refreshes its heartbeat every 3 s and bridge.js every 5 s.
            if (IsFreshHeartbeat(Path.Combine(inbox, "ide-session-status.json"), TimeSpan.FromSeconds(90), requireReady: false))
            {
                return (true, "Cursor sohbeti dinliyor");
            }

            return IsFreshHeartbeat(Path.Combine(inbox, "bridge-status.json"), TimeSpan.FromSeconds(60), requireReady: true)
                ? (true, "Yedek Cursor köprüsü hazır")
                : (false, null);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            MobileLog.Write("Cursor bridge durumu okunamadı: " + exception.Message);
            return (false, null);
        }
    }

    private static bool IsFreshHeartbeat(string path, TimeSpan maxAge, bool requireReady)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (requireReady && !(root.TryGetProperty("ready", out JsonElement ready) && ready.ValueKind == JsonValueKind.True))
        {
            return false;
        }

        return root.TryGetProperty("updatedUtc", out JsonElement updated)
            && updated.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(updated.GetString(), out DateTimeOffset updatedUtc)
            && DateTimeOffset.UtcNow - updatedUtc < maxAge;
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
