using ArsanGazERP.AgentWatcher;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class TelegramPriorityTests
{
    internal static void Run()
    {
        TelegramCommandsPrecedeOlderAutomationWork();
        TelegramCommandsRemainInArrivalOrderWithinPriority();
        LowerPriorityWorkRemainsChronological();
    }

    private static void TelegramCommandsPrecedeOlderAutomationWork()
    {
        string[] files =
        [
            "scheduled-old.task.json",
            "telegram-42.task.json",
            "telegram-43.task.json"
        ];

        string[] ordered = TelegramQueuePolicy.OrderTasks(
            files,
            path => path == "scheduled-old.task.json" ? DateTime.MinValue : DateTime.MaxValue,
            _ => "Normal").ToArray();

        Assert(ordered[0] == "telegram-42.task.json", "Queued Telegram work must precede older automated work.");
    }

    private static void TelegramCommandsRemainInArrivalOrderWithinPriority()
    {
        string[] files = ["telegram-19.task.json", "telegram-17.task.json", "telegram-18.task.json"];

        string[] ordered = TelegramQueuePolicy.OrderTasks(
            files,
            _ => DateTime.MinValue,
            _ => "Normal").ToArray();

        Assert(
            ordered.SequenceEqual(["telegram-17.task.json", "telegram-18.task.json", "telegram-19.task.json"]),
            "Telegram commands must retain numeric Telegram arrival order.");
    }

    private static void LowerPriorityWorkRemainsChronological()
    {
        string[] files = ["later.task.json", "earlier.task.json"];

        string[] ordered = TelegramQueuePolicy.OrderTasks(
            files,
            path => path == "earlier.task.json" ? DateTime.MinValue : DateTime.MaxValue,
            _ => "Normal").ToArray();

        Assert(
            ordered.SequenceEqual(["earlier.task.json", "later.task.json"]),
            "Tasks at the same lower priority must remain chronological.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
