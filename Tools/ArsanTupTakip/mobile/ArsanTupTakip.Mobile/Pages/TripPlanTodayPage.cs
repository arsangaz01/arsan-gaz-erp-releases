using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Today's trip plans made in the panel, also before the trip starts. A driver with a fixed vehicle sees only that
// vehicle's plan. Hidden while the "Sefer planı" feature is off.
public sealed class TodayPlanView : VerticalStackLayout
{
    private readonly Label _info = Ui.Note("Yükleniyor…");
    private readonly VerticalStackLayout _plans = new() { Spacing = 10 };

    public TodayPlanView()
    {
        Spacing = 8;
        Children.Add(_info);
        Children.Add(_plans);
    }

    // False when the feature is off, so the caller can hide the whole section.
    public async Task<bool> RefreshAsync()
    {
        try
        {
            TodayPlansResponse response = await ApiClient.Instance.TodayPlansAsync();
            _plans.Children.Clear();
            if (!response.On)
            {
                _info.Text = "Sefer planı özelliği kapalı.";
                return false;
            }

            List<TripPlanItem> plans = response.Plans ?? [];
            _info.Text = plans.Count == 0
                ? (ApiClient.Instance.Account?.FixedPlate is { Length: > 0 } plate ? $"Bugün {plate} için plan yok." : "Bugün için plan yok.")
                : $"{DateTime.Today:dd.MM.yyyy} · {plans.Count} plan";
            foreach (TripPlanItem plan in plans)
            {
                _plans.Children.Add(PlanCard(plan));
            }

            return true;
        }
        catch (Exception exception)
        {
            _info.Text = "Plan alınamadı: " + exception.Message;
            return true;
        }
    }

    private static View PlanCard(TripPlanItem plan)
    {
        string status = plan.Status switch
        {
            "seferde" => $"Sefer #{plan.TripId} rotasında",
            "kapandi" => "Sefer kapandı",
            _ => "Sefere çıkınca rotanıza eklenecek"
        };
        VerticalStackLayout body = new()
        {
            Spacing = 4,
            Children =
            {
                new Label { Text = $"🗓️ {plan.Plate} · {plan.StopCount} müşteri", FontSize = 15, FontFamily = "OpenSansSemibold" },
                Ui.Note($"{status} · {plan.AlertMeters} m kala uyarı"
                    + (plan.Done > 0 ? $" · {plan.Done} uğrandı" : string.Empty)
                    + (string.IsNullOrEmpty(plan.Note) ? string.Empty : "\nNot: " + plan.Note))
            }
        };
        int index = 0;
        foreach (TripPlanStopItem stop in plan.Stops)
        {
            string place = string.Join(", ", new[] { stop.District, stop.City }.Where(x => !string.IsNullOrWhiteSpace(x)));
            string location = stop.Latitude is null ? " · 📍 konum yok" : stop.LocationVerified == false ? " · 📍 doğrulanmamış" : string.Empty;
            body.Children.Add(new Label
            {
                Text = $"{++index}. {stop.CustomerName}{(place.Length == 0 ? string.Empty : " — " + place)}{location}{(stop.Done ? "  ✔" : string.Empty)}"
                    + (string.IsNullOrEmpty(stop.Note) ? string.Empty : $"\n     Not: {stop.Note}"),
                FontSize = 14,
                LineBreakMode = LineBreakMode.WordWrap,
                TextColor = stop.Done ? Colors.Green : null
            });
        }

        return Ui.Card(body);
    }
}

public sealed class TripPlanTodayPage : ContentPage
{
    private readonly TodayPlanView _view = new();

    public TripPlanTodayPage()
    {
        Title = "Bugünkü sefer planım";
        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Panelde yönetici veya operatörün aracınız için hazırladığı bugünkü müşteri sırası. Sefere çıkınca müşteriler rotanıza kendiliğinden eklenir."),
                    _view,
                    Ui.Secondary("Yenile", async (_, _) => await _view.RefreshAsync())
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _view.RefreshAsync();
    }
}
