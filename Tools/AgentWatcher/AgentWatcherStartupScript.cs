using System.Text;

namespace ArsanGazERP.AgentWatcher;

internal static class AgentWatcherStartupScript
{
    internal static string Build(
        string watcherExecutable,
        string? erpExecutable,
        bool startErp,
        bool startMinimized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(watcherExecutable);

        StringBuilder script = new();
        script.AppendLine("@echo off");
        // Give VPN / network / Postgres time after Windows logon before launching services.
        script.AppendLine("timeout /t 20 /nobreak >nul");
        script.AppendLine($"set \"WATCHER_EXE={Path.GetFullPath(watcherExecutable)}\"");
        script.AppendLine("if not exist \"%WATCHER_EXE%\" exit /b 1");
        script.AppendLine("for %%I in (\"%WATCHER_EXE%\") do set \"WATCHER_DIR=%%~dpI\"");
        // cmd: a trailing backslash inside "…" escapes the closing quote → Windows shows \" "\ not found.
        script.AppendLine("if \"%WATCHER_DIR:~-1%\"==\"\\\" set \"WATCHER_DIR=%WATCHER_DIR:~0,-1%\"");
        script.AppendLine("start \"\" /D \"%WATCHER_DIR%\" \"%WATCHER_EXE%\" --background");

        if (startErp && !string.IsNullOrWhiteSpace(erpExecutable))
        {
            string fullErpPath = Path.GetFullPath(erpExecutable);
            script.AppendLine($"set \"ERP_EXE={fullErpPath}\"");
            script.AppendLine("if exist \"%ERP_EXE%\" (");
            // Extra wait so the database is more likely reachable when ERP opens at logon.
            script.AppendLine("  timeout /t 40 /nobreak >nul");
            script.AppendLine("  for %%I in (\"%ERP_EXE%\") do set \"ERP_DIR=%%~dpI\"");
            script.AppendLine("  if \"%ERP_DIR:~-1%\"==\"\\\" set \"ERP_DIR=%ERP_DIR:~0,-1%\"");
            // Title must come before /MIN /D (start ["title"] [/D path] [/MIN] command).
            if (startMinimized)
                script.AppendLine("  start \"\" /MIN /D \"%ERP_DIR%\" \"%ERP_EXE%\"");
            else
                script.AppendLine("  start \"\" /D \"%ERP_DIR%\" \"%ERP_EXE%\"");
            script.AppendLine(")");
        }

        return script.ToString();
    }
}
