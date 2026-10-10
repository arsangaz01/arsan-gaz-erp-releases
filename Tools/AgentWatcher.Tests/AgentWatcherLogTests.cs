using System.Diagnostics;
using System.Text;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class AgentWatcherLogTests
{
    public static void Run()
    {
        WriteFailureIsReportedWithoutExposingLogText();
        SuccessfulWritePreservesLogFormat();
    }

    private static void WriteFailureIsReportedWithoutExposingLogText()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"watcher-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        CapturingTraceListener listener = new();
        Trace.Listeners.Add(listener);

        try
        {
            AgentWatcherLog.Write(directory, "ERROR", "private message body");
        }
        finally
        {
            Trace.Listeners.Remove(listener);
            listener.Dispose();
            Directory.Delete(directory);
        }

        string trace = listener.Content;
        if (!trace.Contains("AGENTWATCHER_LOG_FAILURE", StringComparison.Ordinal) ||
            !trace.Contains("ERROR", StringComparison.Ordinal) ||
            trace.Contains("private message body", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Log write failure diagnostics must identify the failure without exposing the log message.");
        }
    }

    private static void SuccessfulWritePreservesLogFormat()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"watcher-log-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "watcher.log");

        try
        {
            AgentWatcherLog.Write(path, "INFO", "healthy");
            string entry = File.ReadAllText(path);
            if (!entry.Contains(" [INFO] healthy", StringComparison.Ordinal) ||
                !entry.EndsWith(Environment.NewLine, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Successful log writes must retain the existing entry format.");
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class CapturingTraceListener : TraceListener
    {
        private readonly StringBuilder content = new();

        public string Content => content.ToString();

        public override void Write(string? message) => content.Append(message);

        public override void WriteLine(string? message) => content.AppendLine(message);
    }
}
