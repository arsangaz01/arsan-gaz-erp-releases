using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// "Sefer Başlat": pick one or more registered active vehicles and start the trip; returns the started trip.
public sealed class TripStartPage : ContentPage
{
    private readonly TaskCompletionSource<(TripStartResponse Trip, string? FirstPlate)?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly VerticalStackLayout _vehicles = new() { Spacing = 8 };
    private readonly Entry _note = Ui.TextField("Not (isteğe bağlı)", maxLength: 120);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<(CheckBox Box, VehicleItem Vehicle)> _choices = [];
    private bool _done;
    private bool _addingVehicle;

    private TripStartPage()
    {
        Title = "Sefer Başlat";
        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Sefere çıkacak araçları işaretleyin. Listede olmayan araç için önce \"Araç\" sekmesinden araç ekleyin. Sefer başlayınca tüpleri araca yükleme okutmasına geçilir."),
                    Ui.Section("Araçlar", _vehicles),
                    Ui.Section("Not", _note),
                    Ui.Primary("🚚 Seferi başlat", async (_, _) => await StartAsync()),
                    Ui.Allowed(Ui.Secondary("+ Yeni araç ekle", async (_, _) =>
                    {
                        _addingVehicle = true;
                        await Navigation.PushAsync(new VehicleEditPage(null));
                    }), "arac-yonetimi"),
                    _busy
                }
            }
        };
    }

    public static async Task<(TripStartResponse Trip, string? FirstPlate)?> OpenAsync(INavigation navigation)
    {
        TripStartPage page = new();
        await navigation.PushAsync(page);
        return await page._result.Task;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _addingVehicle = false;
        await LoadAsync();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_done && !_addingVehicle)
        {
            _result.TrySetResult(null);
        }
    }

    private async Task LoadAsync()
    {
        HashSet<string> chosen = _choices.Where(c => c.Box.IsChecked).Select(c => c.Vehicle.Plate).ToHashSet();
        await Ui.RunAsync(this, _busy, async () =>
        {
            List<VehicleItem> vehicles = await ApiClient.Instance.VehiclesAsync(includeInactive: false);
            _choices.Clear();
            _vehicles.Children.Clear();
            if (vehicles.Count == 0)
            {
                _vehicles.Children.Add(Ui.Note("Kayıtlı aktif araç yok. \"+ Yeni araç ekle\" ile ekleyin."));
                return;
            }

            foreach (VehicleItem vehicle in vehicles)
            {
                bool busy = vehicle.ActiveTripId is not null;
                CheckBox box = new() { Color = Ui.Accent, IsChecked = chosen.Contains(vehicle.Plate), IsEnabled = !busy, VerticalOptions = LayoutOptions.Center };
                Label text = new()
                {
                    Text = vehicle.Plate + (vehicle.Caption.Length == 0 ? string.Empty : " · " + vehicle.Caption)
                        + Environment.NewLine + (busy ? "Şu an seferde: " + vehicle.ActiveTripTitle : $"Üzerinde {vehicle.CylindersOnBoard} tüp"),
                    FontSize = 15,
                    LineBreakMode = LineBreakMode.WordWrap,
                    VerticalTextAlignment = TextAlignment.Center,
                    Opacity = busy ? 0.55 : 1
                };
                Grid row = new() { ColumnSpacing = 8, ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star)] };
                row.Add(box, 0, 0);
                row.Add(text, 1, 0);
                TapGestureRecognizer tap = new();
                tap.Tapped += (_, _) => { if (box.IsEnabled) box.IsChecked = !box.IsChecked; };
                text.GestureRecognizers.Add(tap);
                _vehicles.Children.Add(row);
                _choices.Add((box, vehicle));
            }
        });
    }

    private async Task StartAsync()
    {
        List<string> plates = _choices.Where(c => c.Box.IsChecked).Select(c => c.Vehicle.Plate).ToList();
        if (plates.Count == 0)
        {
            await DisplayAlertAsync("Sefer", "En az bir araç seçin.", "Tamam");
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            TripStartResponse started = await ApiClient.Instance.StartTripAsync(Value(_note), plates);
            _done = true;
            await Navigation.PopAsync();
            _result.TrySetResult((started, plates[0]));
        });
    }

    private static string? Value(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();
}
