namespace ArsanGazERP.Services;

public static class BackgroundWorkShutdown
{
    public static async Task CancelAndDrainAsync(
        CancellationTokenSource cancellation,
        params Task?[] backgroundWork)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        cancellation.Cancel();

        try
        {
            await Task.WhenAll(backgroundWork.OfType<Task>());
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }
}
