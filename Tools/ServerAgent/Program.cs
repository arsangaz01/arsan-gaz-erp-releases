using System.Runtime.InteropServices;
using ArsanGazERP.ServerAgent;

if (args is ["--self-test"])
{
    // Windows ignores XDG_DATA_HOME, so the sandbox below would not isolate the real AgentWatcher data there.
    if (OperatingSystem.IsWindows())
    {
        Console.Error.WriteLine("--self-test yalnızca Linux sunucuda çalışır.");
        Environment.ExitCode = 2;
        return;
    }

    string sandbox = Path.Combine(Path.GetTempPath(), "arsangaz-serveragent-selftest-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(sandbox, "ArsanGazERP", "AgentWatcherData"));
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", sandbox);
    try
    {
        Environment.ExitCode = await SelfTest.RunAsync();
    }
    finally
    {
        Directory.Delete(sandbox, recursive: true);
    }

    return;
}

// Same data layout as MobileApi on Linux: LocalApplicationData resolves under $XDG_DATA_HOME, so ARSANGAZ_DATA_DIR
// points every path (AgentWatcherData outbox/inbox, MobileApi, CloudAgent) at the shared production data folder.
if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ARSANGAZ_DATA_DIR") is { Length: > 0 } dataDirectory)
{
    Environment.SetEnvironmentVariable("XDG_DATA_HOME", dataDirectory);
}

ServerAgentOptions options = ServerAgentOptions.FromEnvironment();
if (args is ["--check"])
{
    Console.WriteLine("Ayarlar: " + options.Describe());
    Console.WriteLine("Veri klasörü: " + ServerPaths.DataRoot);
    try
    {
        Console.WriteLine("ERP: " + ArsanGazERP.ErpMcpServer.ErpTools.Summary().Split('\n', 2)[0].Trim());
    }
    catch (Exception exception)
    {
        Console.WriteLine("ERP HATA: " + exception.GetBaseException().Message);
        Environment.ExitCode = 1;
    }

    return;
}

using CancellationTokenSource stopping = new();
using PosixSignalRegistration terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
{
    context.Cancel = true;
    stopping.Cancel();
});
Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    stopping.Cancel();
};

await new ServerAgentHost(options).RunAsync(stopping.Token);
