using ArsanGazERP.Services;

namespace ArsanGazERP.MobileApi;

// Pulls ArsanTüpTakip data into ERP on a timer (read-only on TupTakip Postgres).
internal sealed class TupTakipSyncHostedService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private readonly TupTakipPullSync _sync = new();

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            await RunSafeAsync(stoppingToken);
        }
    }

    internal static async Task<TupTakipSyncRunResult> RunNowAsync(CancellationToken cancellationToken = default)
    {
        TupTakipPullSync sync = new();
        TupTakipSyncRunResult result = await sync.RunAsync(cancellationToken);
        LogResult(result);
        return result;
    }

    private async Task RunSafeAsync(CancellationToken cancellationToken)
    {
        try
        {
            TupTakipSyncRunResult result = await _sync.RunAsync(cancellationToken);
            LogResult(result);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            MobileLog.Write("TupTakip senkron hatası: " + exception.GetBaseException().Message);
        }
    }

    private static void LogResult(TupTakipSyncRunResult result)
    {
        if (result.SkipReason is not null)
        {
            if (TupTakipPullSync.ReadConnectionString() is null)
            {
                return;
            }

            MobileLog.Write(result.SkipReason);
            return;
        }

        MobileLog.Write(result.SummaryLine());
    }
}
