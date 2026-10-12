using System.Text;
using System.Text.RegularExpressions;

namespace ArsanGazERP.ServerAgent;

// Output of /usr/local/sbin/arsangaz-release: user-facing Turkish text plus "@@KEY=VALUE" trailer lines.
internal sealed record ReleaseResult(int? ExitCode, string Text, IReadOnlyDictionary<string, string> Values)
{
    public bool Finished => ExitCode is not null;

    public bool Succeeded => ExitCode == 0;

    public string Value(string key) => Values.TryGetValue(key, out string? value) ? value : string.Empty;

    public static ReleaseResult Parse(int? exitCode, string output)
    {
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        StringBuilder text = new();
        foreach (string line in output.Replace("\r", string.Empty).Split('\n'))
        {
            if (line.StartsWith("@@", StringComparison.Ordinal) && line.IndexOf('=') is > 2 and int separator)
            {
                values[line[2..separator]] = line[(separator + 1)..].Trim();
            }
            else
            {
                text.AppendLine(line);
            }
        }

        return new ReleaseResult(exitCode, text.ToString().Trim(), values);
    }
}

// The server agent (user arsangaz) never runs the release script itself: it drops a whitelisted request into the spool
// folder; arsangaz-release.path starts the root runner, which writes <id>.out and <id>.done back.
internal sealed partial class ReleaseClient(string root)
{
    public static ReleaseClient Default { get; } = new(ServerPaths.ReleaseSpool);

    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "pending", "publish", "apk", "rollback", "status" };

    public string Root => root;

    public async Task<ReleaseResult> RunAsync(string action, string arg1, string arg2, TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (!Actions.Contains(action) || !HexArgument().IsMatch(arg1) || arg2 is not ("" or "force"))
        {
            throw new ArgumentException("Geçersiz yayın isteği.");
        }

        string requests = Path.Combine(root, "requests");
        string results = Path.Combine(root, "results");
        if (!Directory.Exists(requests) || !Directory.Exists(results))
        {
            return new ReleaseResult(-1, "Yayın sistemi sunucuda kurulu değil (install-release.sh).", new Dictionary<string, string>());
        }

        string id = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{action}-{Guid.NewGuid().ToString("N")[..8]}";
        string temporary = Path.Combine(requests, id + ".tmp");
        await File.WriteAllTextAsync(temporary, $"action={action}\narg1={arg1}\narg2={arg2}\n", new UTF8Encoding(false), cancellationToken);
        File.Move(temporary, Path.Combine(requests, id + ".req"));
        Log.Write("RELEASE", $"İstek gönderildi: {id}");

        string done = Path.Combine(results, id + ".done");
        string output = Path.Combine(results, id + ".out");
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!File.Exists(done))
        {
            if (DateTime.UtcNow > deadline)
            {
                return new ReleaseResult(null, "Yayın sistemi henüz yanıt vermedi (başka bir iş sürüyor olabilir).", new Dictionary<string, string>());
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        int exitCode = int.TryParse((await File.ReadAllTextAsync(done, cancellationToken)).Trim(), out int parsed) ? parsed : -1;
        string text = File.Exists(output) ? await File.ReadAllTextAsync(output, cancellationToken) : string.Empty;
        TryDelete(done);
        TryDelete(output);
        Log.Write("RELEASE", $"Sonuç alındı: {id} (çıkış {exitCode})");
        return ReleaseResult.Parse(exitCode, text);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }

    [GeneratedRegex("^[0-9a-f]{0,40}$")]
    private static partial Regex HexArgument();
}
