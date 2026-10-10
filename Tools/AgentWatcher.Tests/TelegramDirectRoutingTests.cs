using System.Text.Json;
using ArsanGazERP.AgentWatcher;

internal static class TelegramDirectRoutingTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Path.GetTempPath(), "ArsanGazERP.DirectTelegramTests", Guid.NewGuid().ToString("N"));
        string inboxDirectory = Path.Combine(root, "CopilotInbox");
        string outboxDirectory = Path.Combine(root, "TelegramOutbox");
        string erpQueueDirectory = Path.Combine(root, "Queue");
        Directory.CreateDirectory(erpQueueDirectory);

        try
        {
            TelegramCopilotInbox inbox = new(inboxDirectory, outboxDirectory);
            TelegramCopilotEnqueueResult later = await inbox.EnqueueAsync(
                105,
                "Daha sonraki istek",
                -100123,
                "Bağlam");
            TelegramCopilotEnqueueResult earlier = await inbox.EnqueueAsync(
                104,
                "Önceki istek",
                -100123,
                null);

            string[] requestFiles = Directory.GetFiles(inboxDirectory, "*.request.json")
                .OrderBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();
            Check(requestFiles.Length == 2, "Direct Telegram requests must appear in the VS Code inbox.");
            Check(
                Path.GetFileName(requestFiles[0]).StartsWith(earlier.Id, StringComparison.Ordinal)
                && Path.GetFileName(requestFiles[1]).StartsWith(later.Id, StringComparison.Ordinal),
                "Fixed-width Telegram update IDs must preserve arrival ordering in the extension's lexical queue.");
            Check(
                Directory.GetFiles(erpQueueDirectory, "*.task.json").Length == 0,
                "Direct Telegram requests must not create ERP task-queue files.");

            BridgeRequest? savedRequest = JsonSerializer.Deserialize<BridgeRequest>(
                await File.ReadAllTextAsync(Path.Combine(inboxDirectory, later.Id + ".request.json")),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            Check(
                savedRequest is { Id: var id, TaskId: var taskId, Text: "Daha sonraki istek", Context: "Bağlam" }
                && id == later.Id
                && taskId == later.Id,
                "The VS Code request must include the original Telegram command and its correlation IDs.");

            File.Delete(Path.Combine(inboxDirectory, later.Id + ".request.json"));
            TelegramCopilotEnqueueResult duplicate = await inbox.EnqueueAsync(
                105,
                "Daha sonraki istek",
                -100123,
                "Bağlam");
            Check(
                duplicate.AlreadyQueued
                && File.Exists(Path.Combine(inboxDirectory, later.Id + ".request.json"))
                && Directory.GetFiles(inboxDirectory, "*.request.json").Length == 2,
                "Duplicate Telegram updates must recover an interrupted write without creating duplicate requests.");

            string completionPath = Path.Combine(inboxDirectory, later.Id + ".completion.json");
            await File.WriteAllTextAsync(completionPath, JsonSerializer.Serialize(new BridgeCompletion
            {
                Id = later.Id,
                TaskId = later.Id,
                Status = "completed",
                Report = "Copilot yanıtı"
            }));
            Check(
                await inbox.ForwardCompletionsAsync() == 1,
                "A correlated VS Code completion must create one Telegram outbox report.");
            string outboxPath = Directory.GetFiles(outboxDirectory, "*.json").Single();
            using (JsonDocument outbox = JsonDocument.Parse(await File.ReadAllTextAsync(outboxPath)))
            {
                JsonElement item = outbox.RootElement;
                Check(
                    item.GetProperty("TaskId").GetString() == later.Id
                    && item.GetProperty("ChatId").GetInt64() == -100123
                    && item.GetProperty("Report").GetString() == "Copilot yanıtı"
                    && item.GetProperty("Kind").GetString() == TelegramCopilotInbox.DirectResponseKind,
                    "A direct Copilot completion must be correlated to the originating authorized Telegram chat.");
            }

            Check(
                !TelegramQueuePolicy.ShouldSuppressNonErrorNotice(TelegramCopilotInbox.DirectResponseKind)
                && await inbox.ForwardCompletionsAsync() == 0,
                "Direct Copilot reports must survive normal-notice suppression and must not be enqueued twice.");

            TelegramCopilotEnqueueResult failed = await inbox.EnqueueAsync(
                106,
                "İşlem başarısızlığı",
                -100123,
                null);
            await File.WriteAllTextAsync(
                Path.Combine(inboxDirectory, failed.Id + ".ack.json"),
                JsonSerializer.Serialize(new BridgeAcknowledgement
                {
                    Status = "failed",
                    Error = "Köprü isteği gönderemedi."
                }));
            Check(
                await inbox.ForwardCompletionsAsync() == 1,
                "A bridge delivery failure must be forwarded to Telegram instead of remaining invisible.");
            Check(
                Directory.GetFiles(outboxDirectory, "*.json").Length == 2,
                "Success and failure reports must each produce one durable Telegram outbox item.");
            Check(
                Directory.GetFiles(erpQueueDirectory, "*.task.json").Length == 0,
                "Completion forwarding must not create ERP task-queue files.");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
