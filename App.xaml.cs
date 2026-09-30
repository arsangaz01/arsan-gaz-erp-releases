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
            DatabaseService databaseService = new();
            await databaseService.InitializeAsync();
            await new TextEncodingRepairService().RepairDatabaseAsync();
            _ = Task.Run(async () => await new ProspectAutoRunner().RunIfDueSafeAsync());
        }
        catch (Exception exception)
        {
            MessageBox.Show(
                "Veritabanı başlatılamadı:" + Environment.NewLine + exception.Message,
                "Arsan Gaz ERP",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }
}