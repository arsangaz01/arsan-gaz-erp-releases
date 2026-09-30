using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services;

public sealed class ProspectAutoRunner
{
    private readonly ProspectDiscoveryService _discovery = new();
    private static string StateDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "ProspectHunter");
    private static string LastRunPath => Path.Combine(StateDirectory, "last-run-free.txt");
    private static string LogPath => Path.Combine(StateDirectory, "prospect-hunter-free.log");

    public async Task<string> RunIfDueSafeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.Equals(Environment.GetEnvironmentVariable("ARSAN_PROSPECT_FREE_AUTO_SCAN", EnvironmentVariableTarget.User), "1", StringComparison.Ordinal))
                return "Ücretsiz otomatik müşteri taraması kapalı.";

            Directory.CreateDirectory(StateDirectory);
            if (File.Exists(LastRunPath)
                && DateTime.TryParse(await File.ReadAllTextAsync(LastRunPath, cancellationToken), out DateTime lastRun)
                && lastRun > DateTime.UtcNow.AddHours(-24))
                return $"Ücretsiz otomatik tarama güncel: {lastRun.ToLocalTime():dd.MM.yyyy HH:mm}";

            ProspectScanResult result = await _discovery.ScanAsync(cancellationToken: cancellationToken);
            await File.WriteAllTextAsync(LastRunPath, result.CompletedAtUtc.ToString("O"), cancellationToken);
            await AppendLogAsync(result.ToString());
            return result.ToString();
        }
        catch (Exception exception)
        {
            string message = "Ücretsiz otomatik tarama çalışmadı: " + exception.GetBaseException().Message;
            await AppendLogAsync(message);
            return message;
        }
    }

    public static void SetEnabled(bool enabled) =>
        Environment.SetEnvironmentVariable("ARSAN_PROSPECT_FREE_AUTO_SCAN", enabled ? "1" : "0", EnvironmentVariableTarget.User);

    public static bool IsEnabled() =>
        string.Equals(Environment.GetEnvironmentVariable("ARSAN_PROSPECT_FREE_AUTO_SCAN", EnvironmentVariableTarget.User), "1", StringComparison.Ordinal);

    private static async Task AppendLogAsync(string message)
    {
        Directory.CreateDirectory(StateDirectory);
        await File.AppendAllTextAsync(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}");
    }
}
