using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Services;

namespace ArsanGazERP;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private SystemSupervisorService? _supervisor;
    private CollectionReminderMonitor? _collectionReminders;
    private CancellationTokenSource? _prospectAutoRunCancellation;
    private Task? _prospectAutoRunTask;
    private bool _restartRequested;

    internal void Restart()
    {
        _restartRequested = true;
        Shutdown();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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

        try
        {
            AutoUpdateResult update = await new AutoUpdateLaunchService().CheckAndStartLatestInstallerAsync();
            if (update.InstallerStarted)
            {
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
            _prospectAutoRunTask = new ProspectAutoRunner().RunIfDueSafeAsync(
                _prospectAutoRunCancellation.Token);
            _supervisor = new SystemSupervisorService();
            _supervisor.Start();
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Uygulama başlatılamadı:" + Environment.NewLine + exception.Message,
                "Arsan Gaz ERP",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        bool restartRequested = _restartRequested;
        if (_prospectAutoRunCancellation is not null)
        {
            await BackgroundWorkShutdown.CancelAndDrainAsync(
                _prospectAutoRunCancellation,
                _prospectAutoRunTask);
            _prospectAutoRunCancellation = null;
            _prospectAutoRunTask = null;
        }

        if (_supervisor is not null)
        {
            await _supervisor.StopAsync();
            _supervisor = null;
        }

        if (_collectionReminders is not null)
        {
            await _collectionReminders.DisposeAsync();
            _collectionReminders = null;
        }

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
