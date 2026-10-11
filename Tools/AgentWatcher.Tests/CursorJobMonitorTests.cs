using ArsanGazERP.Services;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class CursorJobMonitorTests
{
    internal static void Run()
    {
        string inbox = Path.Combine(Path.GetTempPath(), "AgentWatcher.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(inbox);
        try
        {
            DateTime now = DateTime.UtcNow;
            Write(inbox, "jarvischat-1.request.json", "{}");
            Write(inbox, "jarvischat-1.ack.json", "{\"status\":\"submitted\"}");
            Write(inbox, "jarvischat-1.progress.json", "{\"stage\":\"working\",\"steps\":[\"🔎 Arıyor\",\"📄 Okuyor: a.cs\"]}");
            Write(inbox, "jarvis-2.dispatch.json", "{}");
            Write(inbox, "jarvis-2.completion.json", "{\"status\":\"completed\",\"report\":\"Bitti\"}");
            Write(inbox, "mobile-3.ack.json", "{\"status\":\"failed\",\"error\":\"Hata oldu\"}");
            Write(inbox, "telegram-direct-4.request.json", "{}");
            Write(inbox, "telegram-direct-4.ide-claim.json", "{}");
            Write(inbox, "jarvischat-5.dispatch.json", "{}");
            Write(inbox, "bridge-status.json", "{}");

            Dictionary<string, CursorJob> jobs = CursorJobMonitor.Read(inbox, now).ToDictionary(job => job.Id);
            Check(jobs.Count == 5, "bridge-status.json must not be listed as a job.");
            Check(jobs["jarvischat-1"] is { Status: "Çalışıyor", IsActive: true, LastStep: "📄 Okuyor: a.cs", Source: "Jarvis sohbet" },
                "A submitted request with progress must be running and show its last step.");
            Check(jobs["jarvis-2"] is { Status: "Tamamlandı", IsActive: false, Summary: "Bitti" }, "A completion must close the job.");
            Check(jobs["mobile-3"] is { Status: "Başarısız", NeedsAttention: true, Source: "Telefon" }, "A failed ack must need attention.");
            Check(jobs["telegram-direct-4"].Status == "IDE sohbetinde", "IDE-claimed requests must be labelled.");
            Check(jobs["jarvischat-5"] is { Status: "Belirsiz", IsActive: false }, "A lone dispatch marker must not look active.");

            Check(!CursorJobMonitor.RequestCancel(inbox, "jarvis-2"), "Finished jobs cannot be cancelled.");
            Check(!CursorJobMonitor.RequestCancel(inbox, "telegram-direct-4"), "IDE-chat jobs cannot be cancelled by marker.");
            Check(!CursorJobMonitor.RequestCancel(inbox, "..\\settings"), "Unsafe ids must be rejected.");
            Check(CursorJobMonitor.CancelAllActive(inbox, now) == 1 && File.Exists(Path.Combine(inbox, "jarvischat-1.cancel.json")),
                "Emergency stop must mark every bridge-run active job.");
            Check(CursorJobMonitor.Read(inbox, now).Single(job => job.Id == "jarvischat-1").Status == "Durduruluyor",
                "A pending cancel marker must be visible.");

            Check(CursorJobMonitor.Read(inbox, now.AddMinutes(11)).Single(job => job.Id == "jarvischat-1").NeedsAttention,
                "A job running longer than the threshold must need attention.");
        }
        finally
        {
            Directory.Delete(inbox, recursive: true);
        }

        Console.WriteLine("PASS: Cursor job monitor status, attention flags and emergency stop markers (offline).");
    }

    private static void Write(string inbox, string name, string json) => File.WriteAllText(Path.Combine(inbox, name), json);

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
