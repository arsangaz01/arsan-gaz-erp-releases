using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace ArsanGazERP.Services;

public sealed record PiperSettings(bool Enabled, string Voice);

/// <summary>Free offline Turkish text-to-speech using the Piper engine in %LOCALAPPDATA%\ArsanGazERP\Piper.</summary>
public sealed class PiperSpeechService : IAsyncDisposable
{
    public static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Piper");
    private static readonly string ExecutablePath = Path.Combine(Folder, "piper", "piper.exe");
    private static readonly string SettingsPath = Path.Combine(Folder, "settings.json");
    private const string DefaultVoice = "tr_TR-fahrettin-medium";

    private readonly MediaPlayer _player = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TaskCompletionSource<bool>? _playback;
    private Process? _process;
    private bool _disposed;

    public static bool IsInstalled => File.Exists(ExecutablePath) && Voices().Count > 0;

    public static bool IsActive => IsInstalled && LoadSettings().Enabled;

    public static IReadOnlyList<string> Voices() =>
        Directory.Exists(Folder)
            ? Directory.EnumerateFiles(Folder, "*.onnx")
                .Where(path => File.Exists(path + ".json"))
                .Select(Path.GetFileNameWithoutExtension)
                .OfType<string>()
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : [];

    public static PiperSettings LoadSettings()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(SettingsPath));
                bool enabled = !document.RootElement.TryGetProperty("enabled", out JsonElement e) || e.ValueKind != JsonValueKind.False;
                string voice = document.RootElement.TryGetProperty("voice", out JsonElement v) ? v.GetString() ?? DefaultVoice : DefaultVoice;
                return new PiperSettings(enabled, voice);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
        }

        return new PiperSettings(true, DefaultVoice);
    }

    public static void SaveSettings(bool enabled, string voice)
    {
        Directory.CreateDirectory(Folder);
        File.WriteAllText(SettingsPath, JsonSerializer.Serialize(new { enabled, voice }), new UTF8Encoding(false));
    }

    public async Task SpeakAsync(string text, string? voice = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string spoken = ElevenLabsSpeechService.LimitLength(text);
        if (spoken.Length == 0) return;
        string selected = ResolveVoice(voice ?? LoadSettings().Voice);
        string output = Path.Combine(Path.GetTempPath(), $"arsangaz-piper-{Environment.ProcessId}-{Guid.NewGuid():N}.wav");

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await SynthesizeAsync(spoken, selected, output, cancellationToken);
            await PlayAsync(output);
        }
        finally
        {
            _gate.Release();
            try { File.Delete(output); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    public void Stop()
    {
        _playback?.TrySetCanceled();
        _player.Pause();
        try { if (_process is { HasExited: false }) _process.Kill(); } catch (InvalidOperationException) { }
    }

    private static string ResolveVoice(string voice)
    {
        IReadOnlyList<string> voices = Voices();
        if (voices.Count == 0) throw new InvalidOperationException("Piper Türkçe ses dosyası bulunamadı: " + Folder);
        return voices.FirstOrDefault(item => string.Equals(item, voice, StringComparison.OrdinalIgnoreCase)) ?? voices[0];
    }

    private async Task SynthesizeAsync(string text, string voice, string output, CancellationToken cancellationToken)
    {
        if (!File.Exists(ExecutablePath)) throw new InvalidOperationException("Piper kurulu değil: " + ExecutablePath);
        ProcessStartInfo start = new(ExecutablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath)!,
        };
        start.ArgumentList.Add("--model");
        start.ArgumentList.Add(Path.Combine(Folder, voice + ".onnx"));
        start.ArgumentList.Add("--output_file");
        start.ArgumentList.Add(output);

        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Piper başlatılamadı.");
        _process = process;
        try
        {
            Task<string> errors = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.StandardInput.WriteLineAsync(text.Replace('\r', ' ').Replace('\n', ' '));
            process.StandardInput.Close();
            using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            await process.WaitForExitAsync(timeout.Token);
            if (process.ExitCode != 0 || !File.Exists(output))
            {
                string detail = await errors;
                throw new InvalidOperationException($"Piper ses üretemedi (çıkış {process.ExitCode}): {detail[..Math.Min(detail.Length, 200)]}");
            }
        }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(); } catch (InvalidOperationException) { }
            throw;
        }
        finally
        {
            _process = null;
        }
    }

    private async Task PlayAsync(string path)
    {
        StorageFile file = await StorageFile.GetFileFromPathAsync(path);
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _playback = completion;
        TypedEventHandler<MediaPlayer, object> ended = (_, _) => completion.TrySetResult(true);
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, args) =>
            completion.TrySetException(new InvalidOperationException(args.ErrorMessage));
        _player.MediaEnded += ended;
        _player.MediaFailed += failed;
        try
        {
            _player.Source = MediaSource.CreateFromStorageFile(file);
            _player.Play();
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(180));
        }
        finally
        {
            _player.MediaEnded -= ended;
            _player.MediaFailed -= failed;
            _player.Pause();
            _player.Source = null;
            _playback = null;
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        Stop();
        _player.Source = null;
        _player.Dispose();
        return ValueTask.CompletedTask;
    }
}
