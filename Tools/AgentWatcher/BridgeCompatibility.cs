namespace ArsanGazERP.AgentWatcher;

internal sealed record BridgeCompatibilityResult(bool IsCompatible, string Detail);

internal static class BridgeCompatibility
{
    internal const string MinimumExtensionVersion = "0.2.17";
    internal const int RequiredProtocolVersion = 2;

    internal static BridgeCompatibilityResult Check(
        string? extensionVersion,
        int protocolVersion,
        bool dispatchBlocked = false)
    {
        if (string.IsNullOrWhiteSpace(extensionVersion))
        {
            return new(
                false,
                $"Bridge heartbeat omits extensionVersion; minimum extension {MinimumExtensionVersion}, protocol {RequiredProtocolVersion}.");
        }

        if (!Version.TryParse(extensionVersion, out Version? actualVersion)
            || !Version.TryParse(MinimumExtensionVersion, out Version? minimumVersion))
        {
            return new(
                false,
                $"Bridge extension version '{extensionVersion}' is invalid; minimum extension {MinimumExtensionVersion}, protocol {RequiredProtocolVersion}.");
        }

        if (actualVersion < minimumVersion)
        {
            return new(
                false,
                $"Bridge extension version is too old: installed {actualVersion}, minimum supported {minimumVersion}; protocol {protocolVersion}, required protocol {RequiredProtocolVersion}.");
        }

        if (protocolVersion != RequiredProtocolVersion)
        {
            return new(
                false,
                $"Bridge protocol mismatch: installed protocol {protocolVersion}, source requires protocol {RequiredProtocolVersion} (minimum extension {MinimumExtensionVersion}).");
        }

        if (dispatchBlocked)
        {
            return new(false, "VS Code Copilot köprüsü belirsiz önceki teslim nedeniyle yeni istekleri duraklattı.");
        }

        return new(true, $"Bridge extension {actualVersion} (minimum {minimumVersion}), protocol {protocolVersion} is compatible.");
    }
}
