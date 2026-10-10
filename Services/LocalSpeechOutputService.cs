using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace ArsanGazERP.Services;

public sealed class LocalSpeechOutputService : IAsyncDisposable
{
    private const int MaximumTextChunkLength = 800;
    private static readonly char[] WordSeparators = [' ', '\r', '\n', '\t'];
    private readonly SpeechSynthesizer _synthesizer;
    private readonly MediaPlayer _player = new();
    private readonly SemaphoreSlim _playbackGate = new(1, 1);
    private TaskCompletionSource<bool>? _playbackCompletion;
    private bool _disposed;

    private LocalSpeechOutputService(SpeechSynthesizer synthesizer, VoiceInformation voice)
    {
        _synthesizer = synthesizer;
        _synthesizer.Voice = voice;
        VoiceName = voice.DisplayName;
    }

    public string VoiceName { get; }

    public static LocalSpeechOutputService? TryCreate(out string status)
    {
        SpeechSynthesizer? synthesizer = null;
        try
        {
            synthesizer = new SpeechSynthesizer();
            VoiceInformation? voice = SpeechSynthesizer.AllVoices.FirstOrDefault(item =>
                item.Language.StartsWith("tr", StringComparison.OrdinalIgnoreCase));
            if (voice is null)
            {
                synthesizer.Dispose();
                status = "Windows'ta Türkçe metin okuma sesi yüklü değil.";
                return null;
            }

            status = $"Cihazdaki Türkçe ses hazır: {voice.DisplayName}.";
            return new LocalSpeechOutputService(synthesizer, voice);
        }
        catch (Exception exception)
        {
            synthesizer?.Dispose();
            status = "Windows Türkçe ses altyapısı kullanılamıyor: " +
                exception.GetBaseException().Message;
            return null;
        }
    }

    public async Task SpeakAsync(string text)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(text)) return;

        await _playbackGate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (string chunk in SplitText(text))
            {
                using SpeechSynthesisStream speech = await _synthesizer.SynthesizeTextToStreamAsync(chunk);
                TaskCompletionSource<bool> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
                _playbackCompletion = completion;
                TypedEventHandler<MediaPlayer, object> endedHandler = (_, _) => completion.TrySetResult(true);
                TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> failedHandler = (_, args) =>
                    completion.TrySetException(new InvalidOperationException(args.ErrorMessage));
                _player.MediaEnded += endedHandler;
                _player.MediaFailed += failedHandler;
                try
                {
                    _player.Source = MediaSource.CreateFromStream(speech, speech.ContentType);
                    _player.Play();
                    await completion.Task.WaitAsync(TimeSpan.FromSeconds(120));
                }
                finally
                {
                    _player.MediaEnded -= endedHandler;
                    _player.MediaFailed -= failedHandler;
                    _player.Pause();
                    _player.Source = null;
                    _playbackCompletion = null;
                }
            }
        }
        finally
        {
            _playbackGate.Release();
        }
    }

    public void Stop()
    {
        _playbackCompletion?.TrySetCanceled();
        _player.Pause();
    }

    private static IEnumerable<string> SplitText(string text)
    {
        string remaining = text.Trim();
        while (remaining.Length > MaximumTextChunkLength)
        {
            int splitAt = remaining.LastIndexOfAny(
                WordSeparators,
                MaximumTextChunkLength - 1,
                MaximumTextChunkLength);
            if (splitAt <= 0) splitAt = MaximumTextChunkLength;

            yield return remaining[..splitAt].Trim();
            remaining = remaining[splitAt..].TrimStart();
        }

        if (remaining.Length > 0) yield return remaining;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        Stop();
        await _playbackGate.WaitAsync();
        try
        {
            _player.Source = null;
            _player.Dispose();
            _synthesizer.Dispose();
        }
        finally
        {
            _playbackGate.Release();
        }
    }
}
