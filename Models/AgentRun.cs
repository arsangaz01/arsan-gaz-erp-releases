using System;

namespace ArsanGazERP.Models;

public sealed class AgentRun
{
    public int Id { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime CompletedAtUtc { get; set; }
    public bool Succeeded { get; set; }
    public int FindingCount { get; set; }
    public int CriticalCount { get; set; }
    public string Summary { get; set; } = string.Empty;
    public string FindingsJson { get; set; } = "[]";
}