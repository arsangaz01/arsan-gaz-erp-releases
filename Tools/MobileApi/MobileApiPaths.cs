using System.Text;

namespace ArsanGazERP.MobileApi;

internal static class MobileApiPaths
{
    // ARSANGAZ_MOBILEAPI_PORT lets the server's release test instance (arsangaz-mobileapi-staging) run beside production.
    public static int Port { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("ARSANGAZ_MOBILEAPI_PORT"), out int port) && port is > 1024 and < 65536
            ? port
            : 5077;

    // ARSANGAZ_MOBILEAPI_DIR is used by --self-test so it never touches the real users, devices or logs.
    public static string Root { get; } = Environment.GetEnvironmentVariable("ARSANGAZ_MOBILEAPI_DIR") is { Length: > 0 } custom
        ? custom
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "MobileApi");

    public static string DevicesFile => Path.Combine(Root, "devices.json");
    public static string TunnelUrlFile => Path.Combine(Root, "tunnel-url.txt");
    public static string LogFile => Path.Combine(Root, "mobile-api.log");
    public static string CloudflaredExe => Path.Combine(Root, "cloudflared.exe");
    public static string ApkFile => Path.Combine(Root, "ArsanGazERP-Mobil.apk");

    public static void Ensure() => Directory.CreateDirectory(Root);
}

internal static class MobileLog
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
                FileInfo file = new(MobileApiPaths.LogFile);
                if (file.Exists && file.Length > 2 * 1024 * 1024)
                {
                    File.Move(file.FullName, file.FullName + ".old", overwrite: true);
                }

                File.AppendAllText(MobileApiPaths.LogFile, line, Encoding.UTF8);
            }
            catch (IOException)
            {
            }
        }
    }
}
