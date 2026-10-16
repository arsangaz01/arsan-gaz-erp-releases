using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Read-only trip summary for re-checking after the trip was closed.
public sealed class CompletedTripDetailPage : ContentPage
{
    private readonly int _tripId;
    private readonly Label _head = new() { FontSize = 18, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap };
    private readonly Label _meta = Ui.Note(string.Empty);
    private readonly ContentView _tiles = new();
    private readonly VerticalStackLayout _customers = new() { Spacing = 8 };
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
                    Ui.Card(new VerticalStackLayout { Spacing = 8, Children = { _head, _meta, _tiles } }),
                    Ui.Section("Müşteriler", _customers),
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
                + (string.IsNullOrWhiteSpace(trip.Note) ? string.Empty : "\nNot: " + trip.Note);
            _tiles.Content = Ui.TileGrid(
            [
                Ui.StatTile("📦", trip.Loaded.ToString(), "Yüklenen", Ui.Accent),
                Ui.StatTile("🤝", trip.Delivered.ToString(), "Teslim", Ui.Good),
                Ui.StatTile("↩️", trip.PickedUp.ToString(), "Alınan", Ui.Warn),
                Ui.StatTile("🏭", trip.Returned.ToString(), "Depo dönüş", Ui.Indigo)
            ]);

            _customers.Children.Clear();
            if (detail.Customers.Count == 0)
            {
                _customers.Children.Add(Ui.Note("Bu seferde müşteri kaydı yok."));
            }
            else
            {
                foreach (ClosedTripCustomer customer in detail.Customers)
                {
                    _customers.Children.Add(Ui.Card(new Label
                    {
                        Text = customer.Text,
                        FontSize = 14,
                        LineBreakMode = LineBreakMode.WordWrap
                    }));
                }
            }

            _cylinders.Children.Clear();
            if (detail.Cylinders.Count == 0)
            {
                _cylinders.Children.Add(Ui.Note("Bu seferde tüp yok."));
            }
            else
            {
                foreach (ClosedTripCylinder cylinder in detail.Cylinders)
                {
                    string state = cylinder.Delivered is not null ? "teslim " + cylinder.Delivered
                        : cylinder.Returned is not null ? "dönüş " + cylinder.Returned
                        : cylinder.Missing ? "⚠ eksik" : "—";
                    if (cylinder.Pickup)
                    {
                        state = "alım · " + state;
                    }

                    Label line = Ui.Note($"{cylinder.SerialNumber} · {cylinder.GasType} · {cylinder.Plate}\n{state}"
                        + (cylinder.ReasonLabel is null ? string.Empty : " · " + cylinder.ReasonLabel));
                    if (cylinder.Missing)
                    {
                        line.TextColor = Ui.Bad;
                    }

                    _cylinders.Children.Add(line);
                }
            }
        });
    }
}
