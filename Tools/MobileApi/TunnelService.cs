using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArsanGazERP.MobileApi;

internal sealed partial class TunnelService : BackgroundService
{
    public static string? CurrentUrl { get; private set; }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelUrl();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(5);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (!File.Exists(MobileApiPaths.CloudflaredExe))
            {
                MobileLog.Write("cloudflared.exe bulunamadı; uzaktan erişim kapalı: " + MobileApiPaths.CloudflaredExe);
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                continue;
            }

            DateTime started = DateTime.UtcNow;
            try
            {
                await RunTunnelAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                MobileLog.Write("Tünel hatası: " + exception.GetBaseException().Message);
            }

            CurrentUrl = null;
            delay = DateTime.UtcNow - started > TimeSpan.FromMinutes(5)
                ? TimeSpan.FromSeconds(5)
                : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 300));
            MobileLog.Write($"Tünel kapandı; {delay.TotalSeconds:0} saniye sonra yeniden başlatılacak.");
            await Task.Delay(delay, stoppingToken);
        }
    }

    private static void StopOrphanedTunnels()
    {
        foreach (Process process in Process.GetProcessesByName("cloudflared"))
        {
            using (process)
            {
                try
                {
                    if (string.Equals(process.MainModule?.FileName, MobileApiPaths.CloudflaredExe, StringComparison.OrdinalIgnoreCase))
                    {
                        process.Kill(entireProcessTree: true);
                        MobileLog.Write("Önceki çalışmadan kalan cloudflared kapatıldı (PID " + process.Id + ").");
                    }
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }
    }

    private static async Task RunTunnelAsync(CancellationToken stoppingToken)
    {
        StopOrphanedTunnels();
        ProcessStartInfo start = new(MobileApiPaths.CloudflaredExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            WorkingDirectory = MobileApiPaths.Root
        };
        foreach (string argument in new[] { "tunnel", "--no-autoupdate", "--url", $"http://127.0.0.1:{MobileApiPaths.Port}" })
        {
            start.ArgumentList.Add(argument);
        }

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("cloudflared başlatılamadı.");
        MobileLog.Write("cloudflared başlatıldı (PID " + process.Id + ").");
        process.OutputDataReceived += (_, e) => Inspect(e.Data);
        process.ErrorDataReceived += (_, e) => Inspect(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(stoppingToken);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
    }

    private static void Inspect(string? line)
    {
        if (line is null || QuickTunnelUrl().Match(line) is not { Success: true } match || match.Value == CurrentUrl)
        {
            return;
        }

        CurrentUrl = match.Value;
        string? previous = File.Exists(MobileApiPaths.TunnelUrlFile) ? File.ReadAllText(MobileApiPaths.TunnelUrlFile).Trim() : null;
        File.WriteAllText(MobileApiPaths.TunnelUrlFile, CurrentUrl);
        MobileLog.Write("Uzaktan erişim adresi: " + CurrentUrl);
        if (!string.Equals(previous, CurrentUrl, StringComparison.OrdinalIgnoreCase))
        {
            TelegramNotifier.Send(
                "📱 Mobil ERP sunucu adresi değişti:\n" + CurrentUrl + "\n\n" +
                "Telefonda Arsan Gaz ERP > Bağlantı sekmesinde \"Telegram'dan bağlan\"a basın (panoda adres varsa otomatik alınır). " +
                "Eşleşme anahtarı değişmez.\n\n" +
                "Güncelleme / APK: " + CurrentUrl + "/indir");
        }
    }
}
