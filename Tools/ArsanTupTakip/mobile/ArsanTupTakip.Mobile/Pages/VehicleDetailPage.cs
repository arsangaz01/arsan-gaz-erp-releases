using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// One vehicle: details, cylinders on board grouped by gas type, active trip and trip history.
public sealed class VehicleDetailPage : ContentPage
{
    private readonly int _id;
    private readonly Label _info = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _warning = new() { FontSize = 14, TextColor = Colors.Red, IsVisible = false, LineBreakMode = LineBreakMode.WordWrap };
    private readonly ContentView _groups = new();
    private readonly VerticalStackLayout _trips = new() { Spacing = 8 };
    private readonly Button _toggle;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private VehicleItem? _vehicle;

    public VehicleDetailPage(int id)
    {
        _id = id;
        Title = "Araç";
        Ui.ApplyPageChrome(this);
        _toggle = Ui.Secondary("Pasife al", async (_, _) => await ToggleAsync());
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Section("Araç bilgileri", _info, _warning,
                        Ui.ActionRow(Ui.Secondary("Düzenle", async (_, _) => await EditAsync()), _toggle)),
                    Ui.Section("Araçtaki tüpler (gaz türüne göre)", _groups),
                    Ui.Section("Sefer geçmişi (son 30)", _trips),
                    _busy
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await LoadAsync();
    }

    private async Task LoadAsync()
    {
        await Ui.RunAsync(this, _busy, async () =>
        {
            VehicleDetailResponse detail = await ApiClient.Instance.VehicleDetailAsync(_id);
            VehicleItem v = detail.Vehicle;
            _vehicle = v;
            Title = v.Plate;
            List<string> lines =
            [
                $"Plaka: {v.Plate}" + (v.IsActive ? string.Empty : " (pasif)"),
                $"Araç adı: {v.Name ?? "-"} · Tür: {v.VehicleType ?? "-"}",
                $"Şoför: {v.DriverName ?? "-"}",
                $"Kapasite: {(v.CylinderCapacity is int max ? max + " tüp" : "-")} · Araçta: {v.CylindersOnBoard} tüp",
                "Aktif sefer: " + (v.ActiveTripTitle ?? "yok"),
                $"Kayıt: {v.Created}"
            ];
            if (!string.IsNullOrWhiteSpace(v.Note))
            {
                lines.Add("Not: " + v.Note);
            }

            _info.Text = string.Join(Environment.NewLine, lines);
            _warning.Text = detail.CapacityWarning ?? string.Empty;
            _warning.IsVisible = detail.CapacityWarning is not null;
            _toggle.Text = v.IsActive ? "Pasife al" : "Aktif et";
            _groups.Content = CylinderGroupPage.GroupList(Navigation, detail.OnBoard, "Bu araçta şu an tüp yok.", v.Plate);

            _trips.Children.Clear();
            if (detail.Trips.Count == 0)
            {
                _trips.Children.Add(Ui.Note("Bu araç henüz sefere çıkmamış."));
            }

            foreach (VehicleTrip trip in detail.Trips)
            {
                string state = trip.Active ? (trip.VehicleReturned is null ? "seferde" : "döndü " + trip.VehicleReturned) : "kapandı " + trip.Closed;
                Label line = Ui.Note($"{trip.Title} · {state}\nyüklenen {trip.Loaded} · teslim {trip.Delivered} · depoya dönen {trip.Returned}"
                    + (trip.Missing > 0 ? $" · ⚠ {trip.Missing} eksik" : string.Empty));
                if (trip.Missing > 0)
                {
                    line.TextColor = Colors.Red;
                }

                _trips.Children.Add(line);
            }
        });
    }

    private async Task EditAsync()
    {
        if (_vehicle is not null)
        {
            await Navigation.PushAsync(new VehicleEditPage(_vehicle));
        }
    }

    private async Task ToggleAsync()
    {
        if (_vehicle is not VehicleItem v)
        {
            return;
        }

        if (v.IsActive && !await DisplayAlertAsync("Pasife al", $"{v.Plate} pasife alınsın mı? Yeni seferlerde seçilemez; geçmişi silinmez, tekrar aktif edilebilir.", "Pasife al", "Vazgeç"))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            VehicleSaved saved = await ApiClient.Instance.SaveVehicleAsync(v.Id,
                new VehicleForm(v.Plate, v.Name, v.VehicleType, v.DriverName, v.CylinderCapacity, v.Note, !v.IsActive));
            await DisplayAlertAsync("Araç", saved.Message, "Tamam");
        });
        await LoadAsync();
    }
}
