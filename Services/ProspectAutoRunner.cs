using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services;

public sealed class ProspectAutoRunner
{
    private readonly ProspectDiscoveryService _discovery = new();

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "ProspectHunter");

    private static string LastRunPath => Path.Combine(StateDirectory, "last-run.txt");
    private static string LogPath => Path.Combine(StateDirectory, "prospect-hunter.log");

    public async Task<string> RunIfDueSafeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (!string.Equals(
                    Environment.GetEnvironmentVariable("ARSAN_PROSPECT_AUTO_SCAN", EnvironmentVariableTarget.User),
                    "1",
                    StringComparison.Ordinal))
            {
                return "Otomatik müşteri taraması kapalı.";
            }

            _ = ProspectDiscoveryService.GetApiKey();
            Directory.CreateDirectory(StateDirectory);

            if (File.Exists(LastRunPath)
                && DateTime.TryParse(await File.ReadAllTextAsync(LastRunPath, cancellationToken), out DateTime lastRun)
                && lastRun > DateTime.UtcNow.AddHours(-24))
            {
                return $"Otomatik tarama güncel: {lastRun.ToLocalTime():dd.MM.yyyy HH:mm}";
            }

            ProspectScanResult result = await _discovery.ScanAsync(cancellationToken: cancellationToken);
            await File.WriteAllTextAsync(LastRunPath, result.CompletedAtUtc.ToString("O"), cancellationToken);
            await AppendLogAsync(result.ToString());
            return result.ToString();
        }
        catch (Exception exception)
        {
            string message = "Otomatik müşteri taraması çalışmadı: " + exception.GetBaseException().Message;
            await AppendLogAsync(message);
            return message;
        }
    }

    private static async Task AppendLogAsync(string message)
    {
        Directory.CreateDirectory(StateDirectory);
        await File.AppendAllTextAsync(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}");
    }
}
