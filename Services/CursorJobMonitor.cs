using System.IO;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.Services;

public sealed record CursorJob(
    string Id,
    string Source,
    string Status,
    bool IsActive,
    bool NeedsAttention,
    DateTime CreatedLocal,
    TimeSpan Duration,
    string LastStep,
    string Summary);

/// <summary>
/// Reads the Cursor bridge request files in CopilotInbox for the "Cursor İşleri" panel and requests an emergency stop
/// by writing an &lt;id&gt;.cancel.json marker that bridge.js polls while a run is active.
/// </summary>
public static class CursorJobMonitor
{
    public static readonly TimeSpan SlowJobThreshold = TimeSpan.FromMinutes(10);
    private static readonly string[] Suffixes =
    [
        ".request.json", ".dispatch.json", ".ack.json", ".progress.json", ".completion.json",
        ".ide-claim.json", ".ide-started.json", ".cancel.json"
    ];

    public static string DefaultInbox => Path.Combine(AgentWatcherPaths.DataRoot, "CopilotInbox");

    public static IReadOnlyList<CursorJob> Read(string inbox, DateTime nowUtc, int maximum = 60)
    {
        if (!Directory.Exists(inbox)) return [];
        Dictionary<string, Dictionary<string, string>> groups = new(StringComparer.Ordinal);
        foreach (string path in Directory.EnumerateFiles(inbox, "*.json"))
        {
            string name = Path.GetFileName(path);
            string? suffix = Suffixes.FirstOrDefault(candidate => name.EndsWith(candidate, StringComparison.OrdinalIgnoreCase));
            if (suffix is null) continue;
            string id = name[..^suffix.Length];
            if (!groups.TryGetValue(id, out Dictionary<string, string>? files)) groups[id] = files = new(StringComparer.Ordinal);
            files[suffix] = path;
        }

        return groups
            .Select(group => ToJob(group.Key, group.Value, nowUtc))
            .OrderByDescending(job => job.IsActive)
            .ThenByDescending(job => job.CreatedLocal)
            .Take(maximum)
            .ToList();
    }

    public static bool RequestCancel(string inbox, string id)
    {
        // Only bridge.js polls cancel markers; requests handed to the IDE chat cannot be stopped this way.
        if (!IsSafeId(id)
            || File.Exists(Path.Combine(inbox, id + ".completion.json"))
            || File.Exists(Path.Combine(inbox, id + ".ide-claim.json"))
            || File.Exists(Path.Combine(inbox, id + ".ide-started.json")))
        {
            return false;
        }

        string json = JsonSerializer.Serialize(new { id, requestedBy = "erp-control-center", createdUtc = DateTime.UtcNow });
        File.WriteAllText(Path.Combine(inbox, id + ".cancel.json"), json, new UTF8Encoding(false));
        return true;
    }

    public static int CancelAllActive(string inbox, DateTime nowUtc) =>
        Read(inbox, nowUtc, int.MaxValue).Where(job => job.IsActive).Count(job => RequestCancel(inbox, job.Id));

    private static bool IsSafeId(string id) =>
        id.Length is > 0 and <= 200 && id.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.')
        && !id.Contains("..", StringComparison.Ordinal);

    private static CursorJob ToJob(string id, Dictionary<string, string> files, DateTime nowUtc)
    {
        DateTime createdUtc = files.Values.Min(File.GetCreationTimeUtc);
        string? completionPath = files.GetValueOrDefault(".completion.json");
        JsonElement? completion = ReadJson(completionPath);
        JsonElement? ack = ReadJson(files.GetValueOrDefault(".ack.json"));
        JsonElement? progress = ReadJson(files.GetValueOrDefault(".progress.json"));

        string status;
        bool active = false;
        bool failed = false;
        DateTime endUtc = nowUtc;
        string summary = "";
        if (completion is { } done)
        {
            status = Text(done, "status") == "cancelled" ? "Durduruldu" : "Tamamlandı";
            summary = Text(done, "report");
            endUtc = File.GetLastWriteTimeUtc(completionPath!);
        }
        else if (ack is { } acknowledgement && Text(acknowledgement, "status") == "failed")
        {
            status = "Başarısız";
            failed = true;
            summary = Text(acknowledgement, "error");
            endUtc = File.GetLastWriteTimeUtc(files[".ack.json"]);
        }
        else if (!files.ContainsKey(".request.json") && progress is null && ack is null
                 && !files.ContainsKey(".ide-claim.json") && !files.ContainsKey(".ide-started.json"))
        {
            status = "Belirsiz";
            summary = "İstek dosyası yok ve sonuç yazılmamış; iş muhtemelen yarıda kaldı.";
        }
        else
        {
            active = true;
            status = files.ContainsKey(".cancel.json") ? "Durduruluyor"
                : files.ContainsKey(".ide-started.json") || files.ContainsKey(".ide-claim.json") ? "IDE sohbetinde"
                : progress is not null || ack is not null ? "Çalışıyor"
                : "Kuyrukta";
        }

        string lastStep = progress is { } state && state.TryGetProperty("steps", out JsonElement steps)
            && steps.ValueKind == JsonValueKind.Array && steps.GetArrayLength() > 0
            ? steps[steps.GetArrayLength() - 1].GetString() ?? ""
            : "";
        TimeSpan duration = endUtc > createdUtc ? endUtc - createdUtc : TimeSpan.Zero;
        bool needsAttention = failed || (active && duration >= SlowJobThreshold);
        return new CursorJob(id, SourceName(id), status, active, needsAttention, createdUtc.ToLocalTime(), duration,
            lastStep, Shorten(summary.Replace('\n', ' ').Replace('\r', ' '), 240));
    }

    public static string SourceName(string id) => id switch
    {
        _ when id.StartsWith("jarvischat-", StringComparison.Ordinal) => "Jarvis sohbet",
        _ when id.StartsWith("jarvis-", StringComparison.Ordinal) => "Jarvis görevi",
        _ when id.StartsWith("mobile-", StringComparison.Ordinal) => "Telefon",
        _ when id.StartsWith("telegram", StringComparison.Ordinal) => "Telegram",
        _ => "Diğer"
    };

    private static JsonElement? ReadJson(string? path)
    {
        if (path is null) return null;
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8));
            return document.RootElement.Clone();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string Shorten(string value, int length) => value.Length <= length ? value.Trim() : value[..length].Trim() + "…";
}
