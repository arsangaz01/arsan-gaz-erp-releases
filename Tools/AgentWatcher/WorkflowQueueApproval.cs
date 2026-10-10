using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed record WorkflowApprovalResult(bool Approved, string Detail, TaskItem? Item = null);
internal sealed record WorkflowApprovalCandidate(TaskItem Item, bool ApprovalAlreadyGranted);

internal static class WorkflowQueueApproval
{
    internal const string LocalCommand = "--approve-workflow";
    private static readonly string[] TerminalSuffixes = [".done.json", ".error.json", ".cancelled.json"];

    internal static string? ParseLocalCommand(string[] arguments)
    {
        if (arguments.Length == 0 || arguments[0] != LocalCommand) return null;
        if (arguments.Length != 2)
            throw new InvalidDataException("Kullanım: --approve-workflow <iş-id>. Her çağrı yalnızca bir işi onaylar.");
        WorkflowOrchestrator.ValidateIdentifier(arguments[1], "taskId");
        return arguments[1];
    }

    internal static async Task<WorkflowApprovalResult> ApproveAsync(
        string queueDirectory,
        string taskId,
        long authorizedChatId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDirectory);
        WorkflowOrchestrator.ValidateIdentifier(taskId, nameof(taskId));
        if (authorizedChatId == 0)
            throw new InvalidDataException("Yapılandırılmış izinli Telegram sohbeti olmadan workflow onaylanamaz.");

        string reviewPath = Path.Combine(queueDirectory, taskId + ".review.json");
        string taskPath = Path.Combine(queueDirectory, taskId + ".task.json");
        if (File.Exists(taskPath) || TerminalSuffixes.Any(suffix => File.Exists(Path.Combine(queueDirectory, taskId + suffix))))
            return new(false, "Bu workflow için etkin veya terminal kayıt var; eski inceleme kaydı yeniden dispatch edilmedi.");
        if (!File.Exists(reviewPath))
            return new(false, "Onay bekleyen workflow bulunamadı.");

        TaskItem item = JsonSerializer.Deserialize<TaskItem>(
            await File.ReadAllTextAsync(reviewPath, cancellationToken))
            ?? throw new InvalidDataException("Workflow onay kaydı okunamadı.");
        if (item.NotificationChatId != authorizedChatId || item.Workflow is null)
            return new(false, "Bu sohbet workflow'u onaylayamaz.");
        if (item.Id != taskId || item.Workflow.TaskId != taskId)
            throw new InvalidDataException("Workflow dosya/iş kimliği eşleşmiyor.");

        WorkflowOrchestrator.Validate(item.Workflow);
        if (item.Workflow.State is WorkflowState.AwaitingApproval or WorkflowState.ReworkAwaitingApproval)
        {
            if (!WorkflowOrchestrator.Approve(item.Workflow, authorized: true))
                return new(false, $"Workflow şu durumda onaylanamaz: {item.Workflow.State}.");
        }
        else if (item.Workflow.State != WorkflowState.Queued ||
            !item.Workflow.ScopeApprovalGranted ||
            item.Workflow.ApprovedContractHash != item.Workflow.ContractHash)
        {
            return new(false, $"Workflow şu durumda onaylanamaz: {item.Workflow.State}.");
        }

        item.Status = "Bekliyor";
        // Save the sealed approval first so an interrupted transition can resume without a new approval.
        await WorkflowQueuePersistence.WriteAsync(reviewPath, item, cancellationToken: cancellationToken);
        await WorkflowQueuePersistence.WriteAsync(taskPath, item, overwrite: false, cancellationToken: cancellationToken);
        File.Delete(reviewPath);
        return new(true, $"Workflow {taskId} onaylandı ve güvenli kuyruk sınırında işleme alındı.", item);
    }

    internal static async Task<IReadOnlyList<WorkflowApprovalCandidate>> ListPendingAsync(
        string queueDirectory,
        long authorizedChatId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDirectory);
        if (authorizedChatId == 0)
            throw new InvalidDataException("Yapılandırılmış izinli Telegram sohbeti olmadan onaylar listelenemez.");
        if (!Directory.Exists(queueDirectory))
            return [];

        List<WorkflowApprovalCandidate> candidates = [];
        foreach (string reviewPath in Directory.EnumerateFiles(queueDirectory, "*.review.json")
            .OrderBy(path => path, StringComparer.Ordinal))
        {
            cancellationToken.ThrowIfCancellationRequested();
            TaskItem item = JsonSerializer.Deserialize<TaskItem>(
                await File.ReadAllTextAsync(reviewPath, cancellationToken))
                ?? throw new InvalidDataException($"Workflow onay kaydı boş: {Path.GetFileName(reviewPath)}");
            WorkflowJob workflow = item.Workflow
                ?? throw new InvalidDataException($"Workflow onay kaydında sözleşme yok: {Path.GetFileName(reviewPath)}");
            string taskId = Path.GetFileName(reviewPath)[..^".review.json".Length];
            WorkflowOrchestrator.ValidateIdentifier(taskId, nameof(taskId));
            if (!string.Equals(item.Id, taskId, StringComparison.Ordinal)
                || !string.Equals(workflow.TaskId, taskId, StringComparison.Ordinal))
                throw new InvalidDataException($"Workflow dosya/iş kimliği eşleşmiyor: {Path.GetFileName(reviewPath)}");

            WorkflowOrchestrator.Validate(workflow);
            if (item.NotificationChatId != authorizedChatId
                || File.Exists(Path.Combine(queueDirectory, taskId + ".task.json"))
                || TerminalSuffixes.Any(suffix => File.Exists(Path.Combine(queueDirectory, taskId + suffix))))
                continue;

            bool alreadyGranted = workflow.State == WorkflowState.Queued
                && workflow.ScopeApprovalGranted
                && string.Equals(workflow.ApprovedContractHash, workflow.ContractHash, StringComparison.Ordinal);
            if (workflow.State is WorkflowState.AwaitingApproval or WorkflowState.ReworkAwaitingApproval
                || alreadyGranted)
            {
                candidates.Add(new(item, alreadyGranted));
            }
        }

        return candidates;
    }
}
