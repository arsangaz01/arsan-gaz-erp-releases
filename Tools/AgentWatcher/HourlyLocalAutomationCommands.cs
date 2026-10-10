namespace ArsanGazERP.AgentWatcher;

internal static class HourlyLocalAutomationCommands
{
    internal const string StartCommand = "/test-saatlik-baslat";
    internal const string StopCommand = "/test-saatlik-durdur";

    internal static string? TryHandle(
        string command,
        string argument,
        HourlyLocalTestAutomation automation)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(argument);
        ArgumentNullException.ThrowIfNull(automation);

        if (command is not (StartCommand or StopCommand))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(argument))
        {
            return $"Kullanım: {command} (parametre almaz).";
        }

        return command == StartCommand ? Start(automation) : Stop(automation);
    }

    private static string Start(HourlyLocalTestAutomation automation)
    {
        try
        {
            bool started = automation.EnableHourly();
            HourlyLocalAutomationStatus status = automation.Status;
            return started
                ? $"Saatlik yerel test modu kuruldu. İlk en erken çalışma: {status.NextRunUtc:O}. " +
                    "Sabit offline testler dışında işlem yoktur; kullanıcı kuyruğu önceliklidir."
                : $"Saatlik yerel test modu zaten etkin. Sonraki çalışma: {status.NextRunUtc:O}.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException or ObjectDisposedException)
        {
            return Failed(exception);
        }
    }

    private static string Stop(HourlyLocalTestAutomation automation)
    {
        try
        {
            automation.Stop();
            HourlyLocalAutomationStatus status = automation.Status;
            if (status.IsUnavailable)
            {
                return "Saatlik durum okunamıyor; fail-closed kapalı kaldı.";
            }

            return status.LastOutcome == "Stopping"
                ? "Saatlik mod durduruldu; çalışan sabit yerel test iptal ediliyor."
                : "Saatlik yerel test modu durduruldu; otomatik tekrar yapılmayacak.";
        }
        catch (Exception exception) when (
            exception is IOException or InvalidOperationException or UnauthorizedAccessException or AggregateException)
        {
            return Failed(exception);
        }
    }

    private static string Failed(Exception exception) =>
        "Saatlik yerel test komutu uygulanamadı; mod güvenli şekilde etkinleştirilmedi. "
        + exception.GetType().Name;
}
