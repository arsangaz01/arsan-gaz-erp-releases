using System.Diagnostics;

namespace ArsanGazERP.AgentWatcher;

internal static class CodingBridgeLauncher
{
    public const string Cursor = CodingBridgeNames.Cursor;
    public const string VsCode = CodingBridgeNames.VsCode;

    public static string Normalize(string? bridge) => CodingBridgeNames.Normalize(bridge);

    public static string Start(string? bridge)
    {
        // VS Code Telegram/Copilot köprüsü kaldırıldı; seçili olsa bile Cursor açılır.
        if (string.Equals(bridge?.Trim(), VsCode, StringComparison.OrdinalIgnoreCase))
            return "VS Code köprüsü kaldırıldı; Cursor köprüsü kullanılıyor. " + StartCursorBridge();
        return StartCursorBridge();
    }

    public static string StartCursorBridge()
    {
        string workspace = AgentWatcherWorkspace.FindSourceRoot(AppContext.BaseDirectory)
            ?? throw new DirectoryNotFoundException(
                "ArsanGazERP.csproj içeren proje klasörü bulunamadı; Cursor köprüsü başlatılamadı.");
        string bridgeDirectory = Path.Combine(workspace, "Tools", "CursorTelegramBridge");
        string bridgeScript = Path.Combine(bridgeDirectory, "bridge.js");
        if (!File.Exists(bridgeScript))
            throw new FileNotFoundException("Cursor köprü betiği bulunamadı.", bridgeScript);
        if (!Directory.Exists(Path.Combine(bridgeDirectory, "node_modules", "@cursor", "sdk")))
            throw new InvalidOperationException(
                "Cursor SDK kurulu değil; proje klasöründeki Cursor_Telegram_Giris.cmd dosyasını çalıştırın.");

        int runningProcessId = ReadRunningBridgeProcessId();
        if (runningProcessId > 0)
            return $"Cursor köprüsü zaten çalışıyor (PID {runningProcessId}).";

        string node = FindNodeExecutable()
            ?? throw new FileNotFoundException(
                "Node.js (node.exe) bulunamadı; Cursor köprüsü için Node.js LTS kurulmalı.");
        ProcessStartInfo startInfo = new(node)
        {
            WorkingDirectory = bridgeDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };
        startInfo.ArgumentList.Add(bridgeScript);
        using Process process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Cursor köprüsü işletim sistemi tarafından başlatılamadı.");
        return $"Cursor köprüsü başlatıldı (PID {process.Id}, {workspace}).";
    }

    private static int ReadRunningBridgeProcessId()
    {
        string lockPath = Path.Combine(Paths.Root, "cursor-bridge.pid");
        try
        {
            if (!int.TryParse(File.ReadAllText(lockPath).Trim(), out int processId) || processId <= 0)
                return 0;
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited && process.ProcessName.Equals("node", StringComparison.OrdinalIgnoreCase)
                ? processId
                : 0;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return 0;
        }
    }

    private static string? FindNodeExecutable()
    {
        IEnumerable<string> pathCandidates = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory.Trim('"'), "node.exe"));
        string[] installCandidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs", "node.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "nodejs", "node.exe")
        ];
        foreach (string candidate in pathCandidates.Concat(installCandidates))
        {
            try
            {
                if (File.Exists(candidate))
                    return candidate;
            }
            catch (ArgumentException)
            {
                // Malformed PATH entries are skipped.
            }
        }

        return null;
    }
}
