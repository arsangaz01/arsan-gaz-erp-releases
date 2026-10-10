using ArsanGazERP.AgentWatcher;

internal static class WorkflowActivationIntegrationTests
{
    public static async Task RunAsync()
    {
        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            string prompt = await WorkflowBridgePrompt.CreateAsync(f.Item, f.Contexts);
            f.Item = await f.ReadAsync(".task.json");
            WorkflowJob job = f.Item.Workflow!;
            WorkflowActivationFixture.Check(prompt.Contains(f.Item.Id, StringComparison.Ordinal)
                && job.State == WorkflowState.Dispatching && job.Attempt == 1,
                "A restarted reader must see the same approved dispatch and complete contract.");
            await WorkflowActivationFixture.ThrowsAsync<InvalidDataException>(() => Task.FromResult(
                WorkflowOrchestrator.RecordBridgeCompletion(job,
                    new("other-request", f.Item.Id, "completed", "unrelated result"), DateTime.UtcNow)));
            WorkflowActivationFixture.Check(WorkflowOrchestrator.RecordBridgeCompletion(job,
                    new(job.CurrentRequestId!, f.Item.Id, "completed", "Only the requested local analysis was performed."),
                    DateTime.UtcNow),
                "Only the matching bridge result may enter review.");
            f.Item.Status = "Kabul ölçütleri incelemesi bekliyor";
            await WorkflowQueuePersistence.WriteAsync(f.TaskPath, f.Item);
            WorkflowQueuePersistence.Move(f.TaskPath, f.ReviewPath);
            TaskItem review = await f.ReadAsync(".review.json");
            WorkflowActivationFixture.Check(review.Workflow!.State == WorkflowState.AwaitingReview
                && !File.Exists(f.TaskPath),
                "Copilot completion must stop at review, not self-approve acceptance.");

            TelegramWorkflowNotice notice = TelegramWorkflowNotice.Create(
                f.Item.Id, f.ChatId, TelegramWorkflowStage.Review, job.Attempt);
            WorkflowActivationFixture.Check(await TelegramWorkflowNotice.WriteAsync(f.Outbox, notice),
                "The correlated review stage must persist to the existing outbox format.");
            WorkflowActivationFixture.Check(!await TelegramWorkflowNotice.WriteAsync(f.Outbox, notice)
                && Directory.GetFiles(f.Outbox, "*.json").Length == 1,
                "Repeated stage emission must not create duplicate reports.");

            WorkflowCriterionProof[] proofs = review.Workflow.Criteria.Select(criterion => new WorkflowCriterionProof(
                criterion.Id, WorkflowCriterionStatus.Met, "HumanReview", "synthetic-authorized-review",
                "The synthetic reviewer verified the corresponding local assertion.")).ToArray();
            WorkflowActivationFixture.Check(WorkflowOrchestrator.ApplyReview(
                review.Workflow, "human", proofs, authorizedHuman: true, static (_, _, _) => false)
                == WorkflowState.Completed,
                "The separate authorized criterion review may complete the persisted workflow.");
            await WorkflowQueuePersistence.WriteAsync(Path.Combine(f.Queue, f.Item.Id + ".done.json"), review);
            f.Item = review;
            WorkflowActivationFixture.Check(
                !(await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId)).Approved,
                "A terminal checkpoint must prevent an old review from dispatching again.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            WorkflowJob job = f.Item.Workflow!;
            WorkflowOrchestrator.RecordBridgeCompletion(job,
                new(job.CurrentRequestId!, f.Item.Id, "completed", "Model claims completion."), DateTime.UtcNow);
            WorkflowCriterionProof[] unverified = job.Criteria.Select(criterion => new WorkflowCriterionProof(
                criterion.Id, WorkflowCriterionStatus.Met, "CopilotClaim", "model-only",
                "The model says the criterion passed.")).ToArray();
            WorkflowActivationFixture.Check(WorkflowOrchestrator.ApplyReview(
                job, "Gözcü", unverified, authorizedHuman: false, static (_, _, _) => false)
                == WorkflowState.Blocked,
                "Unverified model claims cannot replace human or registered test evidence.");
            await WorkflowQueuePersistence.WriteAsync(f.TaskPath, f.Item);
            WorkflowQueuePersistence.Move(f.TaskPath, f.ReviewPath);
            WorkflowActivationFixture.Check(
                !(await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId)).Approved
                && !File.Exists(f.TaskPath),
                "A blocked acceptance must not become a fresh dispatch through approval replay.");
        }

        using (WorkflowActivationFixture f = new())
        {
            await f.ApproveAndBeginAsync();
            f.Item = await f.ReadAsync(".task.json");
            WorkflowOrchestrator.RecoverInterrupted(f.Item.Workflow!);
            await WorkflowQueuePersistence.WriteAsync(f.TaskPath, f.Item);
            WorkflowQueuePersistence.Move(f.TaskPath, f.ReviewPath);
            WorkflowActivationFixture.Check(
                !(await WorkflowQueueApproval.ApproveAsync(f.Queue, f.Item.Id, f.ChatId)).Approved,
                "An interrupted uncertain result must remain fail-stop after restart.");
        }
        Console.WriteLine("PASS: production approval -> durable dispatch context -> correlated completion -> separate review -> terminal replay prevention and outbox (offline).");
    }
}
