using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
namespace ArsanGazERP.Services;
public sealed class ProspectAutoRunner
{
    private readonly ProspectDiscoveryService _service = new();
    private static string StatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "prospect-last-run.txt");
    public async Task<string> RunIfDueAsync(CancellationToken token = default)
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("ARSAN_PROSPECT_AUTO_SCAN", EnvironmentVariableTarget.User), "1", StringComparison.Ordinal))
            return "Otomatik müşteri taraması kapalı.";
        if (File.Exists(StatePath) && DateTime.TryParse(await File.ReadAllTextAsync(StatePath, token), out DateTime last) && last > DateTime.UtcNow.AddHours(-24))
            return $"Müşteri Avcısı son 24 saat içinde çalıştı: {last.ToLocalTime():dd.MM.yyyy HH:mm}.";
        string result = await _service.ScanAsync(cancellationToken: token);
        Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
        await File.WriteAllTextAsync(StatePath, DateTime.UtcNow.ToString("O"), token);
        return result;
    }
}
