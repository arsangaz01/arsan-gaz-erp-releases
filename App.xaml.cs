using System;
using System.Threading.Tasks;
using System.Windows;
using ArsanGazERP.Services;

namespace ArsanGazERP;

public partial class App : Application
{
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
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
            await new CrmSchemaService().EnsureAsync();
            _ = await new MojibakeRepairService().RepairAsync();
            _ = Task.Run(async () => await new ProspectAutoRunner().RunIfDueSafeAsync());
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
}
