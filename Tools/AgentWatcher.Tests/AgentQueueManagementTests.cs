using System.Text.Json;
using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentQueueManagementTests
{
    internal static void Run()
    {
        DeletesOnlySelectedPendingTaskFiles();
        DeletesAllNonRunningQueueRecords();
        DailyReviewSchedulePolicyPreventsSameDayRequeue();
        RejectsUnknownOrOversizedAgentInstructions();
        AppendsAgentInstructionsWithoutReplacingRoleBoundaries();
    }

    private static void DeletesOnlySelectedPendingTaskFiles()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        string queue = Path.Combine(directory, "Queue");
        Directory.CreateDirectory(queue);

        try
        {
            string pending = WriteTask(queue, "pending", "Bekliyor");
            string active = WriteTask(queue, "active", "Bekliyor");
            string running = WriteTask(queue, "running", "Çalışıyor", DateTime.Now);
            string completed = Path.Combine(queue, "completed.done.json");
            File.WriteAllText(completed, "{}");

            int deleted = AgentTaskQueueMaintenance.DeleteSelectedPendingTasks(
                queue,
                [
                    Path.GetFileName(pending),
                    Path.GetFileName(active),
                    Path.GetFileName(running),
                    Path.GetFileName(completed),
                    "..\\outside.task.json"
                ],
                active);

            Assert(deleted == 1, "Only one eligible pending task should be deleted.");
            Assert(!File.Exists(pending), "The selected pending task should be removed.");
            Assert(File.Exists(active), "The active task must remain untouched.");
            Assert(File.Exists(running), "A task marked as running must remain untouched.");
            Assert(File.Exists(completed), "Completed task records must remain untouched.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DeletesAllNonRunningQueueRecords()
    {
        string directory = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        string queue = Path.Combine(directory, "Queue");
        Directory.CreateDirectory(queue);

        try
        {
            string pending = WriteTask(queue, "pending", "Bekliyor");
            string active = WriteTask(queue, "active", "Çalışıyor", DateTime.Now);
            string started = WriteTask(queue, "started", "Bekliyor", DateTime.Now);
            string completed = Path.Combine(queue, "completed.done.json");
            string failed = Path.Combine(queue, "failed.error.json");
            string cancelled = Path.Combine(queue, "cancelled.cancelled.json");
            string review = Path.Combine(queue, "review.review.json");
            string archivedDirectory = Path.Combine(queue, "PendingArchive");
            Directory.CreateDirectory(archivedDirectory);
            string archived = Path.Combine(archivedDirectory, "archived.done.json");
            string runtimeFile = Path.Combine(queue, "runtime.exe");
            foreach (string path in new[] { completed, failed, cancelled, review, archived })
            {
                File.WriteAllText(path, "{}");
            }
            File.WriteAllText(runtimeFile, "preserve");

            int deleted = AgentTaskQueueMaintenance.DeleteAllNonRunningQueueRecords(queue, active);

            Assert(deleted == 6, "Every non-running queue record, including archived and terminal records, should be deleted.");
            Assert(!File.Exists(pending) && !File.Exists(completed) && !File.Exists(failed)
                && !File.Exists(cancelled) && !File.Exists(review) && !File.Exists(archived),
                "Pending, terminal, approval and archived records should be removed.");
            Assert(File.Exists(active), "The active task must remain untouched.");
            Assert(File.Exists(started), "A task with a start timestamp must remain untouched as possibly in flight.");
            Assert(File.Exists(runtimeFile), "Non-JSON runtime files must remain untouched.");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void DailyReviewSchedulePolicyPreventsSameDayRequeue()
    {
        Assert(
            !DailyReviewSchedulePolicy.ShouldQueue("20261009", "20261009"),
            "Clearing today's queue must not immediately recreate the daily review.");
        Assert(
            DailyReviewSchedulePolicy.ShouldQueue("20261010", "20261009"),
            "The daily review schedule must resume on the next day.");
    }

    private static void RejectsUnknownOrOversizedAgentInstructions()
    {
        Dictionary<string, string> unknownAgent = new(StringComparer.OrdinalIgnoreCase)
        {
            ["unknown-agent"] = "Do work"
        };
        try
        {
            _ = AgentInstructionPolicy.NormalizeAll(unknownAgent);
            throw new InvalidOperationException("Unknown agents must not accept persisted instructions.");
        }
        catch (InvalidDataException)
        {
        }

        string tooLong = new('x', AgentInstructionPolicy.MaximumInstructionLength + 1);
        try
        {
            _ = AgentInstructionPolicy.NormalizeInstruction(tooLong);
            throw new InvalidOperationException("Oversized agent instructions must be rejected.");
        }
        catch (ArgumentOutOfRangeException)
        {
        }
    }

    private static void AppendsAgentInstructionsWithoutReplacingRoleBoundaries()
    {
        const string fixedBoundary = "No external action or file changes.";
        string prompt = AgentInstructionPolicy.AppendToSystemPrompt(
            fixedBoundary,
            "Focus on risks and give concise recommendations.");

        Assert(prompt.StartsWith(fixedBoundary, StringComparison.Ordinal),
            "The original role boundary must remain in the system prompt.");
        Assert(prompt.Contains("Focus on risks and give concise recommendations.", StringComparison.Ordinal),
            "The configured agent instruction must reach its prompt.");
        Assert(prompt.Contains("sabit güvenlik, veri erişimi, onay ve işlem sınırlarını genişletme", StringComparison.Ordinal),
            "The instruction must explicitly remain subordinate to fixed permission boundaries.");
    }

    private static string WriteTask(string queue, string id, string status, DateTime? started = null)
    {
        string path = Path.Combine(queue, id + ".task.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(new TaskItem
            {
                Id = id,
                Title = id,
                Type = "Test",
                Status = status,
                Created = DateTime.Now,
                Started = started
            }));
        return path;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
