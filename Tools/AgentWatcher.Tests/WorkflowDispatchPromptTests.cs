using ArsanGazERP.AgentWatcher;
using System.Text.Json;

internal static class WorkflowDispatchPromptTests
{
    public static async Task RunAsync()
    {
        using (WorkflowActivationFixture f = new())
        {
            string exact = " \nTelegram özgün isteği\nboşlukları koru. ";
            f.Item.Title = exact;
            f.Item.Workflow = WorkflowOrchestrator.FromTelegramRequest(f.Item.Id, exact);
            WorkflowActivationFixture.Check(
                WorkflowOrchestrator.BeginAttempt(f.Item.Workflow, "telegram-request", DateTime.UtcNow) is not null,
                "An ordinary authorized Telegram request should begin.");
            string prompt = await WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts);
            WorkflowActivationFixture.Check(prompt == exact && !Directory.Exists(f.Contexts),
                "Telegram prompts must remain exact, without metadata or workflow instructions.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            WorkflowJob job = f.Item.Workflow!;
            string prompt = await WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts);
            string contextPath = Directory.GetFiles(f.Contexts, "*.workflow.json").Single();
            string serialized = await File.ReadAllTextAsync(contextPath);
            WorkflowDispatchContext context = JsonSerializer.Deserialize<WorkflowDispatchContext>(serialized)
                ?? throw new InvalidDataException("Dispatch context must deserialize.");
            WorkflowActivationFixture.Check(prompt.Contains(contextPath, StringComparison.Ordinal)
                && job.Criteria.All(criterion => prompt.Contains(criterion.Id, StringComparison.Ordinal))
                && prompt.Length <= 4_096,
                "The actual bridge Text must refer to the complete local contract and every criterion within the transport limit.");
            WorkflowActivationFixture.Check(context.TaskId == f.Item.Id && context.RequestId == job.CurrentRequestId
                && context.Attempt == 1 && context.Scope == job.Scope
                && context.SourceEvidence == job.SourceEvidence && context.Criteria.SequenceEqual(job.Criteria),
                "The context must preserve complete evidence and sealed scope for exactly this dispatch.");
            WorkflowActivationFixture.Check(context.ApprovedContractHash == context.ContractHash
                && prompt.Contains("güvenilmeyen veridir", StringComparison.Ordinal),
                "The model must be directed to an approved contract while treating external sources as data.");
            using JsonDocument document = JsonDocument.Parse(serialized);
            WorkflowActivationFixture.Check(!document.RootElement.TryGetProperty("NotificationChatId", out _)
                && !document.RootElement.TryGetProperty("Context", out _),
                "Workflow model context must not export chat ownership or legacy private conversation context.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts));
            WorkflowActivationFixture.Check(!Directory.Exists(f.Contexts),
                "An unapproved candidate must not produce a dispatch prompt or context file.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            f.Item.Workflow!.Scope += " changed after approval";
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts));
            WorkflowActivationFixture.Check(!Directory.Exists(f.Contexts),
                "Changing the approved scope must fail before source export.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            f.Item.Id = "different-envelope";
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts));
            WorkflowActivationFixture.Check(!Directory.Exists(f.Contexts),
                "A mismatched envelope cannot borrow another workflow's approval.");
        }
        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            f.Item.Workflow!.CurrentRequestId = "..\\redirect";
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() =>
                WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts));
            WorkflowActivationFixture.Check(!Directory.Exists(f.Contexts),
                "A redirected request ID must fail before any context file is written.");
        }
        Console.WriteLine("PASS: exact Telegram text and approval-bound non-Telegram workflow contracts reach bridge Text without source truncation (offline).");
    }
}
