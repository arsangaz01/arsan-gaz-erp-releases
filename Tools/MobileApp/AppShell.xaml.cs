using ArsanGazMobile.Pages;
using ArsanGazMobile.Services;

namespace ArsanGazMobile;

// Tabs follow the signed-in user's permissions; the server enforces the same rules on every request.
public partial class AppShell : Shell
{
    private readonly ShellContent? _start;

    public AppShell(UserSession session)
    {
        InitializeComponent();
        ShellContent connection = new()
        {
            Title = "Bağlantı",
            ContentTemplate = new DataTemplate(typeof(ConnectionPage))
        };
        List<ShellContent> tabs = [connection];
        void Add(string title, string permission, Func<Page> create)
        {
            if (session.Can(permission))
            {
                tabs.Add(LazyTab(title, create));
            }
        }

        Add("Tüp", "tup-goruntule", () => new CylinderPage());
        Add("Sefer", "sefer-goruntule", () => new CylinderTripPage());
        Add("Ajan", "asistan|cursor-gorev", () => new ChatPage());
        Add("Araç", "arac-goruntule", () => new VehiclePage());
        Add("CRM", "crm-goruntule", () => new CrmPage(followUps: false));
        Add("Takip", "crm-goruntule", () => new CrmPage(followUps: true));
        Add("Kampanya", "kampanya-goruntule", () => new CampaignPage());
        Add("Özet", "ozet|rapor|hareket-goruntule", () => new SummaryPage());

        TabBar bar = new();
        foreach (ShellContent tab in tabs)
        {
            bar.Items.Add(tab);
        }

        Items.Add(bar);
        _start = tabs.Count > 1 ? tabs[1] : connection;
        CurrentItem = _start;
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
            await ApiClient.Instance.RefreshSessionAsync();
        }
        catch (ApiException)
        {
        }
    }
}
