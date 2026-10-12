using System.Text;

namespace ArsanTupTakip.Api;

// Data folder of the server: panel users, mobile sessions, audit log, APK. TUPTAKIP_DATA_DIR overrides the default.
internal static class AppPaths
{
    public static string Root { get; } = CreateRoot();

    public static string LogFile => Path.Combine(Root, "tuptakip-api.log");

    private static string CreateRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("TUPTAKIP_DATA_DIR");
        string root = !string.IsNullOrWhiteSpace(configured) ? configured.Trim()
            : OperatingSystem.IsWindows() ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanTupTakip")
            : "/var/lib/arsantuptakip";
        Directory.CreateDirectory(root);
        return root;
    }
}

internal static class AppLog
{
    private static readonly Lock Gate = new();

    public static void Write(string message)
    {
        string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}";
        if (!OperatingSystem.IsWindows())
        {
            Console.Out.Write(line);
        }

        lock (Gate)
        {
            try
            {
                FileInfo file = new(AppPaths.LogFile);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }

                File.AppendAllText(AppPaths.LogFile, line, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }
}
