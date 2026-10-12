using ArsanTupTakip.Mobile.Pages;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile;

public partial class AppShell : Shell
{
    private readonly ShellContent _connection;
    private readonly ShellContent _cylinder;

    public AppShell()
    {
        InitializeComponent();
        _connection = new ShellContent
        {
            Title = "Bağlantı",
            ContentTemplate = new DataTemplate(typeof(ConnectionPage))
        };
        _cylinder = LazyTab("Tüp", () => new CylinderPage());
        Items.Add(new TabBar
        {
            Items =
            {
                _connection,
                _cylinder,
                LazyTab("Sefer", () => new CylinderTripPage()),
                LazyTab("Araç", () => new VehiclePage()),
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
        CurrentItem = ready ? _cylinder : _connection;
    }
}
