namespace ArsanGazERP.Models;

public sealed class AgentFinding
{
    public AgentFinding(string severity, string category, string message)
    {
        Severity = severity;
        Category = category;
        Message = message;
    }

    public string Severity { get; }
    public string Category { get; }
    public string Message { get; }
}
