using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ArsanGazERP.MobileApi;

// Quick tunnel URLs change whenever cloudflared restarts, so cloudflared outlives MobileApi restarts:
// a running tunnel is adopted instead of replaced, and only a dead tunnel gets a new address.
internal sealed partial class TunnelService : BackgroundService
{
    private static readonly string TunnelLogFile = Path.Combine(MobileApiPaths.Root, "cloudflared.log");

    // A named tunnel (fixed domain) is used instead of a quick tunnel when this cloudflared config exists.
    private static string NamedTunnelConfig => Path.Combine(MobileApiPaths.Root, "named-tunnel.yml");

    private static bool UsesNamedTunnel => File.Exists(NamedTunnelConfig) && MobilePublicUrl.IsStableConfigured;

    // The public tunnel now runs on the server; this marker keeps the PC from starting a second connector.
    // Deleting the file re-enables the PC tunnel within a minute (rollback) without restarting MobileApi.
    internal static string DisabledMarker => Path.Combine(MobileApiPaths.Root, "tunnel-disabled.txt");

    private static bool IsDisabled => File.Exists(DisabledMarker);

    public static string? CurrentUrl { get; private set; }

    internal static void ApplyPreferredPublicUrl()
    {
        if (MobilePublicUrl.ReadPreferred() is not { Length: > 0 } url)
        {
            return;
        }

        CurrentUrl = url;
        if (MobilePublicUrl.IsStableConfigured)
        {
            File.WriteAllText(MobileApiPaths.TunnelUrlFile, url);
            MobileLog.Write("Sabit mobil adres yüklendi: " + url);
        }
    }

    [GeneratedRegex(@"https://[a-z0-9-]+\.trycloudflare\.com", RegexOptions.IgnoreCase)]
    private static partial Regex QuickTunnelUrl();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        TimeSpan delay = TimeSpan.FromSeconds(5);
        bool disabledLogged = false;
        while (!stoppingToken.IsCancellationRequested)
        {
            if (IsDisabled)
            {
                if (!disabledLogged)
                {
                    disabledLogged = true;
                    StopOwnTunnels();
                    MobileLog.Write("Tünel bu bilgisayarda kapalı (" + Path.GetFileName(DisabledMarker) + "); genel adres sunucudan yayınlanıyor.");
                }

                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
                continue;
            }

            if (disabledLogged)
            {
                disabledLogged = false;
                MobileLog.Write("Tünel kapatma işareti kaldırıldı; bu bilgisayarın tüneli yeniden başlatılıyor.");
            }

            if (!File.Exists(MobileApiPaths.CloudflaredExe))
            {
                MobileLog.Write("cloudflared.exe bulunamadı; uzaktan erişim kapalı: " + MobileApiPaths.CloudflaredExe);
                await Task.Delay(TimeSpan.FromMinutes(10), stoppingToken);
                continue;
            }

            DateTime started = DateTime.UtcNow;
            try
            {
                using Process tunnel = AdoptRunningTunnel() ?? await StartTunnelAsync(stoppingToken);
                while (!tunnel.HasExited)
                {
                    await Task.WhenAny(tunnel.WaitForExitAsync(stoppingToken), Task.Delay(TimeSpan.FromSeconds(15), stoppingToken));
                    stoppingToken.ThrowIfCancellationRequested();
                    if (IsDisabled)
                    {
                        break;
                    }
                }

                if (IsDisabled)
                {
                    continue;
                }
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

    private static List<Process> FindOwnTunnels()
    {
        List<Process> own = [];
        foreach (Process process in Process.GetProcessesByName("cloudflared"))
        {
            try
            {
                if (string.Equals(process.MainModule?.FileName, MobileApiPaths.CloudflaredExe, StringComparison.OrdinalIgnoreCase))
                {
                    own.Add(process);
                    continue;
                }
            }
            catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
            }

            process.Dispose();
        }

        return own;
    }

    private static void StopOwnTunnels()
    {
        foreach (Process process in FindOwnTunnels())
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    MobileLog.Write("Bu bilgisayardaki cloudflared kapatıldı (PID " + process.Id + ").");
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }
    }

    private static Process? AdoptRunningTunnel()
    {
        List<Process> running = FindOwnTunnels();
        string? url = UsesNamedTunnel ? NamedTunnelReady() : ReadUrlFromLog();
        if (running.Count == 1 && url is not null)
        {
            MobileLog.Write($"Çalışan tünel devralındı (PID {running[0].Id}).");
            Publish(url);
            return running[0];
        }

        foreach (Process process in running)
        {
            using (process)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                    MobileLog.Write("Adresi okunamayan eski cloudflared kapatıldı (PID " + process.Id + ").");
                }
                catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                }
            }
        }

        return null;
    }

    private static async Task<Process> StartTunnelAsync(CancellationToken stoppingToken)
    {
        File.Delete(TunnelLogFile);
        ProcessStartInfo start = new(MobileApiPaths.CloudflaredExe)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = MobileApiPaths.Root
        };
        bool named = UsesNamedTunnel;
        string[] arguments = named
            ? ["tunnel", "--no-autoupdate", "--logfile", TunnelLogFile, "--config", NamedTunnelConfig, "run"]
            : ["tunnel", "--no-autoupdate", "--logfile", TunnelLogFile, "--url", $"http://127.0.0.1:{MobileApiPaths.Port}"];
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        Process process = Process.Start(start) ?? throw new InvalidOperationException("cloudflared başlatılamadı.");
        MobileLog.Write($"cloudflared başlatıldı (PID {process.Id}, {(named ? "sabit alan adı" : "geçici adres")}).");
        for (int attempt = 0; attempt < 120 && !process.HasExited; attempt++)
        {
            if ((named ? NamedTunnelReady() : ReadUrlFromLog()) is { } url)
            {
                Publish(url);
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500), stoppingToken);
        }

        return process;
    }

    private static string? ReadUrlFromLog()
    {
        Match match = QuickTunnelUrl().Match(ReadLog());
        return match.Success ? match.Value : null;
    }

    // The stable URL once the named tunnel has a live edge connection; a quick tunnel's log never qualifies.
    private static string? NamedTunnelReady()
    {
        string log = ReadLog();
        return log.Contains("Registered tunnel connection", StringComparison.Ordinal) && !QuickTunnelUrl().IsMatch(log)
            ? MobilePublicUrl.ReadStable()
            : null;
    }

    private static string ReadLog()
    {
        try
        {
            if (!File.Exists(TunnelLogFile))
            {
                return string.Empty;
            }

            using FileStream stream = new(TunnelLogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using StreamReader reader = new(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    private static void Publish(string url)
    {
        if (MobilePublicUrl.IsStableConfigured)
        {
            CurrentUrl = MobilePublicUrl.ReadStable();
            MobileLog.Write("Sabit mobil adres kullanılıyor; geçici tünel adresi yok sayıldı.");
            return;
        }

        CurrentUrl = url;
        string? previous = File.Exists(MobileApiPaths.TunnelUrlFile) ? File.ReadAllText(MobileApiPaths.TunnelUrlFile).Trim() : null;
        File.WriteAllText(MobileApiPaths.TunnelUrlFile, url);
        MobileLog.Write("Uzaktan erişim adresi: " + url);
        if (!string.Equals(previous, url, StringComparison.OrdinalIgnoreCase))
        {
            string deepLink = MobilePublicUrl.BuildAppDeepLink(url);
            TelegramNotifier.Send(
                "📱 Mobil ERP sunucu adresi değişti:\n" + url + "\n\n" +
                "Telefonda tek dokunuş:\n" + deepLink + "\n\n" +
                "Eşleşmiş telefon uygulama açıkken adresi otomatik de günceller. Eşleşme anahtarı değişmez.\n\n" +
                "Güncelleme / APK: " + url + "/indir");
        }
    }
}
