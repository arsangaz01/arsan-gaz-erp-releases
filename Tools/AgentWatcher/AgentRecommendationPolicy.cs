using System.Globalization;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal static class AgentRecommendationPolicy
{
    internal const int DailyAutoForwardLimit = 6;
    internal const string ActionableYes = "ACTIONABLE: yes";
    internal const string ActionableNo = "ACTIONABLE: no";
    internal const string ActionabilityMissing = "ACTIONABLE: missing/invalid";

    private static readonly object DailyReservationLock = new();
    private static readonly string[] QueueRecordPatterns =
    [
        "*.task.json",
        "*.review.json",
        "*.done.json",
        "*.error.json",
        "*.cancelled.json"
    ];
    private static readonly JsonSerializerOptions QueueJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal static string GetActionabilityDecision(string? report)
    {
        if (string.IsNullOrWhiteSpace(report))
        {
            return ActionabilityMissing;
        }

        string finalLine = report
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .LastOrDefault(line => !string.IsNullOrWhiteSpace(line))
            ?.Trim() ?? string.Empty;
        return finalLine switch
        {
            ActionableYes => ActionableYes,
            ActionableNo => ActionableNo,
            _ => ActionabilityMissing
        };
    }

    internal static bool RequiresApproval(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return !settings.AutoForwardAgentRecommendations;
    }

    internal static bool ShouldPauseQueueForFailure(TaskItem? item) =>
        item?.Workflow?.Source is not (WorkflowSource.Gozcu or WorkflowSource.ArGe)
        && item?.Type != GozcuAutonomy.TaskType
        && item?.Id.StartsWith("erp-research-", StringComparison.Ordinal) != true;

    internal static void ValidateSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        bool validDate = string.IsNullOrEmpty(settings.AutoForwardAgentRecommendationsDate)
            || DateTime.TryParseExact(
                settings.AutoForwardAgentRecommendationsDate,
                "yyyyMMdd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out _);
        if (settings.AutoForwardAgentRecommendationsCount is < 0 or > DailyAutoForwardLimit
            || !validDate
            || settings.AutoForwardAgentRecommendationsCount > 0
                && string.IsNullOrEmpty(settings.AutoForwardAgentRecommendationsDate))
        {
            throw new InvalidDataException("Otomatik öneri günlük sınırı veya yerel gün kaydı geçersiz.");
        }
    }

    internal static bool TryReserveDailyAutoForward(
        AppSettings settings,
        string queueDirectory,
        DateTime localNow)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(queueDirectory);
        lock (DailyReservationLock)
        {
            ValidateSettings(settings);
            if (!settings.AutoForwardAgentRecommendations)
            {
                return false;
            }

            DateTime localDate = localNow.Kind == DateTimeKind.Utc
                ? localNow.ToLocalTime().Date
                : localNow.Date;
            string date = localDate.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            int persistedCount = string.Equals(
                    settings.AutoForwardAgentRecommendationsDate,
                    date,
                    StringComparison.Ordinal)
                ? settings.AutoForwardAgentRecommendationsCount
                : 0;
            int retainedCount = CountRetainedAutoForwardedRecommendations(queueDirectory, localDate);
            int currentCount = Math.Max(persistedCount, retainedCount);

            settings.AutoForwardAgentRecommendationsDate = date;
            settings.AutoForwardAgentRecommendationsCount = currentCount;
            if (currentCount >= DailyAutoForwardLimit)
            {
                settings.Save();
                return false;
            }

            settings.AutoForwardAgentRecommendationsCount++;
            settings.Save();
            return true;
        }
    }

    private static int CountRetainedAutoForwardedRecommendations(
        string queueDirectory,
        DateTime localDate)
    {
        if (!Directory.Exists(queueDirectory))
        {
            return 0;
        }

        HashSet<string> taskIds = new(StringComparer.Ordinal);
        foreach (string pattern in QueueRecordPatterns)
        {
            foreach (string path in Directory.EnumerateFiles(queueDirectory, pattern, SearchOption.TopDirectoryOnly))
            {
                string json;
                try
                {
                    json = File.ReadAllText(path);
                }
                catch (FileNotFoundException)
                {
                    continue;
                }

                TaskItem item;
                try
                {
                    item = JsonSerializer.Deserialize<TaskItem>(json, QueueJsonOptions)
                        ?? throw new InvalidDataException("Kuyruk kaydı null olamaz.");
                }
                catch (JsonException exception)
                {
                    throw new InvalidDataException(
                        "Otomatik öneri sınırı kuyruk kaydı doğrulanamadığı için güvenli biçimde durduruldu.",
                        exception);
                }

                if (!item.Id.StartsWith("recommendation-", StringComparison.Ordinal))
                {
                    continue;
                }

                WorkflowJob workflow = item.Workflow
                    ?? throw new InvalidDataException(
                        "Öneri kuyruğunda workflow sözleşmesi eksik; günlük sınır doğrulanamadı.");
                if (workflow.Source is not (WorkflowSource.Gozcu or WorkflowSource.ArGe)
                    || !string.Equals(workflow.TaskId, item.Id, StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        "Öneri kuyruğunda kaynak/iş kimliği geçersiz; günlük sınır doğrulanamadı.");
                }

                if (workflow.RequiresApproval)
                {
                    continue;
                }

                if (item.Created == default)
                {
                    throw new InvalidDataException(
                        "Otomatik öneri kuyruğunda oluşturulma zamanı eksik; günlük sınır doğrulanamadı.");
                }

                DateTime createdLocalDate = item.Created.Kind == DateTimeKind.Utc
                    ? item.Created.ToLocalTime().Date
                    : item.Created.Date;
                if (createdLocalDate == localDate.Date)
                {
                    taskIds.Add(item.Id);
                }
            }
        }

        return taskIds.Count;
    }
}
