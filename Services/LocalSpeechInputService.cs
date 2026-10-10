using System.IO;
using System.Threading.Channels;
using NAudio.Wave;
using Whisper.net;
using Whisper.net.Ggml;

namespace ArsanGazERP.Services;

public sealed class LocalSpeechInputService : IAsyncDisposable
{
    private const int SampleRate = 16_000;
    private const int SilenceSamplesToEndUtterance = SampleRate;
    private const int MaximumUtteranceSamples = SampleRate * 20;
    private const float SpeechThreshold = 0.012f;
    private const float SilenceThreshold = 0.008f;
    private const int PreRollFrames = 3;

    private readonly object _audioLock = new();
    private readonly Queue<float[]> _preRoll = new();
    private readonly List<float> _utterance = [];
    private readonly Channel<float[]> _utterances = Channel.CreateBounded<float[]>(
        new BoundedChannelOptions(4)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = true
        });
    private readonly CancellationTokenSource _shutdown = new();
    private WaveIn? _capture;
    private WhisperFactory? _factory;
    private WhisperProcessor? _processor;
    private Task? _transcriptionWorker;
    private TaskCompletionSource<bool>? _recordingStopped;
    private int _silenceSamples;
    private bool _speechStarted;
    private bool _isPaused;
    private bool _disposed;

    public event Action<string>? TranscriptReceived;
    public event Action<Exception>? Error;

    public bool IsListening => _capture is not null && !_isPaused;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_capture is not null) return;
        using CancellationTokenSource startupCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _shutdown.Token);
        CancellationToken startupToken = startupCancellation.Token;

        string modelDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP",
            "SpeechModels");
        Directory.CreateDirectory(modelDirectory);
        string modelPath = Path.Combine(modelDirectory, "ggml-tiny.bin");
        if (!File.Exists(modelPath) || new FileInfo(modelPath).Length < 50_000_000)
        {
            string temporaryPath = modelPath + ".download";
            try
            {
                using Stream model = await WhisperGgmlDownloader.Default.GetGgmlModelAsync(
                    GgmlType.Tiny,
                    cancellationToken: startupToken);
                await using FileStream target = new(
                    temporaryPath,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    81920,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                await model.CopyToAsync(target, startupToken);
                await target.FlushAsync(startupToken);
                if (target.Length < 50_000_000)
                {
                    throw new InvalidDataException("İndirilen konuşma modeli beklenenden küçük; dosya doğrulanamadı.");
                }

                await target.DisposeAsync();
                File.Move(temporaryPath, modelPath, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        startupToken.ThrowIfCancellationRequested();
        _factory = WhisperFactory.FromPath(modelPath);
        _processor = _factory.CreateBuilder()
            .WithLanguage("tr")
            .WithNoContext()
            .WithThreads(Math.Clamp(Environment.ProcessorCount / 2, 1, 4))
            .Build();
        WaveIn capture = new()
        {
            DeviceNumber = 0,
            WaveFormat = new WaveFormat(SampleRate, 16, 1),
            BufferMilliseconds = 100
        };
        capture.DataAvailable += Capture_DataAvailable;
        capture.RecordingStopped += Capture_RecordingStopped;
        try
        {
            capture.StartRecording();
            _capture = capture;
            _transcriptionWorker = Task.Run(() => ProcessUtterancesAsync(_shutdown.Token));
        }
        catch
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            capture.Dispose();
            await DisposeTranscriptionAsync();
            throw;
        }
    }

    public async Task StopAsync()
    {
        WaveIn? capture = _capture;
        _capture = null;
        _recordingStopped?.TrySetResult(true);
        _recordingStopped = null;
        _isPaused = false;
        if (capture is not null)
        {
            capture.DataAvailable -= Capture_DataAvailable;
            capture.RecordingStopped -= Capture_RecordingStopped;
            try
            {
                capture.StopRecording();
            }
            finally
            {
                capture.Dispose();
            }
        }

        lock (_audioLock)
        {
            _speechStarted = false;
            _silenceSamples = 0;
            _utterance.Clear();
            _preRoll.Clear();
        }

        _shutdown.Cancel();
        _utterances.Writer.TryComplete();
        await DisposeTranscriptionAsync();
    }

    public async Task PauseAsync()
    {
        WaveIn? capture = _capture;
        if (capture is null || _isPaused) return;

        TaskCompletionSource<bool> stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
        _recordingStopped = stopped;
        _isPaused = true;
        lock (_audioLock)
        {
            _speechStarted = false;
            _silenceSamples = 0;
            _utterance.Clear();
            _preRoll.Clear();
        }

        capture.StopRecording();
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
    }

    public void Resume()
    {
        WaveIn? capture = _capture;
        if (capture is null || !_isPaused) return;

        capture.StartRecording();
        _isPaused = false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopAsync();
        _shutdown.Dispose();
    }

    private void Capture_DataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (eventArgs.BytesRecorded < 2) return;

        int sampleCount = eventArgs.BytesRecorded / 2;
        float[] samples = new float[sampleCount];
        double squaredTotal = 0;
        for (int index = 0, sampleIndex = 0; index + 1 < eventArgs.BytesRecorded; index += 2, sampleIndex++)
        {
            float sample = BitConverter.ToInt16(eventArgs.Buffer, index) / 32768f;
            samples[sampleIndex] = sample;
            squaredTotal += sample * sample;
        }

        float rms = (float)Math.Sqrt(squaredTotal / sampleCount);
        lock (_audioLock)
        {
            if (_isPaused) return;

            if (!_speechStarted)
            {
                _preRoll.Enqueue(samples);
                while (_preRoll.Count > PreRollFrames) _preRoll.Dequeue();
                if (rms < SpeechThreshold) return;

                _speechStarted = true;
                foreach (float[] frame in _preRoll) _utterance.AddRange(frame);
                _preRoll.Clear();
                _silenceSamples = 0;
            }
            else
            {
                _utterance.AddRange(samples);
                _silenceSamples = rms < SilenceThreshold
                    ? _silenceSamples + sampleCount
                    : 0;
            }

            if (_silenceSamples >= SilenceSamplesToEndUtterance ||
                _utterance.Count >= MaximumUtteranceSamples)
            {
                QueueUtterance();
            }
        }
    }

    private void Capture_RecordingStopped(object? sender, StoppedEventArgs eventArgs)
    {
        if (eventArgs.Exception is null)
        {
            _recordingStopped?.TrySetResult(true);
        }
        else
        {
            _recordingStopped?.TrySetException(eventArgs.Exception);
        }

        _recordingStopped = null;
        if (eventArgs.Exception is not null) Error?.Invoke(eventArgs.Exception);
    }

    private void QueueUtterance()
    {
        if (_utterance.Count >= SampleRate / 3)
        {
            float[] samples = _utterance.ToArray();
            if (!_utterances.Writer.TryWrite(samples))
            {
                Error?.Invoke(new InvalidOperationException(
                    "Ses çözümleme kuyruğu dolu; yeni konuşma bu kez işlenmedi."));
            }
        }

        _utterance.Clear();
        _preRoll.Clear();
        _silenceSamples = 0;
        _speechStarted = false;
    }

    private async Task ProcessUtterancesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (float[] samples in _utterances.Reader.ReadAllAsync(cancellationToken))
            {
                if (_processor is null) return;
                if (!HasSpeechEnergy(samples)) continue;
                string text = string.Concat(
                    await _processor.ProcessAsync(samples, cancellationToken)
                        .Select(segment => segment.Text)
                        .ToListAsync(cancellationToken));
                text = text.Trim();
                if (text.Length > 0) TranscriptReceived?.Invoke(text);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            Error?.Invoke(exception);
        }
    }

    private static bool HasSpeechEnergy(float[] samples)
    {
        double squaredTotal = 0;
        foreach (float sample in samples) squaredTotal += sample * sample;
        return Math.Sqrt(squaredTotal / samples.Length) >= SilenceThreshold;
    }

    private async Task DisposeTranscriptionAsync()
    {
        if (_transcriptionWorker is not null)
        {
            await _transcriptionWorker;
            _transcriptionWorker = null;
        }

        _processor?.Dispose();
        _processor = null;
        _factory?.Dispose();
        _factory = null;
    }
}
