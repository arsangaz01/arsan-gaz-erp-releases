namespace ArsanGazERP.AgentWatcher;

internal static class CodingBridgeNames
{
    public const string Cursor = "Cursor";
    // Kept only so old settings/queue records still deserialize; never selected or launched.
    public const string VsCode = "VS Code";

    // User 2026-10-18: VS Code / Copilot Telegram köprüsü kaldırıldı — her zaman Cursor.
    public static string Normalize(string? bridge) => Cursor;
}
