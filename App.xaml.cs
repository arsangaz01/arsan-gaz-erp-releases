using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ArsanGazERP.Services;

namespace ArsanGazERP;

public partial class App : Application
{
    private static readonly TimeSpan BackgroundShutdownTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ErrorDialogInterval = TimeSpan.FromSeconds(30);
    private static readonly string ErrorLogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "Logs",
        "desktop-errors.log");
    private static readonly object ErrorLogLock = new();

    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private SystemSupervisorService? _supervisor;
    private CollectionReminderMonitor? _collectionReminders;
    private CancellationTokenSource? _prospectAutoRunCancellation;
    private Task? _prospectAutoRunTask;
    private bool _restartRequested;
    private DateTime _lastErrorDialogUtc = DateTime.MinValue;

    internal void Restart()
    {
        _restartRequested = true;
        Shutdown();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        WindowThemeService.Register();
        _singleInstanceMutex = new Mutex(true, @"Local\ArsanGazERP.SingleInstance.V1", out bool isFirstInstance);
        _ownsSingleInstanceMutex = isFirstInstance;
        if (!isFirstInstance)
        {
            MessageBox.Show(
                "Arsan Gaz ERP bu Windows oturumunda zaten açık.",
                "Arsan Gaz ERP",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;
        }

        SplashScreen splash = new("Assets/ArsanGazMark.png");
        splash.Show(autoClose: false, topMost: false);
        try
        {
            AutoUpdateResult update = await new AutoUpdateLaunchService().CheckAndStartLatestInstallerAsync();
            if (update.InstallerStarted)
            {
                splash.Close(TimeSpan.Zero);
                Shutdown();
                return;
            }

            DatabaseService databaseService = new();
            await databaseService.InitializeAsync();
            await CylinderSchemaService.EnsureAsync();
            await new CrmSchemaService().EnsureAsync();
            await CollectionReminderService.EnsureSchemaAsync();
            _ = await new MojibakeRepairService().RepairAsync();
            _collectionReminders = new CollectionReminderMonitor(Dispatcher);
            _collectionReminders.Start();
            _ = Task.Run(() =>
            {
                try
                {
                    DesktopShortcutService.EnsureCurrentShortcuts(AppContext.BaseDirectory);
                }
                catch (Exception exception)
                {
                    Debug.WriteLine("Masaüstü kısayolları güncellenemedi: " + exception.Message);
                }
            });
            _prospectAutoRunCancellation = new CancellationTokenSource();
            CancellationToken prospectToken = _prospectAutoRunCancellation.Token;
            _prospectAutoRunTask = Task.Run(() => new ProspectAutoRunner().RunIfDueSafeAsync(prospectToken), prospectToken);
            _supervisor = new SystemSupervisorService();
            _supervisor.Start();

            // The main window queries the database as soon as it loads, so it must not exist before the schema is ready.
            ArsanGazERP.MainWindow mainWindow = new();
            MainWindow = mainWindow;
            mainWindow.Show();
            splash.Close(TimeSpan.FromMilliseconds(200));
        }
        catch (Exception exception)
        {
            splash.Close(TimeSpan.Zero);
            LogError("Başlatma", exception);
            MessageBox.Show(
                "Uygulama başlatılamadı:" + Environment.NewLine + exception.Message,
                "Arsan Gaz ERP",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        bool restartRequested = _restartRequested;
        StopBackgroundWork();

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        _singleInstanceMutex = null;
        base.OnExit(e);

        if (restartRequested)
        {
            RestartCurrentProcess();
        }
    }

    // OnExit must finish synchronously: the process ends as soon as it returns, so awaited work after the first
    // await would never run. The shutdown work runs on the thread pool because blocking the UI thread on
    // continuations captured on it would deadlock.
    private void StopBackgroundWork()
    {
        CancellationTokenSource? prospectCancellation = _prospectAutoRunCancellation;
        Task? prospectTask = _prospectAutoRunTask;
        SystemSupervisorService? supervisor = _supervisor;
        _prospectAutoRunCancellation = null;
        _prospectAutoRunTask = null;
        _supervisor = null;

        Stopwatch elapsed = Stopwatch.StartNew();
        Task shutdown = Task.Run(async () =>
        {
            if (prospectCancellation is not null)
            {
                await BackgroundWorkShutdown.CancelAndDrainAsync(prospectCancellation, prospectTask);
            }

            if (supervisor is not null)
            {
                await supervisor.StopAsync();
            }
        });
        try
        {
            if (!shutdown.Wait(BackgroundShutdownTimeout))
            {
                LogMessage("Kapanış", "Arka plan işleri süresinde durmadı; uygulama yine de kapatılıyor.");
            }
        }
        catch (AggregateException exception)
        {
            LogError("Kapanış", exception.GetBaseException());
        }

        if (_collectionReminders is not null)
        {
            TimeSpan remaining = BackgroundShutdownTimeout - elapsed.Elapsed;
            _collectionReminders.Stop(remaining > TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1));
            _collectionReminders = null;
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        LogError("Arayüz", e.Exception);
        e.Handled = true;
        DateTime now = DateTime.UtcNow;
        if (now - _lastErrorDialogUtc < ErrorDialogInterval)
        {
            return;
        }

        _lastErrorDialogUtc = now;
        MessageBox.Show(
            "Beklenmeyen bir hata oluştu; işlem tamamlanamadı ancak uygulama açık kalmaya devam ediyor."
                + Environment.NewLine + Environment.NewLine + e.Exception.GetBaseException().Message,
            "Arsan Gaz ERP",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            LogError("Kritik", exception);
        }
    }

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        LogError("Arka plan görevi", e.Exception.GetBaseException());
        e.SetObserved();
    }

    private static void LogError(string source, Exception exception) => LogMessage(source, exception.ToString());

    private static void LogMessage(string source, string message)
    {
        try
        {
            lock (ErrorLogLock)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
                File.AppendAllText(
                    ErrorLogPath,
                    $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} | {source} | {message}{Environment.NewLine}");
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine("Hata günlüğü yazılamadı: " + exception.Message);
        }
    }

    private static void RestartCurrentProcess()
    {
        try
        {
            string executablePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("Uygulama yürütülebilir dosyasının yolu alınamadı.");
            ProcessStartInfo startInfo = new(executablePath)
            {
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = true
            };
            foreach (string argument in Environment.GetCommandLineArgs().Skip(1))
            {
                startInfo.ArgumentList.Add(argument);
            }

            Process? restartedProcess = Process.Start(startInfo);
            restartedProcess?.Dispose();
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or
                System.IO.FileNotFoundException or
                UnauthorizedAccessException or
                InvalidOperationException)
        {
            Environment.ExitCode = 1;
            MessageBox.Show(
                "Arsan Gaz ERP yeniden başlatılamadı:" + Environment.NewLine + exception.GetBaseException().Message,
                "Arsan Gaz ERP",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
}
