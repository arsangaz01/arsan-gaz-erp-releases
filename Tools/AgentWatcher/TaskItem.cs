namespace ArsanGazERP.AgentWatcher;

internal sealed class TaskItem
{
    internal const string RunningStatus = "Çalışıyor";

    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string Type { get; set; } = "";
    public string Priority { get; set; } = "";
    public string Provider { get; set; } = "";
    public string Status { get; set; } = "";
    public DateTime Created { get; set; }
    public DateTime? Started { get; set; }
    public DateTime? Completed { get; set; }
    public string? Report { get; set; }
    public string? Error { get; set; }
    public string? Context { get; set; }
    public bool CloudSafeContext { get; set; }
    public bool RetryRequiresReview { get; set; }
    public long? NotificationChatId { get; set; }
    public List<AgentSubtask> AgentSubtasks { get; set; } = [];
    public List<TelegramAttachment> Attachments { get; set; } = [];
    public WorkflowJob? Workflow { get; set; }

    internal void MarkRunning(DateTime started)
    {
        Status = RunningStatus;
        Started = started;
        Error = null;
    }
}

internal sealed class AgentSubtask
{
    public string AgentId { get; set; } = "";
    public string AgentName { get; set; } = "";
    public string Task { get; set; } = "";
    public string Status { get; set; } = "Bekliyor";
    public DateTime? Started { get; set; }
    public DateTime? Completed { get; set; }
    public string? Response { get; set; }
}
