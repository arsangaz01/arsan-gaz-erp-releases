using System.Globalization;

namespace ArsanGazERP.AgentWatcher;

internal static class TelegramQueuePolicy
{
    public const string ErrorNoticeKind = "error";
    public const string CollectionReminderKind = "collection-reminder";

    public static IEnumerable<string> OrderTasks(
        IEnumerable<string> taskFiles,
        Func<string, DateTime> getCreatedTime,
        Func<string, string?>? getPriority = null)
    {
        if (getPriority is null)
        {
            List<string> legacyOrder = taskFiles
                .OrderBy(getCreatedTime)
                .ThenBy(ArrivalOrder)
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .ToList();
            int[] telegramPositions = Enumerable.Range(0, legacyOrder.Count)
                .Where(index => ArrivalOrder(legacyOrder[index]) != long.MaxValue)
                .ToArray();
            string[] telegramTasks = telegramPositions
                .Select(index => legacyOrder[index])
                .OrderBy(ArrivalOrder)
                .ThenBy(Path.GetFileName, StringComparer.Ordinal)
                .ToArray();

            for (int index = 0; index < telegramPositions.Length; index++)
            {
                legacyOrder[telegramPositions[index]] = telegramTasks[index];
            }

            return legacyOrder;
        }

        return taskFiles
            .OrderBy(path => ArrivalOrder(path) == long.MaxValue
                ? PriorityRank(getPriority(path))
                : 0)
            .ThenBy(path => ArrivalOrder(path) == long.MaxValue ? 1 : 0)
            .ThenBy(path => ArrivalOrder(path) == long.MaxValue
                ? getCreatedTime(path).Ticks
                : ArrivalOrder(path))
            .ThenBy(Path.GetFileName, StringComparer.Ordinal)
            .ToArray();
    }

    private static int PriorityRank(string? priority) =>
        priority?.Trim() switch
        {
            string value when value.Equals("Acil", StringComparison.OrdinalIgnoreCase) => 0,
            string value when value.Equals("Yüksek", StringComparison.OrdinalIgnoreCase) => 1,
            _ => 2
        };

    public static long ArrivalOrder(string file)
    {
        string name = Path.GetFileName(file);
        const string prefix = "telegram-";
        const string suffix = ".task.json";
        return name.StartsWith(prefix, StringComparison.Ordinal)
            && name.EndsWith(suffix, StringComparison.Ordinal)
            && long.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length),
                NumberStyles.None, CultureInfo.InvariantCulture, out long updateId)
            ? updateId : long.MaxValue;
    }

    public static bool ShouldSuppressNonErrorNotice(string? outboxKind) =>
        !string.Equals(outboxKind, ErrorNoticeKind, StringComparison.Ordinal)
        && !string.Equals(outboxKind, CollectionReminderKind, StringComparison.Ordinal)
        && !string.Equals(outboxKind, TelegramWorkflowNotice.OutboxKind, StringComparison.Ordinal)
        && !string.Equals(outboxKind, TelegramWorkflowNotice.ApprovalOutboxKind, StringComparison.Ordinal)
        && !string.Equals(outboxKind, TelegramCopilotInbox.DirectResponseKind, StringComparison.Ordinal);

    public static bool ShouldQueueWorkflowNotice(bool enabled, string taskId, long? chatId) =>
        enabled
        && chatId is long authorizedChatId
        && authorizedChatId != 0
        && (ArrivalOrder(taskId + ".task.json") != long.MaxValue
            || IsRecommendationTaskId(taskId));

    public static bool ShouldSuppressWorkflowNotice(bool enabled) => !enabled;

    public static bool ShouldQueueWorkflowApprovalNotice(long? chatId) =>
        chatId is long authorizedChatId && authorizedChatId != 0;

    private static bool IsRecommendationTaskId(string taskId) =>
        taskId.StartsWith("recommendation-", StringComparison.Ordinal)
        && taskId.Length <= 80
        && taskId.All(character =>
            char.IsAsciiLetterOrDigit(character) || character is '-' or '_');
}
