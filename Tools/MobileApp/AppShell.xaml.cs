using ArsanGazMobile.Pages;
using ArsanGazMobile.Services;

namespace ArsanGazMobile;

public partial class AppShell : Shell
{
    private readonly ShellContent _connection;
    private readonly ShellContent _ajan;

    public AppShell()
    {
        InitializeComponent();
        _connection = new ShellContent
        {
            Title = "Bağlantı",
            ContentTemplate = new DataTemplate(typeof(ConnectionPage))
        };
        _ajan = LazyTab("Ajan", () => new ChatPage());
        Items.Add(new TabBar
        {
            Items =
            {
                _connection,
                _ajan,
                LazyTab("Tüp", () => new CylinderPage()),
                LazyTab("Sefer", () => new CylinderTripPage()),
                LazyTab("CRM", () => new CrmPage(followUps: false)),
                LazyTab("Takip", () => new CrmPage(followUps: true)),
                LazyTab("Kampanya", () => new CampaignPage()),
                LazyTab("Özet", () => new SummaryPage())
            }
        });
        CurrentItem = _connection;
    }

    private static ShellContent LazyTab(string title, Func<Page> create) =>
        new()
        {
            Title = title,
            ContentTemplate = new DataTemplate(() =>
            {
                try
                {
                    return create();
                }
                catch (Exception exception)
                {
                    CrashLog.Write(exception, title + " sekmesi");
                    return CrashLog.ErrorPage(title + " sekmesi açılamadı", exception.ToString());
                }
            })
        };

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

        bool ready = ApiClient.Instance.ServerUrl.Length > 0 && await ApiClient.Instance.IsPairedAsync();
        CurrentItem = ready ? _ajan : _connection;
    }
}
