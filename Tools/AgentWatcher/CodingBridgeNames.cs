namespace ArsanGazERP.AgentWatcher;

internal static class CodingBridgeNames
{
    public const string Cursor = "Cursor";
    public const string VsCode = "VS Code";

    public static string Normalize(string? bridge) =>
        string.Equals(bridge?.Trim(), VsCode, StringComparison.OrdinalIgnoreCase) ? VsCode : Cursor;
}
