using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using ArsanGazERP.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record SupervisorCheck(string Name, bool IsHealthy, string Details);
public sealed record SupervisorReport(DateTime CheckedAtLocal, bool IsHealthy, IReadOnlyList<SupervisorCheck> Checks);

public sealed class SystemSupervisorService : IDisposable
{
    private static readonly string StateDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "Supervisor");
    private static readonly string ReportPath = Path.Combine(StateDirectory, "latest-health.json");
    private static readonly SemaphoreSlim ReportWriteGate = new(1, 1);

    private readonly CancellationTokenSource _closing = new();
    private Task? _monitorTask;

    public void Start()
    {
        if (_monitorTask is not null)
        {
            return;
        }

        _monitorTask = Task.Run(() => MonitorAsync(_closing.Token));
    }

    public static async Task<SupervisorReport?> ReadLatestReportAsync()
    {
        if (!File.Exists(ReportPath))
        {
            return null;
        }

        await using FileStream stream = new(ReportPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return await JsonSerializer.DeserializeAsync<SupervisorReport>(stream);
    }

    public static async Task<SupervisorReport> CheckNowAsync(CancellationToken cancellationToken = default)
    {
        SupervisorReport report = await CheckSystemAsync(cancellationToken);
        await WriteReportAsync(report, cancellationToken);
        return report;
    }

    public async Task StopAsync()
    {
        _closing.Cancel();
        if (_monitorTask is not null)
        {
            try
            {
                await _monitorTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _closing.Dispose();
    }

    private static async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(45));
        do
        {
            try
            {
                SupervisorReport report = await CheckSystemAsync(cancellationToken);
                await WriteReportAsync(report, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                await WriteFailureReportAsync(exception, cancellationToken);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private static async Task<SupervisorReport> CheckSystemAsync(CancellationToken cancellationToken)
    {
        List<SupervisorCheck> checks = [];

        await using ArsanGazDbContext erp = new();
        checks.Add(await CheckDatabaseAsync("ERP veritabanı", DatabaseKind.Erp, cancellationToken));
        checks.Add(await CheckDatabaseAsync("Ortak CRM veritabanı", DatabaseKind.Crm, cancellationToken));

        DateTime localDayStart = DateTime.Today;
        DateTime todayStartUtc = localDayStart.ToUniversalTime();
        DateTime tomorrowStartUtc = localDayStart.AddDays(1).ToUniversalTime();
        int prospectsFoundToday = await erp.Prospects.CountAsync(
            prospect => prospect.FoundAtUtc >= todayStartUtc && prospect.FoundAtUtc < tomorrowStartUtc,
            cancellationToken);
        await using CrmDbContext crm = new();
        int crmCompaniesAddedToday = await crm.CrmCompanies.CountAsync(
            company => company.CreatedAtUtc >= todayStartUtc && company.CreatedAtUtc < tomorrowStartUtc,
            cancellationToken);
        checks.Add(new(
            "Bugün bulunan adaylar",
            true,
            $"{prospectsFoundToday:N0} yeni aday kaydı; {crmCompaniesAddedToday:N0} firma kaydı CRM'e eklendi."));

        int whatsAppAttempts = await crm.CrmActivities.CountAsync(
            activity => activity.ActivityType == "WhatsApp API gönderimi" &&
                        activity.ActivityAtUtc >= todayStartUtc &&
                        activity.ActivityAtUtc < tomorrowStartUtc,
            cancellationToken);
        int whatsAppAccepted = await crm.CrmActivities.CountAsync(
            activity => activity.ActivityType == "WhatsApp API gönderimi" &&
                        activity.ActivityAtUtc >= todayStartUtc &&
                        activity.ActivityAtUtc < tomorrowStartUtc &&
                        activity.Description.Contains("Meta API isteği kabul etti"),
            cancellationToken);
        checks.Add(new(
            "Bugün kaydedilen müşteri iletişimi",
            true,
            $"WhatsApp API denemesi: {whatsAppAttempts:N0}; Meta'nın kabul ettiği istek: {whatsAppAccepted:N0}. " +
            "Kabul, mesaj teslimatı anlamına gelmez; teslimat teyidi bu sayaçta doğrulanmıyor."));

        NvidiaNimService nim = new();
        checks.Add(new(
            "NVIDIA NIM",
            true,
            nim.IsConfigured
                ? $"İsteğe bağlı yedek yapılandırıldı ({nim.Model})."
                : "İsteğe bağlı yedek yapılandırılmamış; bu durum tek başına sağlık hatası değildir."));

        checks.Add(await EnsureAgentWatcherRunningAsync(cancellationToken));

        return new SupervisorReport(DateTime.Now, checks.All(check => check.IsHealthy), checks);
    }

    private static async Task<SupervisorCheck> EnsureAgentWatcherRunningAsync(CancellationToken cancellationToken)
    {
        string? executable = FindAgentWatcherExecutable();
        if (executable is null)
        {
            return new(
                "AgentWatcher",
                false,
                "AgentWatcher çalışmıyor ve ERP ile aynı dağıtımda yürütülebilir dosyası bulunamadı.");
        }

        string expectedExecutable = Path.GetFullPath(executable);
        (int ProcessId, string? ExecutablePath)? existingProcess = FindAgentWatcherProcess();
        if (existingProcess is { } runningProcess)
        {
            if (runningProcess.ExecutablePath is string runningExecutable &&
                string.Equals(Path.GetFullPath(runningExecutable), expectedExecutable, StringComparison.OrdinalIgnoreCase))
            {
                return new("AgentWatcher", true, $"Bu ERP dağıtımına ait arka plan süreci çalışıyor (PID {runningProcess.ProcessId}).");
            }

            string runningPath = runningProcess.ExecutablePath ?? "çalıştırılabilir dosya yolu okunamadı";
            return new(
                "AgentWatcher",
                false,
                $"Eski veya doğrulanamayan AgentWatcher süreci çalışıyor (PID {runningProcess.ProcessId}, {runningPath}). " +
                "Bekleyen görevleri kontrol edip eski süreci kapattıktan sonra ERP'yi yeniden açın.");
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = executable,
                Arguments = "--background",
                WorkingDirectory = Path.GetDirectoryName(executable)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
        catch (Exception exception) when (
            exception is Win32Exception or FileNotFoundException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new("AgentWatcher", false, "Arka plan izleyicisi başlatılamadı: " + exception.GetBaseException().Message);
        }

        for (int attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            (int ProcessId, string? ExecutablePath)? startedProcess = FindAgentWatcherProcess();
            if (startedProcess is { } detectedProcess)
            {
                if (detectedProcess.ExecutablePath is string runningExecutable &&
                    string.Equals(Path.GetFullPath(runningExecutable), expectedExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    return new("AgentWatcher", true, $"Bu ERP dağıtımı tarafından arka planda başlatıldı (PID {detectedProcess.ProcessId}).");
                }

                string runningPath = detectedProcess.ExecutablePath ?? "çalıştırılabilir dosya yolu okunamadı";
                return new(
                    "AgentWatcher",
                    false,
                    $"Beklenen arka plan süreci yerine eski veya doğrulanamayan AgentWatcher çalışıyor " +
                    $"(PID {detectedProcess.ProcessId}, {runningPath}).");
            }
        }

        return new(
            "AgentWatcher",
            false,
            "Başlatma isteği verildi ancak izleyici süreci 5 saniye içinde doğrulanamadı.");
    }

    private static (int ProcessId, string? ExecutablePath)? FindAgentWatcherProcess()
    {
        foreach (Process process in Process.GetProcessesByName("ArsanGazERP.AgentWatcher"))
        {
            using (process)
            {
                if (!process.HasExited)
                {
                    try
                    {
                        return (process.Id, process.MainModule?.FileName);
                    }
                    catch (Exception exception) when (
                        exception is Win32Exception or InvalidOperationException)
                    {
                        return (process.Id, null);
                    }
                }
            }
        }

        return null;
    }

    private static string? FindAgentWatcherExecutable()
    {
        const string executableName = "ArsanGazERP.AgentWatcher.exe";
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string[] candidates =
            [
                Path.Combine(directory.FullName, "AgentWatcher", executableName),
                Path.Combine(directory.FullName, "Tools", "AgentWatcher", "publish", executableName)
            ];
            string? existing = candidates.FirstOrDefault(File.Exists);
            if (existing is not null)
            {
                return existing;
            }
        }

        return null;
    }

    private static async Task<SupervisorCheck> CheckDatabaseAsync(
        string name,
        DatabaseKind kind,
        CancellationToken cancellationToken)
    {
        if (ArsanDatabase.IsPostgres)
        {
            try
            {
                await using System.Data.Common.DbConnection server = await ArsanDatabase.OpenAsync(kind, readOnly: true, cancellationToken);
                await using System.Data.Common.DbCommand ping = ArsanDatabase.Command(server, null,
                    "SELECT COUNT(*) FROM information_schema.tables WHERE table_schema = current_schema();");
                long tables = Convert.ToInt64(await ping.ExecuteScalarAsync(cancellationToken));
                return new(name, tables > 0, tables > 0
                    ? $"PostgreSQL bağlantısı başarılı ({ArsanDatabase.Describe(kind)}, {tables} tablo)."
                    : $"PostgreSQL şemasında tablo yok: {ArsanDatabase.Describe(kind)}");
            }
            catch (Exception exception) when (exception is System.Data.Common.DbException or TimeoutException or InvalidOperationException)
            {
                return new(name, false, $"PostgreSQL'e bağlanılamadı ({ArsanDatabase.Describe(kind)}): {exception.GetBaseException().Message}");
            }
        }

        string databasePath = ArsanDatabase.SqlitePath(kind);
        if (!File.Exists(databasePath))
        {
            return new(name, false, $"Veritabanı bulunamadı: {databasePath}");
        }

        SqliteConnectionStringBuilder settings = new()
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            DefaultTimeout = 10
        };

        await using SqliteConnection connection = new(settings.ToString());
        await connection.OpenAsync(cancellationToken);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        object? result = await command.ExecuteScalarAsync(cancellationToken);
        string check = Convert.ToString(result) ?? "sonuç yok";
        return new(name, string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase),
            string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase)
                ? "SQLite quick_check başarılı."
                : "SQLite bütünlük kontrolü başarısız: " + check);
    }

    private static async Task WriteFailureReportAsync(Exception exception, CancellationToken cancellationToken)
    {
        SupervisorReport failure = new(
            DateTime.Now,
            false,
            [new SupervisorCheck("Gözcü", false, exception.GetBaseException().Message)]);
        await WriteReportAsync(failure, cancellationToken);
    }

    private static async Task WriteReportAsync(SupervisorReport report, CancellationToken cancellationToken)
    {
        await ReportWriteGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(StateDirectory);
            string temporaryPath = ReportPath + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temporaryPath, ReportPath, true);
        }
        finally
        {
            ReportWriteGate.Release();
        }
    }

    public void Dispose()
    {
        _closing.Cancel();
    }
}
