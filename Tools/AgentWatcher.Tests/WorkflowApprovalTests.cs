using ArsanGazERP.AgentWatcher;
using System.Text.Json;

internal static class WorkflowApprovalTests
{
    public static async Task RunAsync()
    {
        WorkflowActivationFixture.Check(WorkflowQueueApproval.ParseLocalCommand([]) is null,
            "Ordinary startup must not become local approval.");
        WorkflowActivationFixture.Check(WorkflowQueueApproval.ParseLocalCommand(["--background"]) is null,
            "Background mode must not manufacture approval.");
        WorkflowActivationFixture.Check(
            WorkflowQueueApproval.ParseLocalCommand(["--approve-workflow", "workflow-1"]) == "workflow-1",
            "The explicit command must select exactly one workflow.");
        await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() => Task.FromResult(
            WorkflowQueueApproval.ParseLocalCommand(["--approve-workflow", "workflow-1", "workflow-2"])));
        await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() => Task.FromResult(
            WorkflowQueueApproval.ParseLocalCommand(["--approve-workflow", "..\\settings"])));

        using (WorkflowActivationFixture f = new())
        {
            await f.WriteReviewAsync();
            WorkflowApprovalResult result = await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId);
            TaskItem queued = await f.ReadAsync(".task.json");
            WorkflowJob queuedWorkflow = queued.Workflow
                ?? throw new InvalidDataException("The approved checkpoint must retain its contract.");
            WorkflowActivationFixture.Check(result.Approved && !File.Exists(f.ReviewPath),
                "Approval must persist a dispatchable checkpoint and remove the review checkpoint.");
            WorkflowActivationFixture.Check(queued.Workflow is
                { State: WorkflowState.Queued, ApprovalGranted: true, ScopeApprovalGranted: true },
                "The shared native approver must grant only the selected sealed scope.");
            WorkflowActivationFixture.Check(queuedWorkflow.ApprovedContractHash == queuedWorkflow.ContractHash
                && queued.NotificationChatId == f.ChatId && queued.Context == f.Item.Context,
                "Approval must preserve contract, owner and original task fields.");
            WorkflowApprovalResult replay = await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId);
            WorkflowActivationFixture.Check(!replay.Approved && Directory.GetFiles(f.Queue, "*.task.json").Length == 1,
                "An approval replay must not create or overwrite an active job.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.WriteReviewAsync();
            string original = await File.ReadAllTextAsync(f.ReviewPath);
            WorkflowApprovalResult denied = await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId + 1);
            WorkflowActivationFixture.Check(!denied.Approved
                && original == await File.ReadAllTextAsync(f.ReviewPath) && !File.Exists(f.TaskPath),
                "A different configured chat cannot mutate the proposal.");
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, 0));
            f.Item.NotificationChatId = null;
            await f.WriteReviewAsync();
            WorkflowActivationFixture.Check(
                !(await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId)).Approved,
                "An ownerless job must not acquire an approval recipient.");
        }

        using (WorkflowActivationFixture f = new())
        {
            f.Item.Workflow!.Scope += " changed";
            await f.WriteReviewAsync();
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId));
            WorkflowActivationFixture.Check(!File.Exists(f.TaskPath), "A changed contract cannot dispatch.");
        }

        using (WorkflowActivationFixture f = new())
        {
            string id = f.Item.Id;
            f.Item.Id = "different-envelope";
            await WorkflowQueuePersistence.WriteAsync(Path.Combine(f.Queue, id + ".review.json"), f.Item);
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowQueueApproval.ApproveAsync(f.Queue, id, f.ChatId));
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.WriteReviewAsync();
            Directory.CreateDirectory(f.TaskPath);
            await WorkflowActivationFixture.ThrowsAsync<IOException>(() =>
                WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId));
            TaskItem checkpoint = await f.ReadAsync(".review.json");
            WorkflowActivationFixture.Check(checkpoint.Workflow is
                { State: WorkflowState.Queued, ScopeApprovalGranted: true } && !File.Exists(f.TaskPath),
                "A queue-write failure must retain the sealed approval without a dispatchable file.");
            Directory.Delete(f.TaskPath);
            WorkflowActivationFixture.Check(
                (await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId)).Approved,
                "An interrupted approved transition must resume without inventing a second approval.");
            WorkflowActivationFixture.Check(Directory.GetFiles(f.Queue, "*.tmp").Length == 0,
                "Failed atomic writes must clean up only their temporary files.");
        }
        Console.WriteLine("PASS: explicit one-job approval, owner checks, sealed contracts, replay prevention and interrupted persistence (offline).");
    }
}

internal sealed class WorkflowActivationFixture : IDisposable
{
    readonly string directory = Path.Combine(Path.GetTempPath(), "arsangaz-workflow-activation-" + Guid.NewGuid().ToString("N"));
    public string Queue => Path.Combine(directory, "Queue");
    public string Contexts => Path.Combine(directory, "Contexts");
    public string Outbox => Path.Combine(directory, "Outbox");
    public long ChatId => 42;
    public TaskItem Item { get; set; }
    public string ReviewPath => Path.Combine(Queue, Item.Id + ".review.json");
    public string TaskPath => Path.Combine(Queue, Item.Id + ".task.json");

    public WorkflowActivationFixture()
    {
        Directory.CreateDirectory(Queue);
        string id = "workflow-" + Guid.NewGuid().ToString("N");
        Item = new()
        {
            Id = id,
            Title = "Review the public local proposal",
            Provider = "VS Code Copilot Chat",
            Status = "İnsan onayı bekliyor",
            Created = DateTime.Now,
            Context = "Untrusted proposal data, not a permission grant.",
            NotificationChatId = ChatId,
            Workflow = WorkflowOrchestrator.FromRecommendation(
                id, WorkflowSource.Gozcu, "public-cycle", new string('x', 3_900),
                ["https://learn.microsoft.com/en-us/dotnet/"],
                "Only inspect local code; no private data, external writes, deployments or approval changes.",
                "Gözcü", maxAttempts: 2)
        };
    }

    public Task WriteReviewAsync() => WorkflowQueuePersistence.WriteAsync(ReviewPath, Item);

    public async Task<TaskItem> ReadAsync(string suffix) =>
        JsonSerializer.Deserialize<TaskItem>(await File.ReadAllTextAsync(Path.Combine(Queue, Item.Id + suffix)))
        ?? throw new InvalidDataException("Test checkpoint could not be read.");

    public async Task ApproveAndBeginAsync()
    {
        await WriteReviewAsync();
        Check((await WorkflowQueueApproval.ApproveAsync(Queue, Item.Id, ChatId)).Approved,
            "The synthetic proposal approval must succeed.");
        Item = await ReadAsync(".task.json");
        Check(WorkflowOrchestrator.BeginAttempt(Item.Workflow!, "request-" + Guid.NewGuid().ToString("N"), DateTime.UtcNow) is not null,
            "The selected sealed workflow must begin a single correlated attempt.");
        Item.Status = "Çalışıyor";
        await WorkflowQueuePersistence.WriteAsync(TaskPath, Item);
    }

    public static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    public static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}
