using System.Diagnostics;
using System.Security;
using System.Text;

internal static class AgentWatcherLog
{
    internal static void Write(string path, string level, string text)
    {
        try
        {
            File.AppendAllText(
                path,
                $"{DateTime.Now:O} [{level}] {text}{Environment.NewLine}",
                new UTF8Encoding(false));
        }
        catch (Exception exception) when (IsExpectedWriteFailure(exception))
        {
            ReportWriteFailure(level, exception);
        }
    }

    internal static bool IsExpectedWriteFailure(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or ArgumentException or
            NotSupportedException or SecurityException;

    internal static void ReportWriteFailure(string level, Exception exception) =>
        Trace.WriteLine(
            $"{DateTime.Now:O} [AGENTWATCHER_LOG_FAILURE] Could not write [{level}] to the AgentWatcher log ({exception.GetType().Name}).");
}
