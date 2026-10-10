using System.Net;
using System.Text.Json;
using ArsanGazERP.AgentWatcher;

internal static class TelegramHttpDeadlineTests
{
    public static async Task RunAsync()
    {
        await AStalledSendTimesOutAndCanBeRetriedAsync();
        await LongPollGetsTimeToFinishAndShutdownCancelsPromptlyAsync();
        UploadDeadlineScalesWithFileSize();
        Console.WriteLine("PASS: Telegram HTTP deadlines, retry progress and shutdown cancellation (mock only).");
    }

    private static async Task AStalledSendTimesOutAndCanBeRetriedAsync()
    {
        using CancellationTokenSource callerCancellation = new();
        using StallingThenSuccessfulHandler handler = new();
        string offsetPath = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"), "telegram-offset.txt");
        using TelegramBotService telegram = new(
            "test-token", 4242, handler, offsetPath,
            requestTimeout: TimeSpan.FromMilliseconds(50),
            longPollTimeout: TimeSpan.FromMilliseconds(500));

        Task send = telegram.SendMessageWithReceiptAsync("deadline regression", callerCancellation.Token);
        Task firstRequest = await Task.WhenAny(handler.FirstRequestStarted.Task, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert(ReferenceEquals(firstRequest, handler.FirstRequestStarted.Task), "The mock request did not start.");

        try
        {
            await send;
            throw new InvalidOperationException("A stalled Telegram send should time out before it can return a response.");
        }
        catch (OperationCanceledException) when (!callerCancellation.IsCancellationRequested) { }
        Assert(handler.FirstRequestCancellationObserved.Task.IsCompleted,
            "The request deadline must cancel the stalled handler.");

        await telegram.SendMessageWithReceiptAsync("deadline regression", callerCancellation.Token);
        Assert(handler.RequestCount == 2, "A later send attempt must make progress after the timed-out request.");
    }

    private static void UploadDeadlineScalesWithFileSize()
    {
        Assert(TelegramBotService.GetUploadTimeout(200 * 1024) == TimeSpan.FromSeconds(60),
            "Small uploads must keep the 60 second minimum.");
        Assert(TelegramBotService.GetUploadTimeout(31_639_242) > TimeSpan.FromMinutes(8),
            "A 30 MB APK must get enough time on a slow uplink.");
        Assert(TelegramBotService.GetUploadTimeout(50L * 1024 * 1024) <= TimeSpan.FromMinutes(15),
            "Upload deadlines must stay bounded.");
    }

    private static async Task LongPollGetsTimeToFinishAndShutdownCancelsPromptlyAsync()
    {
        using CancellationTokenSource stop = new();
        using PollingHandler handler = new();
        string offsetPath = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"), "telegram-offset.txt");
        using TelegramBotService telegram = new(
            "test-token", 4242, handler, offsetPath,
            requestTimeout: TimeSpan.FromMilliseconds(40),
            longPollTimeout: TimeSpan.FromMilliseconds(500),
            retryDelay: static (_, token) => Task.Delay(TimeSpan.FromMilliseconds(1), token));

        Task run = telegram.RunAsync(
            (_, _, _, _) => Task.FromResult(string.Empty),
            (_, _, _, _) => Task.FromResult(string.Empty),
            _ => { },
            stop.Token);
        Task retriedPoll = await Task.WhenAny(handler.RetriedPollStarted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert(ReferenceEquals(retriedPoll, handler.RetriedPollStarted.Task),
            "A valid 25-second long poll must not use the shorter ordinary-request deadline.");

        stop.Cancel();
        Task completed = await Task.WhenAny(run, Task.Delay(TimeSpan.FromSeconds(1)));
        Assert(ReferenceEquals(completed, run), "Caller shutdown cancellation must stop the stalled poll promptly.");
        await run;
        Assert(handler.TimedOutPollCancellationObserved.Task.IsCompleted,
            "An expired getUpdates deadline must cancel its stalled request before retrying.");
        Assert(handler.StalledPollCancellationObserved.Task.IsCompleted,
            "Caller cancellation must be propagated to the active long poll.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class StallingThenSuccessfulHandler : HttpMessageHandler
    {
        private int _requestCount;
        public TaskCompletionSource FirstRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource FirstRequestCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int RequestCount => Volatile.Read(ref _requestCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requestCount) == 1)
            {
                FirstRequestStarted.TrySetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    FirstRequestCancellationObserved.TrySetResult();
                    throw;
                }
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "{\"ok\":true,\"result\":{\"message_id\":1,\"chat\":{\"id\":4242}}}")
            };
        }
    }

    private sealed class PollingHandler : HttpMessageHandler
    {
        private int _pollCount;
        public TaskCompletionSource RetriedPollStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource TimedOutPollCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StalledPollCancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using JsonDocument body = JsonDocument.Parse(
                await request.Content!.ReadAsStringAsync(cancellationToken));
            Assert(body.RootElement.GetProperty("timeout").GetInt32() == 25,
                "Telegram getUpdates must retain its 25-second server-side long poll.");

            int poll = Interlocked.Increment(ref _pollCount);
            if (poll == 1)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(120), cancellationToken);
                return JsonResponse("{\"ok\":true,\"result\":[]}");
            }

            if (poll == 2)
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
                catch (OperationCanceledException)
                {
                    TimedOutPollCancellationObserved.TrySetResult();
                    throw;
                }
            }

            RetriedPollStarted.TrySetResult();
            try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); }
            catch (OperationCanceledException)
            {
                StalledPollCancellationObserved.TrySetResult();
                throw;
            }

            throw new InvalidOperationException("An infinite poll unexpectedly completed.");
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json)
        };
    }
}
