using ArsanGazMobile.Pages;
using ArsanGazMobile.Services;

namespace ArsanGazMobile;

public partial class AppShell : Shell
{
    private readonly ShellContent _connection;

    public AppShell()
    {
        InitializeComponent();
        _connection = new ShellContent { Title = "Bağlantı", Content = new ConnectionPage() };
        Items.Add(new TabBar
        {
            Items =
            {
                new ShellContent { Title = "Ajan", Content = new ChatPage() },
                new ShellContent { Title = "Tüp", Content = new CylinderPage() },
                new ShellContent { Title = "CRM", Content = new CrmPage(followUps: false) },
                new ShellContent { Title = "Takip", Content = new CrmPage(followUps: true) },
                new ShellContent { Title = "Özet", Content = new SummaryPage() },
                _connection
            }
        });
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        try
        {
            await ServerUrlCoordinator.BootstrapAsync();
        }
        catch (ApiException)
        {
        }

        if (ApiClient.Instance.ServerUrl.Length == 0 || !await ApiClient.Instance.IsPairedAsync())
        {
            CurrentItem = _connection;
        }
    }
}
