using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Adds a vehicle or edits a registered one. The plate of a saved vehicle cannot change (trips refer to it).
public sealed class VehicleEditPage : ContentPage
{
    private readonly VehicleItem? _existing;
    private readonly Entry _plate = Ui.TextField("Plaka * (01 ABC 123)", maxLength: 20);
    private readonly Entry _name = Ui.TextField("Araç adı (ör. Beyaz kamyonet)", maxLength: 100);
    private readonly Entry _type = Ui.TextField("Tür (kamyonet, kamyon, panelvan…)", maxLength: 100);
    private readonly Entry _driver = Ui.TextField("Şoför (isteğe bağlı)", maxLength: 100);
    private readonly Entry _capacity = Ui.TextField("Kapasite: kaç tüp alır (isteğe bağlı)", Keyboard.Numeric, 5);
    private readonly Entry _note = Ui.TextField("Not", maxLength: 500);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };

    public VehicleEditPage(VehicleItem? existing)
    {
        _existing = existing;
        Title = existing is null ? "Yeni araç" : "Aracı düzenle";
        Ui.ApplyPageChrome(this);
        _plate.Keyboard = Keyboard.Create(KeyboardFlags.CapitalizeCharacter);
        if (existing is not null)
        {
            _plate.Text = existing.Plate;
            _plate.IsEnabled = false;
            _name.Text = existing.Name;
            _type.Text = existing.VehicleType;
            _driver.Text = existing.DriverName;
            _capacity.Text = existing.CylinderCapacity?.ToString();
            _note.Text = existing.Note;
        }

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note(existing is null
                        ? "Kaydedilen araçlar Sefer ekranında \"Sefer Başlat\" ile seçilebilir. Plaka kayıttan sonra değiştirilemez."
                        : "Plaka değiştirilemez (seferler ve tüp hareketleri plakaya bağlı). Farklı plaka için yeni araç ekleyip bunu pasife alın."),
                    Ui.Section("Araç", _plate, _name, _type, _driver, _capacity, _note),
                    Ui.Primary("Kaydet", async (_, _) => await SaveAsync()),
                    _busy
                }
            }
        };
    }

    private static string? Value(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();

    private async Task SaveAsync()
    {
        if (_existing is null && Value(_plate) is null)
        {
            await DisplayAlertAsync("Araç", "Plaka zorunludur.", "Tamam");
            return;
        }

        int? capacity = null;
        if (Value(_capacity) is string text)
        {
            if (!int.TryParse(text, out int parsed) || parsed < 0)
            {
                await DisplayAlertAsync("Araç", "Kapasite tüp adedi olarak tam sayı olmalıdır.", "Tamam");
                return;
            }

            capacity = parsed;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            VehicleSaved saved = await ApiClient.Instance.SaveVehicleAsync(_existing?.Id,
                new VehicleForm(_existing?.Plate ?? Value(_plate), Value(_name), Value(_type), Value(_driver), capacity, Value(_note), _existing?.IsActive));
            await DisplayAlertAsync("Araç", saved.Message, "Tamam");
            await Navigation.PopAsync();
        });
    }
}
