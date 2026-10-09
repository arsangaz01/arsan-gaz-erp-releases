using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace ArsanGazERP.Services;

public sealed record ElevenLabsVoice(string Id, string Name);

public sealed record ElevenLabsSettings(bool Enabled, string VoiceId, bool HasApiKey);

public static class ElevenLabsSettingsStore
{
    private static readonly object Sync = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArsanGazERP.ElevenLabs.Secret.v1");
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "ElevenLabs");
    private static readonly string SettingsPath = Path.Combine(Folder, "settings.json");
    private static readonly string KeyPath = Path.Combine(Folder, "api-key.bin");

    public static ElevenLabsSettings Load()
    {
        lock (Sync)
        {
            Persisted persisted = new();
            if (File.Exists(SettingsPath))
            {
                try
                {
                    persisted = JsonSerializer.Deserialize<Persisted>(File.ReadAllText(SettingsPath)) ?? new Persisted();
                }
                catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
                {
                    persisted = new Persisted();
                }
            }

            return new ElevenLabsSettings(persisted.Enabled, persisted.VoiceId, File.Exists(KeyPath));
        }
    }

    public static bool IsActive
    {
        get
        {
            ElevenLabsSettings settings = Load();
            return settings.Enabled && settings.HasApiKey && settings.VoiceId.Length > 0;
        }
    }

    public static void Save(bool enabled, string voiceId, string? apiKey)
    {
        string normalizedVoice = voiceId.Trim();
        if (normalizedVoice.Length > 0 && !normalizedVoice.All(char.IsAsciiLetterOrDigit))
        {
            throw new ArgumentException("Voice ID yalnızca harf ve rakamlardan oluşmalıdır.", nameof(voiceId));
        }

        lock (Sync)
        {
            Directory.CreateDirectory(Folder);
            if (!string.IsNullOrWhiteSpace(apiKey))
            {
                WriteAtomically(KeyPath, ProtectedData.Protect(
                    Encoding.UTF8.GetBytes(apiKey.Trim()), Entropy, DataProtectionScope.CurrentUser));
            }

            WriteAtomically(SettingsPath, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
                new Persisted(enabled, normalizedVoice),
                new JsonSerializerOptions { WriteIndented = true })));
        }
    }

    public static void Remove()
    {
        lock (Sync)
        {
            if (File.Exists(KeyPath)) File.Delete(KeyPath);
            if (File.Exists(SettingsPath)) File.Delete(SettingsPath);
        }
    }

    internal static string GetApiKey()
    {
        lock (Sync)
        {
            if (!File.Exists(KeyPath))
            {
                throw new InvalidOperationException("ElevenLabs API anahtarı kaydedilmemiş.");
            }

            try
            {
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(
                    File.ReadAllBytes(KeyPath), Entropy, DataProtectionScope.CurrentUser));
            }
            catch (Exception exception) when (exception is IOException or CryptographicException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException("ElevenLabs API anahtarı bu Windows hesabında açılamadı; yeniden kaydedin.", exception);
            }
        }
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        string temporary = path + ".tmp";
        File.WriteAllBytes(temporary, contents);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed record Persisted(bool Enabled = false, string VoiceId = "");
}

public sealed class ElevenLabsSpeechService : IAsyncDisposable
{
    public const int MaximumSpokenCharacters = 900;
    private const string ModelId = "eleven_multilingual_v2";
    private static readonly HttpClient Http = new()
    {
        BaseAddress = new Uri("https://api.elevenlabs.io/"),
        Timeout = TimeSpan.FromSeconds(45)
    };
    private readonly MediaPlayer _player = new();
    private TaskCompletionSource<bool>? _playback;
    private bool _disposed;

    public static async Task<IReadOnlyList<ElevenLabsVoice>> ListVoicesAsync(string apiKey, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Get, "v1/voices");
        request.Headers.Add("xi-api-key", apiKey.Trim());
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        string payload = await response.Content.ReadAsStringAsync(cancellationToken);
        EnsureSuccess(response, payload);
        using JsonDocument document = JsonDocument.Parse(payload);
        return document.RootElement.GetProperty("voices").EnumerateArray()
            .Select(voice => new ElevenLabsVoice(
                voice.GetProperty("voice_id").GetString() ?? string.Empty,
                voice.TryGetProperty("name", out JsonElement name) ? name.GetString() ?? "?" : "?"))
            .Where(voice => voice.Id.Length > 0)
            .OrderBy(voice => voice.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public async Task SpeakAsync(string text, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ElevenLabsSettings settings = ElevenLabsSettingsStore.Load();
        await SpeakAsync(text, settings.VoiceId, ElevenLabsSettingsStore.GetApiKey(), cancellationToken);
    }

    public async Task SpeakAsync(string text, string voiceId, string apiKey, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        string spoken = LimitLength(text);
        if (spoken.Length == 0) return;

        using HttpRequestMessage request = new(
            HttpMethod.Post,
            $"v1/text-to-speech/{Uri.EscapeDataString(voiceId.Trim())}?output_format=mp3_44100_128");
        request.Headers.Add("xi-api-key", apiKey.Trim());
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("audio/mpeg"));
        request.Content = JsonContent.Create(new { text = spoken, model_id = ModelId });
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            EnsureSuccess(response, await response.Content.ReadAsStringAsync(cancellationToken));
        }

        byte[] audio = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        await PlayAsync(audio);
    }

    public void Stop()
    {
        _playback?.TrySetCanceled();
        _player.Pause();
    }

    internal static string LimitLength(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.Length <= MaximumSpokenCharacters) return trimmed;
        int cut = trimmed.LastIndexOfAny(['.', '!', '?', '\n'], MaximumSpokenCharacters - 1);
        if (cut < MaximumSpokenCharacters / 2) cut = MaximumSpokenCharacters;
        return trimmed[..cut].TrimEnd() + " Devamı ekranda.";
    }

    private async Task PlayAsync(byte[] audio)
    {
        using MemoryStream memory = new(audio, writable: false);
        using Windows.Storage.Streams.IRandomAccessStream stream = memory.AsRandomAccessStream();
        TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _playback = completion;
        TypedEventHandler<MediaPlayer, object> ended = (_, _) => completion.TrySetResult(true);
        TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failed = (_, args) =>
            completion.TrySetException(new InvalidOperationException(args.ErrorMessage));
        _player.MediaEnded += ended;
        _player.MediaFailed += failed;
        try
        {
            _player.Source = MediaSource.CreateFromStream(stream, "audio/mpeg");
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

    private static void EnsureSuccess(HttpResponseMessage response, string payload)
    {
        if (response.IsSuccessStatusCode) return;
        string detail = payload.Length > 300 ? payload[..300] : payload;
        throw new InvalidOperationException((int)response.StatusCode switch
        {
            401 => "ElevenLabs API anahtarı geçersiz (401).",
            402 or 429 => "ElevenLabs kotası/limiti doldu (" + (int)response.StatusCode + "). Windows sesi kullanılacak.",
            _ => $"ElevenLabs HTTP {(int)response.StatusCode}: {detail}"
        });
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
