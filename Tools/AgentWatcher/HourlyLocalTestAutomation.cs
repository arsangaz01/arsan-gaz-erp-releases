using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed record HourlyLocalAutomationStatus(
    bool Enabled,
    int RunsInWindow,
    DateTimeOffset? NextRunUtc,
    string LastOutcome,
    string LastReport,
    bool IsUnavailable);

internal sealed class HourlyLocalAutomationState
{
    public int Version { get; set; } = 1;
    public bool Enabled { get; set; }
    public DateTimeOffset? RequestedAtUtc { get; set; }
    public DateTimeOffset? WindowStartedUtc { get; set; }
    public int RunsInWindow { get; set; }
    public DateTimeOffset? LastRunStartedUtc { get; set; }
    public DateTimeOffset? LastRunFinishedUtc { get; set; }
    public DateTimeOffset? NextRunUtc { get; set; }
    public string LastOutcome { get; set; } = "Never";
    public string LastReport { get; set; } = "Kapalı; saatlik opt-in bekleniyor.";
    public long RunSequence { get; set; }
    public string? ActiveRunId { get; set; }
}

internal sealed class HourlyLocalTestAutomation : IDisposable
{
    internal const int MaximumRunsPerWindow = 24;
    internal static readonly TimeSpan ScheduleInterval = TimeSpan.FromHours(1);
    internal static readonly TimeSpan WindowDuration = TimeSpan.FromHours(24);
    internal static readonly TimeSpan MaximumRunDuration = TimeSpan.FromMinutes(20);

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly string _statePath;
    private readonly Func<CancellationToken, Task<string>> _runFixedSuite;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _maximumRunTime;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _sync = new();
    private HourlyLocalAutomationState _state;
    private CancellationTokenSource? _activeRun;
    private string? _persistenceFault;
    private bool _disposed;

    internal HourlyLocalTestAutomation(string statePath, string workspaceRoot)
        : this(statePath, workspaceRoot, token => LocalTestAutomation.RunFixedTestSuiteAsync(workspaceRoot, token))
    {
    }

    internal HourlyLocalTestAutomation(
        string statePath,
        string workspaceRoot,
        Func<CancellationToken, Task<string>> runFixedSuite,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? maximumRunTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(runFixedSuite);
        _statePath = Path.GetFullPath(statePath);
        _runFixedSuite = runFixedSuite;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maximumRunTime = maximumRunTime ?? MaximumRunDuration;
        if (_maximumRunTime <= TimeSpan.Zero || _maximumRunTime > MaximumRunDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRunTime),
                $"Saatlik test çalışması en fazla {MaximumRunDuration.TotalMinutes:N0} dakika sürebilir.");
        }

        _state = LoadState();
        if (_persistenceFault is null && _state.LastOutcome is "Running" or "Stopping")
        {
            _state.Enabled = false;
            _state.LastOutcome = "Uncertain";
            _state.LastReport = "Yeniden başlatmada koşu sonucu belirsiz bulundu; otomatik tekrar yapılmadı.";
            _state.ActiveRunId = null;
            _state.NextRunUtc = null;
            SaveStateOrFailClosed();
        }
    }

    internal HourlyLocalAutomationStatus Status
    {
        get
        {
            lock (_sync)
            {
                return new(
                    _persistenceFault is null && _state.Enabled,
                    _state.RunsInWindow,
                    _state.NextRunUtc,
                    _persistenceFault is null ? _state.LastOutcome : "Unavailable",
                    _persistenceFault is null
                        ? _state.LastReport
                        : "Durum okunamadı; saatlik otomasyon fail-closed kapalı.",
                    _persistenceFault is not null);
            }
        }
    }

    internal bool EnableHourly()
    {
        lock (_sync)
        {
            ThrowIfUnavailable();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Çalışan saatlik test durdurulmadan yeniden etkinleştirilemez.");
            }

            if (_state.Enabled)
            {
                return false;
            }

            DateTimeOffset now = _clock().ToUniversalTime();
            DateTimeOffset? previousWindowStarted = _state.WindowStartedUtc;
            bool windowExpired = previousWindowStarted is null ||
                now >= previousWindowStarted.Value.Add(WindowDuration);
            _state = new HourlyLocalAutomationState
            {
                Enabled = true,
                RequestedAtUtc = now,
                WindowStartedUtc = windowExpired ? now : previousWindowStarted,
                RunsInWindow = windowExpired ? 0 : _state.RunsInWindow,
                LastRunStartedUtc = _state.LastRunStartedUtc,
                LastRunFinishedUtc = _state.LastRunFinishedUtc,
                NextRunUtc = NextUtcHourBoundary(now),
                LastOutcome = "Armed",
                RunSequence = _state.RunSequence,
                LastReport = windowExpired
                    ? "Saatlik yerel test opt-in edildi; çalışma yalnız uygun UTC saat sınırında başlar."
                    : "Saatlik yerel test yeniden etkinleştirildi; mevcut 24 saatlik kota korunuyor."
            };
            SaveStateOrFailClosed();
            return _persistenceFault is null;
        }
    }

    internal void Stop()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            if (_persistenceFault is not null || _disposed)
            {
                return;
            }

            _state.Enabled = false;
            _state.NextRunUtc = null;
            _state.LastOutcome = _activeRun is null ? "Stopped" : "Stopping";
            active = _activeRun;
            SaveStateOrFailClosed();
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task<bool> TickAsync(CancellationToken cancellationToken)
    {
        bool acquired;
        try
        {
            acquired = await _runGate.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (!acquired)
        {
            return false;
        }

        CancellationTokenSource? runCancellation = null;
        string? runId = null;
        try
        {
            lock (_sync)
            {
                if (_disposed || _persistenceFault is not null || !_state.Enabled)
                {
                    return false;
                }

                DateTimeOffset now = _clock().ToUniversalTime();
                if (_state.WindowStartedUtc is not { } windowStarted ||
                    now >= windowStarted.Add(WindowDuration))
                {
                    _state.WindowStartedUtc = now;
                    _state.RunsInWindow = 0;
                }

                if (_state.NextRunUtc is not { } nextRun || now < nextRun)
                {
                    return false;
                }

                if (_state.RunsInWindow >= MaximumRunsPerWindow)
                {
                    _state.NextRunUtc = _state.WindowStartedUtc!.Value.Add(WindowDuration);
                    SaveStateOrFailClosed();
                    return false;
                }

                if (_state.RunSequence == long.MaxValue)
                {
                    DisableBeforeRun("RunSequenceLimit");
                    return false;
                }

                _state.RunSequence++;
                runId = "hourly-local-" + _state.RunSequence.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _state.RunsInWindow++;
                _state.LastRunStartedUtc = now;
                _state.LastRunFinishedUtc = null;
                _state.NextRunUtc = now.Add(ScheduleInterval);
                _state.LastOutcome = "Running";
                _state.LastReport = "Sabit yerel test paketi çalışıyor; dış çağrı veya iş verisi mutasyonu yok.";
                _state.ActiveRunId = runId;
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                runCancellation.CancelAfter(_maximumRunTime);
                _activeRun = runCancellation;
                SaveStateOrFailClosed();
                if (_persistenceFault is not null)
                {
                    _activeRun = null;
                    runCancellation.Dispose();
                    return false;
                }
            }

            _ = await _runFixedSuite(runCancellation!.Token);
            CompleteRun(runId!, "Passed");
            return true;
        }
        catch (OperationCanceledException)
        {
            bool stopped;
            lock (_sync)
            {
                stopped = !_state.Enabled;
            }

            CompleteRun(
                runId ?? _state.ActiveRunId ?? "",
                stopped ? "Stopped" : cancellationToken.IsCancellationRequested ? "Uncertain" : "TimedOut");
            return true;
        }
        catch (Exception)
        {
            CompleteRun(runId ?? _state.ActiveRunId ?? "", "Failed");
            return true;
        }
        finally
        {
            lock (_sync)
            {
                if (ReferenceEquals(_activeRun, runCancellation))
                {
                    _activeRun = null;
                }
            }

            runCancellation?.Dispose();
            _runGate.Release();
        }
    }

    public void Dispose()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            active = _activeRun;
        }

        try
        {
            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        _runGate.Dispose();
    }

    internal static DateTimeOffset NextUtcHourBoundary(DateTimeOffset value)
    {
        long hourTicks = TimeSpan.TicksPerHour;
        long nextTicks = (value.ToUniversalTime().Ticks / hourTicks + 1) * hourTicks;
        return new DateTimeOffset(nextTicks, TimeSpan.Zero);
    }

    private HourlyLocalAutomationState LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return new HourlyLocalAutomationState();
        }

        try
        {
            HourlyLocalAutomationState state = JsonSerializer.Deserialize<HourlyLocalAutomationState>(
                File.ReadAllText(_statePath),
                JsonOptions)
                ?? throw new InvalidDataException("Saatlik durum kaydı boş.");
            ValidateState(state);
            return state;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _persistenceFault = "Saatlik durum geçersiz; otomasyon kapalı ve kayıt korunmuş durumda. "
                + exception.GetType().Name;
            return new HourlyLocalAutomationState();
        }
    }

    private static void ValidateState(HourlyLocalAutomationState state)
    {
        static bool IsUtc(DateTimeOffset? value) => value is null || value.Value.Offset == TimeSpan.Zero;
        bool activeOutcome = state.LastOutcome is "Running" or "Stopping";
        bool invalidActiveRun = activeOutcome
            ? state.ActiveRunId is null || state.LastRunStartedUtc is null ||
                (state.LastOutcome == "Running" && !state.Enabled) ||
                (state.LastOutcome == "Stopping" && state.Enabled)
            : state.ActiveRunId is not null;

        if (state.Version != 1 ||
            state.RunsInWindow is < 0 or > MaximumRunsPerWindow ||
            state.RunSequence < 0 ||
            state.LastOutcome is not ("Never" or "Armed" or "Running" or "Passed" or "Failed"
                or "TimedOut" or "Stopped" or "Stopping" or "Uncertain" or "RunSequenceLimit") ||
            state.LastReport is null ||
            state.LastReport.Length > 1_000 ||
            !IsUtc(state.RequestedAtUtc) ||
            !IsUtc(state.WindowStartedUtc) ||
            !IsUtc(state.LastRunStartedUtc) ||
            !IsUtc(state.LastRunFinishedUtc) ||
            !IsUtc(state.NextRunUtc) ||
            state.Enabled && (state.RequestedAtUtc is null || state.WindowStartedUtc is null || state.NextRunUtc is null) ||
            !state.Enabled && state.NextRunUtc is not null ||
            invalidActiveRun ||
            state.ActiveRunId is { Length: > 80 })
        {
            throw new InvalidDataException("Saatlik otomasyon durumu veya sınırları geçersiz.");
        }
    }

    private void CompleteRun(string runId, string outcome)
    {
        lock (_sync)
        {
            if (_state.ActiveRunId != runId)
            {
                return;
            }

            bool stopped = !_state.Enabled || _state.LastOutcome == "Stopping";
            _state.Enabled = outcome == "Passed" && !stopped;
            _state.LastOutcome = stopped ? "Stopped" : outcome;
            _state.LastReport = _state.LastOutcome switch
            {
                "Passed" => "Sabit yerel test paketi başarıyla tamamlandı.",
                "Failed" => "Sabit yerel test paketi başarısız oldu; saatlik mod durduruldu.",
                "TimedOut" => "Yerel test 20 dakikalık çalışma sınırını aştı; saatlik mod durduruldu.",
                "Uncertain" => "Uygulama kapanırken sonuç belirsiz kaldı; otomatik tekrar yapılmadı.",
                _ => "Saatlik yerel mod durduruldu; otomatik tekrar yapılmadı."
            };
            _state.ActiveRunId = null;
            _state.LastRunFinishedUtc = _clock().ToUniversalTime();
            if (!_state.Enabled)
            {
                _state.NextRunUtc = null;
            }

            SaveStateOrFailClosed();
        }
    }

    private void DisableBeforeRun(string outcome)
    {
        _state.Enabled = false;
        _state.NextRunUtc = null;
        _state.LastOutcome = outcome;
        _state.LastReport = "Saatlik yerel mod güvenli durum hatası nedeniyle durduruldu.";
        SaveStateOrFailClosed();
    }

    private void SaveStateOrFailClosed()
    {
        try
        {
            string directory = Path.GetDirectoryName(_statePath)
                ?? throw new InvalidOperationException("Saatlik durum yolu geçersiz.");
            Directory.CreateDirectory(directory);
            string temporary = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllText(
                    temporary,
                    JsonSerializer.Serialize(_state, JsonOptions),
                    new UTF8Encoding(false));
                File.Move(temporary, _statePath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporary))
                {
                    File.Delete(temporary);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _state.Enabled = false;
            _state.NextRunUtc = null;
            _persistenceFault = "Saatlik durum kaydedilemedi; otomasyon kapalı. " + exception.GetType().Name;
        }
    }

    private void ThrowIfUnavailable()
    {
        if (_persistenceFault is not null)
        {
            throw new IOException(_persistenceFault);
        }
    }
}
