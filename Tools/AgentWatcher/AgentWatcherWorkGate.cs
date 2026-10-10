namespace ArsanGazERP.AgentWatcher;

internal sealed class AgentWatcherWorkGate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _waitingQueueWork;
    private bool _disposed;

    internal async Task RunQueueAsync(Func<Task> queueWork, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queueWork);
        ObjectDisposedException.ThrowIf(_disposed, this);

        Interlocked.Increment(ref _waitingQueueWork);
        try
        {
            await _gate.WaitAsync(cancellationToken);
        }
        finally
        {
            Interlocked.Decrement(ref _waitingQueueWork);
        }

        try
        {
            await queueWork();
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> TryRunScheduledAsync(
        Func<bool> hasPriorityQueuedWork,
        Func<CancellationToken, Task> scheduledWork,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(hasPriorityQueuedWork);
        ArgumentNullException.ThrowIfNull(scheduledWork);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Volatile.Read(ref _waitingQueueWork) > 0 ||
            !await _gate.WaitAsync(0, cancellationToken))
        {
            return false;
        }

        try
        {
            if (Volatile.Read(ref _waitingQueueWork) > 0 || hasPriorityQueuedWork())
            {
                return false;
            }

            await scheduledWork(cancellationToken);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
