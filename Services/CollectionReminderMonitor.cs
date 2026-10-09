using System.IO;
using System.Windows;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace ArsanGazERP.Services;

public sealed class CollectionReminderMonitor : IAsyncDisposable
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "Logs",
        "collection-reminders.log");

    private readonly CollectionReminderService _reminders = new();
    private readonly Dispatcher _dispatcher;
    private readonly CancellationTokenSource _closing = new();
    private Forms.NotifyIcon? _notifyIcon;
    private string _lastDigest = string.Empty;
    private Task? _loop;

    public CollectionReminderMonitor(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        _loop ??= Task.Run(() => RunAsync(_closing.Token));
    }

    public async ValueTask DisposeAsync()
    {
        _closing.Cancel();
        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _closing.Dispose();
        if (_dispatcher.CheckAccess())
        {
            DisposeNotifyIcon();
        }
        else if (!_dispatcher.HasShutdownStarted)
        {
            _ = _dispatcher.BeginInvoke(DisposeNotifyIcon);
        }
    }

    private void DisposeNotifyIcon()
    {
        if (_notifyIcon is null)
        {
            return;
        }

        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _notifyIcon = null;
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(20), cancellationToken);
        using PeriodicTimer timer = new(CheckInterval);
        do
        {
            try
            {
                CollectionReminderDigest? digest = await _reminders.PrepareDailyDigestAsync(DateTime.Today, cancellationToken);
                if (digest is not null)
                {
                    await DeliverAsync(digest);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                await LogAsync("Kontrol başarısız: " + exception.GetBaseException().Message);
            }
        }
        while (await timer.WaitForNextTickAsync(cancellationToken));
    }

    private async Task DeliverAsync(CollectionReminderDigest digest)
    {
        string telegramStatus;
        try
        {
            telegramStatus = CollectionReminderService.TryQueueTelegram(digest.Text, out string? detail)
                ? "Telegram'a iletilmek üzere AgentWatcher kuyruğuna eklendi."
                : "Telegram'a gönderilmedi: " + detail;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            telegramStatus = "Telegram kuyruğu yazılamadı: " + exception.Message;
        }

        await LogAsync($"{digest.Count} hatırlatma gösterildi. {telegramStatus}");
        await _dispatcher.InvokeAsync(() => ShowDesktopNotification(digest));
    }

    private void ShowDesktopNotification(CollectionReminderDigest digest)
    {
        _lastDigest = digest.Text;
        if (_notifyIcon is null)
        {
            _notifyIcon = new Forms.NotifyIcon
            {
                Icon = LoadApplicationIcon(),
                Text = "Arsan Gaz ERP · Tahsilat hatırlatması",
                Visible = true
            };
            _notifyIcon.BalloonTipClicked += (_, _) => ShowDigestWindow();
            _notifyIcon.DoubleClick += (_, _) => ShowDigestWindow();
        }

        string firstLines = string.Join(
            Environment.NewLine,
            digest.Text.Split(Environment.NewLine).Skip(1).Take(3));
        _notifyIcon.ShowBalloonTip(
            15000,
            $"{digest.Count} tahsilat hatırlatması",
            firstLines.Length > 0 ? firstLines : digest.Text,
            Forms.ToolTipIcon.Info);
    }

    private void ShowDigestWindow()
    {
        if (_lastDigest.Length == 0)
        {
            return;
        }

        MessageBox.Show(_lastDigest, "Tahsilat hatırlatması", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static System.Drawing.Icon LoadApplicationIcon()
    {
        try
        {
            if (Environment.ProcessPath is string path &&
                System.Drawing.Icon.ExtractAssociatedIcon(path) is System.Drawing.Icon icon)
            {
                return icon;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or IOException)
        {
        }

        return System.Drawing.SystemIcons.Information;
    }

    private static async Task LogAsync(string message)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            await File.AppendAllTextAsync(LogPath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {message}{Environment.NewLine}");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
        }
    }
}
