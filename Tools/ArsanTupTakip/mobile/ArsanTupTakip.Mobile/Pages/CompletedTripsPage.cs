using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Home → Tamamlanmış seferler: closed trips with the same StatTile look as the active trip summary.
public sealed class CompletedTripsPage : ContentPage
{
    private readonly VerticalStackLayout _list = new() { Spacing = 12 };
    private readonly Label _empty = Ui.Note("Henüz tamamlanmış sefer yok.");
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public CompletedTripsPage()
    {
        Title = "Tamamlanmış seferler";
        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Sefer numarasına göre tarih sırası (yeniden eskiye). Dokunun: detay."),
                    _busy,
                    _empty,
                    _list
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
            List<ClosedTripItem> trips = await ApiClient.Instance.ClosedTripsAsync(50);
            _list.Children.Clear();
            _empty.IsVisible = trips.Count == 0;
            foreach (ClosedTripItem trip in trips)
            {
                _list.Children.Add(TripCard(trip));
            }
        });
    }

    private View TripCard(ClosedTripItem trip)
    {
        string plates = trip.Plates.Count == 0 ? "-" : string.Join(", ", trip.Plates);
        Label head = new()
        {
            Text = $"{trip.Number} · {trip.Title}",
            FontSize = 17,
            FontFamily = "OpenSansSemibold",
            LineBreakMode = LineBreakMode.WordWrap
        };
        Label meta = Ui.Note($"🚚 {plates}\nBaşlangıç {trip.Started}" + (trip.Closed is null ? string.Empty : $" · Bitiş {trip.Closed}"));
        View tiles = Ui.TileGrid(
        [
            Ui.StatTile("📦", trip.Loaded.ToString(), "Yüklenen", Ui.Accent),
            Ui.StatTile("🤝", trip.Delivered.ToString(), "Teslim", Ui.Good),
            Ui.StatTile("↩️", trip.PickedUp.ToString(), "Alınan", Ui.Warn),
            Ui.StatTile("🏭", trip.Returned.ToString(), "Depo dönüş", Ui.Indigo)
        ]);

        Border card = Ui.Card(new VerticalStackLayout { Spacing = 10, Children = { head, meta, tiles } });
        TapGestureRecognizer tap = new();
        tap.Tapped += async (_, _) => await Navigation.PushAsync(new CompletedTripDetailPage(trip.Id));
        card.GestureRecognizers.Add(tap);
        return card;
    }
}
