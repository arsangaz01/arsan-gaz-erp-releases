using System;
using System.Collections.Generic;

namespace ArsanGazERP.Models;

public sealed class CrmCompany
{
    public int Id { get; set; }
    public int? SourceProspectId { get; set; }
    public string CompanyName { get; set; } = string.Empty;
    public string? City { get; set; }
    public string? Sector { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public string? Website { get; set; }
    public string? Address { get; set; }
    public int Score { get; set; }
    public string Status { get; set; } = "Yeni";
    public string? AssignedTo { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastContactAtUtc { get; set; }
    public ICollection<CrmActivity> Activities { get; set; } = new List<CrmActivity>();
    public ICollection<CrmNote> Notes { get; set; } = new List<CrmNote>();
}

public sealed class CrmActivity
{
    public int Id { get; set; }
    public int CrmCompanyId { get; set; }
    public CrmCompany Company { get; set; } = null!;
    public string ActivityType { get; set; } = "Not";
    public string Description { get; set; } = string.Empty;
    public DateTime ActivityAtUtc { get; set; } = DateTime.UtcNow;
    public string UserName { get; set; } = Environment.UserName;
}

public sealed class CrmNote
{
    public int Id { get; set; }
    public int CrmCompanyId { get; set; }
    public CrmCompany Company { get; set; } = null!;
    public string NoteText { get; set; } = string.Empty;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
