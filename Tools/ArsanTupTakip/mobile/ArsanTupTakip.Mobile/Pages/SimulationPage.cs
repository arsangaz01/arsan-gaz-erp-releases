using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Yönetici only: pick a user, see the home screen as that user sees it and grant or revoke single permissions.
public sealed class SimulationPage : ContentPage
{
    private readonly Picker _user = new() { Title = "Kullanıcı seçin" };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly VerticalStackLayout _summary = new() { Spacing = 8 };
    private readonly VerticalStackLayout _groups = new() { Spacing = 8 };
    private List<SimulationUser> _users = [];
    private SimulationView? _view;
    private readonly int? _startUserId;

    public SimulationPage(int? userId = null)
    {
        Title = "Kullanıcı gözünden";
        Ui.ApplyPageChrome(this);
        _startUserId = userId;
        _user.SelectedIndexChanged += async (_, _) =>
        {
            if (_user.SelectedIndex >= 0 && _user.SelectedIndex < _users.Count && _users[_user.SelectedIndex].Id != _view?.Id)
            {
                await LoadAsync(_users[_user.SelectedIndex].Id);
            }
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Bir kullanıcı seçin: ana ekran ve tüm ekranlar onun yetkileriyle görünür. Aşağıdan yetkilerini ekleyip çıkarabilirsiniz; değişiklik hemen kaydedilir ve web paneldeki kullanıcı ayarlarıyla aynıdır."),
                    Ui.Section("Kullanıcı", _user),
                    _busy,
                    _summary,
                    _groups
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_users.Count > 0)
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            _users = await ApiClient.Instance.SimulationUsersAsync();
            _user.ItemsSource = _users.Select(u => u.Text).ToList();
            int? wanted = _startUserId ?? ApiClient.Instance.Simulation?.Id;
            int index = _users.FindIndex(u => u.Id == wanted);
            if (index >= 0)
            {
                _user.SelectedIndex = index;
            }
            else if (_users.Count == 0)
            {
                _summary.Children.Add(Ui.Note("Başka kullanıcı yok."));
            }
        });
    }

    private Task LoadAsync(int userId) => Ui.RunAsync(this, _busy, async () =>
    {
        _view = await ApiClient.Instance.SimulationViewAsync(userId);
        if (ApiClient.Instance.Simulation?.Id == userId)
        {
            ApiClient.Instance.StartSimulation(_view);
        }

        Show();
    });

    private void Show()
    {
        SimulationView v = _view!;
        bool active = ApiClient.Instance.Simulation?.Id == v.Id;
        _summary.Children.Clear();
        VerticalStackLayout card = new()
        {
            Spacing = 6,
            Children =
            {
                new Label { Text = $"{v.DisplayName} ({v.User})", FontSize = 17, FontFamily = "OpenSansSemibold" },
                Ui.Note($"{v.RoleText} · {v.Permissions.Count} yetki" + (v.FixedPlate is { Length: > 0 } plate ? $" · sabit araç {plate}" : string.Empty)
                    + (v.IsActive ? string.Empty : " · pasif hesap (giriş yapamaz)")),
                active
                    ? Ui.Secondary("Simülasyonu bitir, kendi ekranıma dön", (_, _) => { ApiClient.Instance.EndSimulation(); App.ShowMain(); })
                    : Ui.Primary("Ana ekranı bu kullanıcı gibi göster", (_, _) => { ApiClient.Instance.StartSimulation(v); App.ShowMain(); })
            }
        };
        if (active)
        {
            card.Children.Insert(2, Ui.Note("Simülasyon açık: ana ekrana dönünce bu kullanıcının ekranını görürsünüz."));
        }

        _summary.Children.Add(Ui.Card(card));

        _groups.Children.Clear();
        _groups.Children.Add(Ui.SectionCaption("Yetkiler"));
        _groups.Children.Add(Ui.Note(v.Editable
            ? "Anahtarla ekleyin veya çıkarın. \"rol varsayılanı\": rolün hazır setinde olan yetki. Ön koşulu olan yetki eklenince ön koşulu da eklenir; ön koşul çıkarılınca ona bağlı yetkiler de kalkar."
            : "Yönetici her zaman tüm yetkilere sahiptir; yetkileri değiştirilemez."));
        foreach (SimulationGroup group in v.Groups)
        {
            VerticalStackLayout rows = new() { Spacing = 10 };
            foreach (SimulationPermission p in group.Permissions)
            {
                rows.Children.Add(PermissionRow(v, p));
            }

            _groups.Children.Add(Ui.Section(group.Name, rows));
        }
    }

    private View PermissionRow(SimulationView v, SimulationPermission p)
    {
        Switch toggle = new() { IsToggled = p.Granted, IsEnabled = v.Editable, VerticalOptions = LayoutOptions.Center, OnColor = Ui.Accent };
        toggle.Toggled += async (_, e) =>
        {
            if (e.Value == p.Granted)
            {
                return;
            }

            await ChangeAsync(v.Id, p, e.Value, toggle);
        };
        Grid row = new() { ColumnSpacing = 10, ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)] };
        string tag = p.Granted == p.RoleDefault ? (p.RoleDefault ? " · rol varsayılanı" : string.Empty) : p.Granted ? " · kişiye özel eklendi" : " · kişiye özel çıkarıldı";
        row.Add(new VerticalStackLayout
        {
            Spacing = 1,
            Children =
            {
                new Label { Text = p.Label + tag, FontSize = 14, FontFamily = "OpenSansSemibold", LineBreakMode = LineBreakMode.WordWrap },
                Ui.Note(p.Description)
            }
        }, 0, 0);
        row.Add(toggle, 1, 0);
        return row;
    }

    private async Task ChangeAsync(int userId, SimulationPermission p, bool granted, Switch toggle)
    {
        bool saved = false;
        await Ui.RunAsync(this, _busy, async () =>
        {
            _view = await ApiClient.Instance.SetSimulationPermissionAsync(userId, p.Key, granted);
            saved = true;
        });

        if (!saved)
        {
            toggle.IsToggled = p.Granted;
            return;
        }

        Show();
        if (_view!.Changes.Count > 1)
        {
            await DisplayAlertAsync("Yetkiler", "Kaydedildi:\n" + string.Join("\n", _view.Changes), "Tamam");
        }
    }
}
