using GitHub.Copilot;
using System.IO;

namespace ArsanGazERP.Services;

public sealed record CopilotChatMessage(string Role, string Content);
public sealed record CopilotModelOptions(string? Model = null, string? ReasoningEffort = null);

public sealed class CopilotAgentRuntime : IAsyncDisposable
{
    private static readonly SemaphoreSlim RequestGate = new(2, 2);
    private readonly CopilotClient _client;
    private readonly string _workingDirectory;
    private readonly object _startLock = new();
    private Task? _startTask;

    public CopilotAgentRuntime()
    {
        _workingDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ArsanGazERP",
            "CopilotRuntime");
        Directory.CreateDirectory(_workingDirectory);
        _client = new CopilotClient(new CopilotClientOptions
        {
            UseLoggedInUser = true,
            LogLevel = CopilotLogLevel.Error,
            WorkingDirectory = _workingDirectory
        });
    }

    public async Task<string> CompleteAsync(
        string systemPrompt,
        IReadOnlyList<CopilotChatMessage> messages,
        CancellationToken cancellationToken = default,
        CopilotModelOptions? modelOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(systemPrompt);
        ArgumentNullException.ThrowIfNull(messages);

        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(65));
        await RequestGate.WaitAsync(timeout.Token);
        try
        {
            await EnsureStartedAsync(timeout.Token);
            await using CopilotSession session = await _client.CreateSessionAsync(new SessionConfig
            {
                Model = modelOptions?.Model,
                ReasoningEffort = modelOptions?.ReasoningEffort,
                SystemMessage = new SystemMessageConfig
                {
                    Mode = SystemMessageMode.Append,
                    Content = systemPrompt
                },
                AvailableTools = Array.Empty<string>(),
                EnableSessionStore = false,
                InfiniteSessions = new InfiniteSessionConfig { Enabled = false },
                Streaming = false
            });

            AssistantMessageEvent? response = await session.SendAndWaitAsync(new MessageOptions
            {
                Prompt = FormatPrompt(messages)
            }).WaitAsync(timeout.Token);

            string answer = response?.Data.Content?.Trim() ?? string.Empty;
            if (answer.Length == 0)
            {
                throw new InvalidOperationException("GitHub Copilot boş yanıt döndürdü.");
            }

            return answer;
        }
        finally
        {
            RequestGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _client.DisposeAsync();
    }

    private Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        Task startTask;
        lock (_startLock)
        {
            _startTask ??= _client.StartAsync();
            startTask = _startTask;
        }

        return startTask.WaitAsync(cancellationToken);
    }

    private static string FormatPrompt(IReadOnlyList<CopilotChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            throw new ArgumentException("Copilot isteği en az bir mesaj içermelidir.", nameof(messages));
        }

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            messages.TakeLast(13).Select(message =>
                $"{(message.Role == "assistant" ? "Önceki ajan yanıtı" : "Kullanıcı mesajı")}:\n{message.Content}"));
    }
}
