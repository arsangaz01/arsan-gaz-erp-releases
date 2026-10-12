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

// --command "/yayin": runs one Telegram command through the same handler (operator test over SSH as user arsangaz);
// replies print here, background release results go to the Telegram outbox like a real command.
if (args is ["--command", { Length: > 0 } commandText])
{
    if (!commandText.StartsWith('/'))
    {
        Console.Error.WriteLine("Yalnızca /komut çalıştırılabilir (düz metin Cursor kuyruğuna gider).");
        Environment.ExitCode = 2;
        return;
    }

    ServerPaths.Ensure();
    ArsanGazERP.AgentWatcher.TelegramCopilotInbox commandInbox = new(ServerPaths.CopilotInbox, ServerPaths.TelegramOutbox);
    ServerCommands commands = new(commandInbox, () => 0, notify: text =>
    {
        Console.WriteLine("[Telegram'a gönderilen]\n" + text);
        return ArsanGazERP.Services.TelegramOutbox.TryQueue(text);
    });
    Console.WriteLine(await commands.HandleAsync(0, commandText, options.ChatId, CancellationToken.None));
    if (commands.BackgroundRelease is { } background)
    {
        await background;
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
