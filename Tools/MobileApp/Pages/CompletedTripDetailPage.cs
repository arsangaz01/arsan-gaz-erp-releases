using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Read-only trip summary for re-checking after the trip was closed.
public sealed class CompletedTripDetailPage : ContentPage
{
    private readonly int _tripId;
    private readonly Label _head = new() { FontSize = 18, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _meta = Ui.Note(string.Empty);
    private readonly Label _counts = new() { FontSize = 14, LineBreakMode = LineBreakMode.WordWrap };
    private readonly VerticalStackLayout _cylinders = new() { Spacing = 6 };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public CompletedTripDetailPage(int tripId)
    {
        _tripId = tripId;
        Title = "Sefer #" + tripId;
        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { _head, _meta, _counts } }),
                    Ui.Section("Tüpler", _cylinders),
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
            ClosedTripDetail detail = await ApiClient.Instance.ClosedTripDetailAsync(_tripId);
            ClosedTripItem trip = detail.Trip;
            Title = trip.Number;
            _head.Text = $"{trip.Number} · {trip.Title}";
            string plates = trip.Plates.Count == 0 ? "-" : string.Join(", ", trip.Plates);
            _meta.Text = $"🚚 {plates}\nBaşlangıç {trip.Started}"
                + (trip.Closed is null ? string.Empty : $"\nBitiş {trip.Closed}")
                + (string.IsNullOrWhiteSpace(trip.ClosedBy) ? string.Empty : "\nKapatan: " + trip.ClosedBy)
                + (string.IsNullOrWhiteSpace(trip.Note) ? string.Empty : "\nNot: " + trip.Note);
            _counts.Text = $"📦 Yüklenen {trip.Loaded} · 🤝 Teslim {trip.Delivered} · ↩️ Alınan {trip.PickedUp} · 🏭 Depo dönüş {trip.Returned}";

            _cylinders.Children.Clear();
            if (detail.Cylinders.Count == 0)
            {
                _cylinders.Children.Add(Ui.Note("Bu seferde tüp yok."));
                return;
            }

            foreach (ClosedTripCylinder cylinder in detail.Cylinders)
            {
                string state = cylinder.Delivered is not null ? "teslim " + cylinder.Delivered
                    : cylinder.Returned is not null ? "dönüş " + cylinder.Returned
                    : cylinder.Missing ? "⚠ eksik" : "—";
                if (cylinder.IsPickup)
                {
                    state = "alım" + (string.IsNullOrWhiteSpace(cylinder.PickupCustomer) ? string.Empty : " (" + cylinder.PickupCustomer + ")") + " · " + state;
                }

                Label line = Ui.Note($"{cylinder.SerialNumber} · {cylinder.GasType} · {cylinder.Plate}\n{state}"
                    + (cylinder.ReasonLabel is null ? string.Empty : " · " + cylinder.ReasonLabel));
                if (cylinder.Missing)
                {
                    line.TextColor = Color.FromArgb("#DC2626");
                }

                _cylinders.Children.Add(line);
            }
        });
    }
}
