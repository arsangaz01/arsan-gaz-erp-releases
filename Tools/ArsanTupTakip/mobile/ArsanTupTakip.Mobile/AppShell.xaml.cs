using ArsanTupTakip.Mobile.Pages;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile;

// Tabs follow the signed-in user's permissions; "Hesap" is always there.
public partial class AppShell : Shell
{
    public AppShell()
    {
        InitializeComponent();
        ApiClient api = ApiClient.Instance;
        TabBar tabs = new();
        if (api.CanAny([Perm.CylinderView, Perm.CylinderRegister, Perm.CylinderAudit, .. Perm.AnyMove]))
        {
            tabs.Items.Add(LazyTab("Tüp", () => new CylinderPage()));
        }

        if (api.Can(Perm.TripView))
        {
            tabs.Items.Add(LazyTab("Sefer", () => new CylinderTripPage()));
        }

        if (api.Can(Perm.VehicleView))
        {
            tabs.Items.Add(LazyTab("Araç", () => new VehiclePage()));
        }

        if (api.CanAny(Perm.Summary, Perm.Reports, Perm.MovementHistory))
        {
            tabs.Items.Add(LazyTab("Özet", () => new SummaryPage()));
        }

        tabs.Items.Add(LazyTab("Hesap", () => new AccountPage()));
        Items.Add(tabs);
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
        await ServerUrlCoordinator.RefreshIfDueAsync();
    }
}
