using System.Text;
using ArsanGazERP.Services;

namespace ArsanGazERP.ServerAgent;

internal static class ServerPaths
{
    public static string DataRoot => AgentWatcherPaths.DataRoot;
    public static string CopilotInbox => Path.Combine(DataRoot, "CopilotInbox");
    public static string TelegramOutbox => Path.Combine(DataRoot, "TelegramOutbox");
    public static string SentOutbox => Path.Combine(TelegramOutbox, "sent");
    public static string ExpiredOutbox => Path.Combine(TelegramOutbox, "expired");

    public static string StateRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "ServerAgent");

    public static string StatusFile => Path.Combine(StateRoot, "status.json");

    // Request spool of the root release runner (Tools/ServerAgent/deploy/arsangaz-release.sh, arsangaz-release.path).
    public static string ReleaseSpool { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Release");

    // Written by Tools/CursorIdeBridge/Sync-ServerInbox.ps1 on the office PC on every sync pass.
    public static string PcSyncStatusFile => Path.Combine(CopilotInbox, "pc-sync-status.json");

    // Written by the server Cursor agent (Tools/CursorTelegramBridge/server-bridge.js, user cursoragent).
    public static string CursorAgentStatusFile =>
        Environment.GetEnvironmentVariable("CURSOR_AGENT_STATUS_FILE") is { Length: > 0 } path
            ? path
            : "/var/lib/arsangaz-cursoragent/status.json";

    public static void Ensure()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(CopilotInbox);
        Directory.CreateDirectory(TelegramOutbox);
        Directory.CreateDirectory(SentOutbox);
        Directory.CreateDirectory(StateRoot);
    }

    // MobileApi and the reminder service read the notification chat from AgentWatcher's settings.json.
    public static void EnsureNotificationChat(long chatId)
    {
        if (chatId == 0 || AgentChatService.GetTelegramNotificationChatId() == chatId)
        {
            return;
        }

        string path = AgentWatcherPaths.SettingsFile;
        File.WriteAllText(path + ".tmp", "{\n  \"TelegramChatId\": \"" + chatId + "\"\n}\n", new UTF8Encoding(false));
        File.Move(path + ".tmp", path, overwrite: true);
    }
}

internal static class Log
{
    public static void Write(string area, string message) =>
        Console.Out.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} [{area}] {message}");
}
