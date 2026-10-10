using System.Text;

namespace ArsanGazERP.MobileApi;

internal static class MobileApiPaths
{
    public const int Port = 5077;

    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "MobileApi");

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
