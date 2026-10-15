using System.Globalization;
using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// Adds a cylinder whose code was scanned but is not registered yet; it starts in the warehouse or at the chosen customer.
public sealed class CylinderRegisterPage : ContentPage
{
    private const string OtherGas = "Diğer (elle yaz)";
    private static readonly string[] Units = ["Litre", "kg", "m³"];
    private static readonly (string Label, bool? Value)[] Fullness = [("Bilinmiyor", null), ("Dolu", true), ("Boş", false)];

    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Entry _barcode = new() { Placeholder = "Barkod / QR içeriği" };
    private readonly Entry _serial = new() { Placeholder = "Seri numarası (tüpün üzerindeki)" };
    private readonly Picker _gas = new() { Title = "Gaz türü" };
    private readonly Entry _otherGas = new() { Placeholder = "Gaz türünü yazın", IsVisible = false };
    private readonly Entry _capacity = new() { Placeholder = "Kapasite (ör. 40)", Keyboard = Keyboard.Numeric };
    private readonly Picker _unit = new() { Title = "Birim", ItemsSource = Units, SelectedIndex = 0 };
    private readonly Picker _fullness = new() { Title = "Dolu / boş" };
    private readonly CheckBox _hasTestDate = new() { Color = Ui.Accent };
    private readonly DatePicker _testDate = new() { Date = DateTime.Today.AddYears(1), Format = "dd.MM.yyyy", IsEnabled = false };
    private readonly Label _customerInfo = new()
    {
        FontSize = 15,
        FontFamily = "OpenSansSemibold",
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "Depoda (müşteri yok) — isterseniz müşteri seçin."
    };
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private CustomerItem? _selectedCustomer;
    private bool _saved;

    // Leaving for the new-customer form is not leaving the registration.
    private bool _customerFormOpen;

    private CylinderRegisterPage(string code)
    {
        Title = "Yeni tüp kaydı";
        Ui.ApplyPageChrome(this);
        _barcode.Text = code;
        _serial.Text = code;
        _fullness.ItemsSource = Fullness.Select(f => f.Label).ToList();
        _fullness.SelectedIndex = 0;
        _gas.SelectedIndexChanged += (_, _) => _otherGas.IsVisible = Equals(_gas.SelectedItem, OtherGas);
        _hasTestDate.CheckedChanged += (_, e) => _testDate.IsEnabled = e.Value;

        Grid capacityRow = new()
        {
            ColumnSpacing = 8,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(new GridLength(110))]
        };
        capacityRow.Add(_capacity, 0, 0);
        capacityRow.Add(_unit, 1, 0);

        Grid testRow = new()
        {
            ColumnSpacing = 4,
            ColumnDefinitions = [new ColumnDefinition(GridLength.Auto), new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Auto)]
        };
        testRow.Add(_hasTestDate, 0, 0);
        testRow.Add(new Label { Text = "Test tarihi", VerticalTextAlignment = TextAlignment.Center }, 1, 0);
        testRow.Add(_testDate, 2, 0);

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Okutulan kod sistemde kayıtlı değil. Bilgileri girip kaydedin; müşteri seçmezseniz tüp \"Depoda\" olarak envantere eklenir."),
                    Ui.Section("Kimlik", Ui.Note("Barkod"), _barcode, Ui.Note("Seri numarası"), _serial),
                    Ui.Section("Tüp bilgisi", _gas, _otherGas, capacityRow, _fullness, testRow),
                    Ui.Section("Müşteri (isteğe bağlı)",
                        Ui.Note("Seçerseniz tüp doğrudan bu müşteriye teslim edilmiş olarak kaydedilir. Listede yoksa yeni müşteri açabilirsiniz."),
                        _customerInfo,
                        Ui.Primary("🔎 Müşteri ara / seç", async (_, _) => await PickCustomerAsync()),
                        Ui.ActionRow(
                            Ui.Secondary("Depoda bırak", (_, _) => ClearCustomer()),
                            Ui.Secondary("➕ Yeni müşteri", async (_, _) => await NewCustomerAsync()))),
                    Ui.Primary("Kaydet", async (_, _) => await SaveAsync()),
                    _busy
                }
            }
        };
    }

    // Opens the form and returns the saved barcode, or null when the user leaves without saving.
    public static async Task<string?> RegisterAsync(INavigation navigation, string code)
    {
        CylinderRegisterPage page = new(code);
        await navigation.PushAsync(page);
        return await page._result.Task;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (_gas.ItemsSource is not null)
        {
            return;
        }

        List<string> gases;
        try
        {
            gases = await ApiClient.Instance.GasTypesAsync();
        }
        catch (ApiException)
        {
            gases = [];
        }

        _gas.ItemsSource = gases.Append(OtherGas).ToList();
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_saved && !_customerFormOpen)
        {
            _result.TrySetResult(null);
        }
    }

    private void ShowSelectedCustomer() =>
        _customerInfo.Text = _selectedCustomer is CustomerItem c
            ? $"{c.Name}\n{c.Subtitle}"
            : "Depoda (müşteri yok) — isterseniz müşteri seçin.";

    private void ClearCustomer()
    {
        _selectedCustomer = null;
        ShowSelectedCustomer();
    }

    private async Task PickCustomerAsync()
    {
        CustomerItem? picked = await CustomerPickPage.OpenAsync(Navigation, "Müşteri seçin");
        if (picked is null)
        {
            return;
        }

        _selectedCustomer = picked;
        ShowSelectedCustomer();
    }

    private async Task NewCustomerAsync()
    {
        CustomerItem? created;
        _customerFormOpen = true;
        try
        {
            created = await CustomerCreatePage.CreateAsync(Navigation, _selectedCustomer?.Name);
        }
        finally
        {
            _customerFormOpen = false;
        }

        if (created is null)
        {
            return;
        }

        _selectedCustomer = created;
        ShowSelectedCustomer();
    }

    private string GasType => Equals(_gas.SelectedItem, OtherGas) ? (_otherGas.Text ?? string.Empty).Trim() : _gas.SelectedItem as string ?? string.Empty;

    private async Task SaveAsync()
    {
        string serial = (_serial.Text ?? string.Empty).Trim();
        string barcode = (_barcode.Text ?? string.Empty).Trim();
        string text = (_capacity.Text ?? string.Empty).Trim().Replace(',', '.');
        if (serial.Length == 0 || GasType.Length == 0)
        {
            await DisplayAlertAsync("Yeni tüp", "Seri numarası ve gaz türü zorunludur.", "Tamam");
            return;
        }

        if (!decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal capacity) || capacity <= 0)
        {
            await DisplayAlertAsync("Yeni tüp", "Kapasite sıfırdan büyük bir sayı olmalıdır (ör. 40 veya 10,5).", "Tamam");
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            string message = await ApiClient.Instance.RegisterCylinderAsync(
                serial,
                barcode.Length == 0 ? null : barcode,
                GasType,
                capacity,
                (string)_unit.SelectedItem,
                Fullness[Math.Max(0, _fullness.SelectedIndex)].Value,
                _hasTestDate.IsChecked ? _testDate.Date : null,
                _selectedCustomer?.Id);
            _saved = true;
            await DisplayAlertAsync("Kaydedildi", message, "Tamam");
            await Navigation.PopAsync();
            _result.TrySetResult(barcode.Length == 0 ? serial : barcode);
        });
    }
}
