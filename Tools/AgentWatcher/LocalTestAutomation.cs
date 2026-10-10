using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed record LocalTestAutomationStatus(
    bool Enabled,
    DateTimeOffset? WindowStartedUtc,
    int RunsStarted,
    DateTimeOffset? LastRunStartedUtc,
    string LastOutcome,
    string Report);

internal sealed class LocalTestAutomation : IDisposable
{
    internal const int MaximumRuns = 7;
    internal static readonly TimeSpan MaximumWindow = TimeSpan.FromDays(7);
    internal static readonly TimeSpan MaximumRunDuration = TimeSpan.FromMinutes(20);
    private const int MaximumReportCharacters = 16_384;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] SensitiveLabels =
        ["api_key", "api-key", "apikey", "access_token", "access-token", "password", "secret", "authorization"];

    private readonly string _statePath;
    private readonly string _workspaceRoot;
    private readonly Func<CancellationToken, Task<string>> _runTests;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _maximumRunTime;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private readonly object _sync = new();
    private PersistedState _state;
    private CancellationTokenSource? _activeRun;
    private string? _persistenceFault;
    private bool _disposed;

    internal LocalTestAutomation(string statePath, string workspaceRoot)
        : this(statePath, workspaceRoot, token => RunFixedTestSuiteAsync(workspaceRoot, token), null, null)
    {
    }

    internal LocalTestAutomation(
        string statePath,
        string workspaceRoot,
        Func<CancellationToken, Task<string>> runTests,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? maximumRunTime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentNullException.ThrowIfNull(runTests);
        _statePath = Path.GetFullPath(statePath);
        _workspaceRoot = Path.GetFullPath(workspaceRoot);
        _runTests = runTests;
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _maximumRunTime = maximumRunTime ?? MaximumRunDuration;
        if (_maximumRunTime <= TimeSpan.Zero || _maximumRunTime > MaximumRunDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumRunTime),
                $"Test çalışması 0 ile {MaximumRunDuration.TotalMinutes:N0} dakika arasında sınırlandırılmalıdır.");
        }

        _state = LoadState();
    }

    internal LocalTestAutomationStatus Status
    {
        get
        {
            lock (_sync)
            {
                if (_persistenceFault is not null)
                {
                    return Snapshot(
                        enabled: false,
                        outcome: "Unavailable",
                        report: "Yerel otomasyon durum dosyası okunamadı veya geçersiz; dosya korunarak otomasyon kapalı tutuldu. "
                            + _persistenceFault);
                }

                return Snapshot();
            }
        }
    }

    internal bool IsDue
    {
        get
        {
            lock (_sync)
            {
                if (_persistenceFault is not null || !_state.Enabled || _activeRun is not null || _disposed)
                {
                    return false;
                }

                DateTimeOffset now = _clock().ToUniversalTime();
                if (_state.WindowStartedUtc is not { } started)
                {
                    return true;
                }

                return now >= started.Add(MaximumWindow) ||
                    _state.RunsStarted >= MaximumRuns ||
                    _state.LastRunStartedUtc is null ||
                    _state.LastRunStartedUtc.Value.UtcDateTime.Date < now.UtcDateTime.Date;
            }
        }
    }

    internal void EnableNewTrial()
    {
        lock (_sync)
        {
            ThrowIfUnavailable();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activeRun is not null)
            {
                throw new InvalidOperationException("Çalışan test iptal edilmeden yeni bir deneme başlatılamaz.");
            }

            DateTimeOffset now = _clock().ToUniversalTime();
            _state = new PersistedState
            {
                Enabled = true,
                WindowStartedUtc = now,
                Report = "Yerel test otomasyonu etkinleştirildi; ilk uygun denetim AgentWatcher zamanlayıcısında başlar."
            };
            SaveState();
        }
    }

    internal void Stop()
    {
        CancellationTokenSource? active;
        lock (_sync)
        {
            if (_persistenceFault is not null)
            {
                return;
            }

            _state.Enabled = false;
            _state.LastOutcome = _activeRun is null ? "Stopped" : "Stopping";
            _state.Report = _activeRun is null
                ? "Yerel test otomasyonu kullanıcı tarafından durduruldu."
                : "Durdurma istendi; çalışan yerel test süreci iptal ediliyor.";
            active = _activeRun;
            TrySaveState();
        }

        try
        {
            if (new FileInfo(_statePath).Length > MaximumReportCharacters * 2)
            {
                throw new InvalidDataException("Yerel otomasyon durum dosyası izin verilen boyutu aşıyor.");
            }

            active?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    internal async Task TickAsync(CancellationToken cancellationToken)
    {
        bool acquired;
        try
        {
            acquired = await _runGate.WaitAsync(0, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (!acquired)
        {
            return;
        }

        CancellationTokenSource? runCancellation = null;
        try
        {
            lock (_sync)
            {
                if (_disposed || _persistenceFault is not null || !_state.Enabled)
                {
                    return;
                }

                DateTimeOffset now = _clock().ToUniversalTime();
                if (_state.WindowStartedUtc is not { } started || now >= started.Add(MaximumWindow))
                {
                    DisableAtLimit("Yedi günlük deneme süresi sona erdi; otomasyon kapatıldı.");
                    return;
                }

                if (_state.RunsStarted >= MaximumRuns)
                {
                    DisableAtLimit("Yedi yerel test çalışması tamamlandı; otomasyon kapatıldı.");
                    return;
                }

                if (_state.LastRunStartedUtc is { } lastRun &&
                    lastRun.UtcDateTime.Date >= now.UtcDateTime.Date)
                {
                    return;
                }

                _state.RunsStarted++;
                _state.LastRunStartedUtc = now;
                _state.LastOutcome = "Running";
                _state.Report = $"Yerel sentetik testler başlatıldı: {now:O}; çalışma {_state.RunsStarted}/{MaximumRuns}.";
                runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                runCancellation.CancelAfter(_maximumRunTime);
                _activeRun = runCancellation;
                try
                {
                    SaveState();
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    _state.Enabled = false;
                    _persistenceFault = "Çalışma başlamadan önce durum kaydedilemedi; test başlatılmadı. " + exception.Message;
                    _activeRun = null;
                    runCancellation.Dispose();
                    runCancellation = null;
                    return;
                }
            }

            string output = await _runTests(runCancellation!.Token);
            FinishRun("Passed", "Yerel test paketi başarıyla tamamlandı.", output);
        }
        catch (OperationCanceledException)
        {
            bool explicitlyStopped;
            lock (_sync)
            {
                explicitlyStopped = !_state.Enabled || cancellationToken.IsCancellationRequested;
            }

            FinishRun(
                explicitlyStopped ? "Stopped" : "TimedOut",
                explicitlyStopped
                    ? "Yerel test çalışması durduruldu; sonraki uygun gün ayrıca denetlenebilir."
                    : $"Yerel test çalışması {MaximumRunDuration.TotalMinutes:N0} dakikalık zaman sınırını aştı ve durduruldu.");
        }
        catch (Exception exception)
        {
            FinishRun(
                "Failed",
                "Yerel test çalışması başarısız oldu: " + exception.GetBaseException().Message);
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

    internal static ProcessStartInfo BuildTestStartInfo(string workspaceRoot, string dotnetExecutable)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(dotnetExecutable);

        string root = Path.GetFullPath(workspaceRoot);
        string project = Path.GetFullPath(Path.Combine(
            root,
            "Tools",
            "AgentWatcher.Tests",
            "ArsanGazERP.AgentWatcher.Tests.csproj"));
        string relativeProject = Path.GetRelativePath(root, project);
        if (Path.IsPathRooted(relativeProject) ||
            relativeProject == ".." ||
            relativeProject.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            !File.Exists(project))
        {
            throw new InvalidOperationException("İzinli yerel test projesi workspace içinde bulunamadı.");
        }

        ProcessStartInfo startInfo = new(Path.GetFullPath(dotnetExecutable))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("--project");
        startInfo.ArgumentList.Add(project);
        startInfo.ArgumentList.Add("--no-restore");
        startInfo.ArgumentList.Add("--configuration");
        startInfo.ArgumentList.Add("Release");
        startInfo.ArgumentList.Add("--no-launch-profile");
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";
        startInfo.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
        foreach (string key in startInfo.Environment.Keys.ToArray())
        {
            if (key.Contains("API_KEY", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("ACCESS_KEY", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("TOKEN", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("SECRET", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("PASSWORD", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("CREDENTIAL", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("CONNECTION", StringComparison.OrdinalIgnoreCase) ||
            key.Contains("PRIVATE_KEY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("HTTP_PROXY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("HTTPS_PROXY", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("ALL_PROXY", StringComparison.OrdinalIgnoreCase))
            {
                startInfo.Environment.Remove(key);
            }
        }

        return startInfo;
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

    private PersistedState LoadState()
    {
        if (!File.Exists(_statePath))
        {
            return new PersistedState();
        }

        try
        {
            PersistedState state = JsonSerializer.Deserialize<PersistedState>(
                File.ReadAllText(_statePath),
                JsonOptions)
                ?? throw new InvalidDataException("Durum kaydı null.");
            ValidateState(state);
            return state;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            _persistenceFault = "Durum dosyası otomatik olarak sıfırlanmadı: " + exception.Message;
            return new PersistedState();
        }
    }

    private static void ValidateState(PersistedState state)
    {
        if (state.RunsStarted is < 0 or > MaximumRuns ||
            state.WindowStartedUtc is { } windowStarted && windowStarted.Offset != TimeSpan.Zero ||
            state.LastRunStartedUtc is { } lastRunStarted && lastRunStarted.Offset != TimeSpan.Zero ||
            (state.RunsStarted == 0) != (state.LastRunStartedUtc is null) ||
            string.IsNullOrWhiteSpace(state.LastOutcome) ||
            state.Report is null ||
            state.Report.Length > MaximumReportCharacters + 128 ||
            state.Enabled && state.WindowStartedUtc is null ||
            state.LastRunStartedUtc is { } lastRun &&
                state.WindowStartedUtc is { } started &&
                lastRun < started)
        {
            throw new InvalidDataException("Yerel otomasyon deneme durumu veya sınırları geçersiz.");
        }
    }

    private LocalTestAutomationStatus Snapshot(
        bool? enabled = null,
        string? outcome = null,
        string? report = null) =>
        new(
            enabled ?? _state.Enabled,
            _state.WindowStartedUtc,
            _state.RunsStarted,
            _state.LastRunStartedUtc,
            outcome ?? _state.LastOutcome,
            report ?? _state.Report);

    private void ThrowIfUnavailable()
    {
        if (_persistenceFault is not null)
        {
            throw new IOException(_persistenceFault);
        }
    }

    private void DisableAtLimit(string report)
    {
        _state.Enabled = false;
        _state.LastOutcome = "LimitReached";
        _state.Report = report;
        TrySaveState();
    }

    private void FinishRun(string outcome, string summary, string? output = null)
    {
        lock (_sync)
        {
            _state.LastOutcome = outcome;
            string report = summary;
            if (!string.IsNullOrWhiteSpace(output))
            {
                report += Environment.NewLine + Environment.NewLine + "Yerel test çıktısı:" + Environment.NewLine + output;
            }

            _state.Report = Sanitize(report);
            if (_state.RunsStarted >= MaximumRuns)
            {
                _state.Enabled = false;
                _state.Report += Environment.NewLine + "Yedi çalışma sınırı doldu; otomasyon kapatıldı.";
            }

            TrySaveState();
        }
    }

    private void SaveState()
    {
        string directory = Path.GetDirectoryName(_statePath)
            ?? throw new InvalidOperationException("Otomasyon durum yolu geçersiz.");
        Directory.CreateDirectory(directory);
        string temporaryPath = _statePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(_state, JsonOptions),
                new UTF8Encoding(false));
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private void TrySaveState()
    {
        try
        {
            SaveState();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _state.Enabled = false;
            _persistenceFault = "Durum kaydedilemedi; otomasyon kapalı tutuldu. " + exception.Message;
        }
    }

    private static string Sanitize(string text)
    {
        string redacted = RedactAssignments(text);
        redacted = RedactBearerTokens(redacted);
        return redacted.Length <= MaximumReportCharacters
            ? redacted
            : redacted[..MaximumReportCharacters] + Environment.NewLine + "[Çıktı sınır nedeniyle kısaltıldı.]";
    }

    private static string RedactAssignments(string text)
    {
        foreach (string label in SensitiveLabels)
        {
            int searchFrom = 0;
            while (searchFrom < text.Length)
            {
                int index = text.IndexOf(label, searchFrom, StringComparison.OrdinalIgnoreCase);
                if (index < 0)
                {
                    break;
                }

                int separator = index + label.Length;
                while (separator < text.Length && char.IsWhiteSpace(text[separator]))
                {
                    separator++;
                }

                if (separator >= text.Length || text[separator] is not ':' and not '=')
                {
                    searchFrom = separator;
                    continue;
                }

                int valueStart = separator + 1;
                while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
                {
                    valueStart++;
                }

                int valueEnd = valueStart;
                while (valueEnd < text.Length &&
                       !char.IsWhiteSpace(text[valueEnd]) &&
                       text[valueEnd] is not ',' and not ';')
                {
                    valueEnd++;
                }

                if (valueEnd == valueStart)
                {
                    searchFrom = valueStart;
                    continue;
                }

                const string replacement = "=[REDACTED]";
                text = text[..index] + label + replacement + text[valueEnd..];
                searchFrom = index + label.Length + replacement.Length;
            }
        }

        return text;
    }

    private static string RedactBearerTokens(string text)
    {
        const string label = "Bearer";
        int searchFrom = 0;
        while (searchFrom < text.Length)
        {
            int index = text.IndexOf(label, searchFrom, StringComparison.OrdinalIgnoreCase);
            if (index < 0)
            {
                break;
            }

            int valueStart = index + label.Length;
            if (index > 0 && char.IsLetterOrDigit(text[index - 1]))
            {
                searchFrom = valueStart;
                continue;
            }

            while (valueStart < text.Length && char.IsWhiteSpace(text[valueStart]))
            {
                valueStart++;
            }

            int valueEnd = valueStart;
            while (valueEnd < text.Length &&
                   !char.IsWhiteSpace(text[valueEnd]) &&
                   text[valueEnd] is not ',' and not ';')
            {
                valueEnd++;
            }

            if (valueEnd == valueStart)
            {
                searchFrom = valueStart;
                continue;
            }

            const string replacement = "Bearer [REDACTED]";
            text = text[..index] + replacement + text[valueEnd..];
            searchFrom = index + replacement.Length;
        }

        return text;
    }

    internal static async Task<string> RunFixedTestSuiteAsync(string workspaceRoot, CancellationToken cancellationToken)
    {
        string dotnet = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            "dotnet",
            "dotnet.exe");
        if (!File.Exists(dotnet))
        {
            throw new FileNotFoundException(
                "Program Files altında doğrulanmış dotnet.exe bulunamadı; PATH üzerinden başka çalıştırıcı denenmedi.",
                dotnet);
        }

        using Process process = new()
        {
            StartInfo = BuildTestStartInfo(workspaceRoot, dotnet),
            EnableRaisingEvents = true
        };
        cancellationToken.ThrowIfCancellationRequested();
        if (!process.Start())
        {
            throw new InvalidOperationException("Yerel test süreci başlatılamadı.");
        }

        Task<string> standardOutput = ReadCappedAsync(process.StandardOutput);
        Task<string> standardError = ReadCappedAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        string output = JoinOutput(await standardOutput, await standardError);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Sabit yerel test paketi çıkış kodu {process.ExitCode} ile başarısız oldu.{Environment.NewLine}{output}");
        }

        return output;
    }

    private static async Task<string> ReadCappedAsync(StreamReader reader)
    {
        char[] buffer = new char[2048];
        StringBuilder output = new();
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory())) > 0)
        {
            int remaining = MaximumReportCharacters - output.Length;
            if (remaining > 0)
            {
                output.Append(buffer, 0, Math.Min(read, remaining));
            }
        }

        return output.ToString();
    }

    private static string JoinOutput(string standardOutput, string standardError) =>
        string.IsNullOrWhiteSpace(standardError)
            ? standardOutput
            : standardOutput + Environment.NewLine + "[stderr]" + Environment.NewLine + standardError;

    private sealed class PersistedState
    {
        public bool Enabled { get; set; }
        public DateTimeOffset? WindowStartedUtc { get; set; }
        public int RunsStarted { get; set; }
        public DateTimeOffset? LastRunStartedUtc { get; set; }
        public string LastOutcome { get; set; } = "Never";
        public string Report { get; set; } = "Kapalı; yalnızca sentetik yerel testler için kullanıcı onayı bekleniyor.";
    }
}
