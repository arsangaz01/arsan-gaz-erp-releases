namespace ArsanGazERP.AgentWatcher;

internal static class AgentWatcherShutdown
{
    public static async Task DrainAsync(
        Action stopScheduling,
        CancellationTokenSource closing,
        Action cancelActiveWork,
        Func<Task> stopTelegramPollingAsync,
        Func<Task?> startupWork,
        Func<Task?> scheduledWork,
        Func<Task?> queuedWork,
        Func<Task> disposeAgentRuntimeAsync,
        Action disposeHttpClients)
    {
        List<Exception> failures = [];
        Capture(stopScheduling, failures);
        Capture(closing.Cancel, failures);
        Capture(cancelActiveWork, failures);

        try
        {
            await stopTelegramPollingAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await Task.WhenAll(
                new[] { startupWork(), scheduledWork(), queuedWork() }
                    .OfType<Task>());
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            await disposeAgentRuntimeAsync();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }

        try
        {
            disposeHttpClients();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
        finally
        {
            closing.Dispose();
        }

        if (failures.Count > 0)
        {
            throw new AggregateException("AgentWatcher shutdown encountered cleanup failures.", failures);
        }
    }

    private static void Capture(Action action, ICollection<Exception> failures)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }
}
