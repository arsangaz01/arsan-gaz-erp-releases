using System.Collections.Concurrent;
using System.Text;

namespace ArsanTupTakip.Api;

// Messages for the managers. The API never talks to Telegram itself: it drops a text file into the notice box of
// @TuptakipArsanbot (TUPTAKIP_NOTICE_DIR, /var/lib/arsantuptakip-queue/notices on the server), which sends it to its
// single allowed chat and deletes it. Without the folder the notice is only logged; the panel's loss control list
// still shows everything. Never used for customers.
internal static class ManagerNotices
{
    internal const string DirectoryVariable = "TUPTAKIP_NOTICE_DIR";

    // Tests keep the texts here instead of writing files.
    internal static bool Capture { get; set; }

    internal static ConcurrentQueue<string> Captured { get; } = new();

    internal static bool Send(string name, string text)
    {
        if (Capture)
        {
            Captured.Enqueue(text);
            return true;
        }

        string? directory = Environment.GetEnvironmentVariable(DirectoryVariable)?.Trim();
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            AppLog.Write($"Yönetici bildirimi gönderilemedi ({name}): bildirim kutusu ({DirectoryVariable}) tanımlı değil veya yok.");
            return false;
        }

        // The bot only picks up names matching ^[a-z0-9][a-z0-9-]{0,80}\.txt$; the temporary dot file is ignored.
        string file = $"{name}-{DateTime.Now:yyyyMMddHHmmss}-{Guid.NewGuid().ToString("N")[..6]}.txt";
        string target = Path.Combine(directory, file);
        string temporary = Path.Combine(directory, "." + file + ".tmp");
        try
        {
            File.WriteAllText(temporary, text, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows())
            {
                // The service runs with UMask 0077; the bot reads the file through the shared group.
                File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite);
            }

            File.Move(temporary, target);
            AppLog.Write($"Yönetici bildirimi bildirim kutusuna yazıldı: {file}");
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Yönetici bildirimi yazılamadı ({name}): {exception.Message}");
            try
            {
                File.Delete(temporary);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
            }

            return false;
        }
    }
}
