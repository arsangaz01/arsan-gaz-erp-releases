using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Cylinders of a trip in one state ("Araçta", "Müşteriye teslim edildi"), grouped by gas type; a group opens the cylinder details.
public sealed class TripCylindersPage : ContentPage
{
    public const string OnVehicle = "Araçta";
    public const string Delivered = "Müşteriye teslim edildi";

    private readonly VerticalStackLayout _body = new() { Spacing = 12 };

    private TripCylindersPage(string title)
    {
        Title = title;
        Ui.ApplyPageChrome(this);
        _body.Children.Add(new ActivityIndicator { IsRunning = true, Color = Ui.Accent });
        Content = new ScrollView { Content = new VerticalStackLayout { Padding = new Thickness(16, 12, 16, 24), Children = { _body } } };
    }

    public static async Task OpenAsync(INavigation navigation, int tripId, string? plate, string state, string title, string icon, Color color)
    {
        TripCylindersPage page = new(title);
        await navigation.PushAsync(page);
        await page.LoadAsync(tripId, plate, state, title, icon, color);
    }

    private async Task LoadAsync(int tripId, string? plate, string state, string title, string icon, Color color)
    {
        View content;
        try
        {
            TripLoadView load = await ApiClient.Instance.TripLoadAsync(tripId, plate);
            List<GasGroup> groups = load.Groups
                .Select(g => Filter(g, state))
                .Where(g => g.Count > 0)
                .ToList();
            int total = groups.Sum(g => g.Count);
            int full = groups.Sum(g => g.Full);
            int empty = groups.Sum(g => g.Empty);
            content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    state == Delivered
                        ? (View)Ui.StatTile(icon, total.ToString(), title, color, load.Title)
                        : Ui.TileGrid(
                        [
                            Ui.StatTile(icon, total.ToString(), title, color, plate is null ? load.Title : $"{load.Title} · {plate}"),
                            Ui.StatTile("🟢", $"{full} / {empty}", "Dolu / boş", Ui.Accent)
                        ]),
                    CylinderGroupPage.GroupList(Navigation, groups, "Bu listede tüp yok.", title)
                }
            };
        }
        catch (Exception exception)
        {
            content = Ui.Note("Liste alınamadı: " + exception.Message);
        }

        _body.Children.Clear();
        _body.Children.Add(content);
    }

    private static GasGroup Filter(GasGroup group, string state)
    {
        List<LoadCylinder> cylinders = group.Cylinders.Where(c => c.State == state).ToList();
        int full = cylinders.Count(c => c.Fullness == "Dolu");
        int empty = cylinders.Count(c => c.Fullness == "Boş");
        string summary = $"{cylinders.Count} tüp" + (full + empty > 0 ? $" · dolu {full} · boş {empty}" : string.Empty);
        return new GasGroup(group.GasType, cylinders.Count, full, empty, 0, summary, cylinders);
    }
}
