using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

// Opens a new ERP customer record when the customer is not in the picker list; returns the created (or chosen existing) customer.
public sealed class CustomerCreatePage : ContentPage
{
    private const string UseExisting = "Mevcut kaydı seç";
    private const string CreateAnyway = "Yine de yeni kayıt aç";

    private readonly TaskCompletionSource<CustomerItem?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Entry _name = Ui.TextField("Firma / müşteri adı *", maxLength: 200);
    private readonly Entry _contact = Ui.TextField("Yetkili kişi", maxLength: 150);
    private readonly Entry _phone = Ui.TextField("Telefon", Keyboard.Telephone, 50);
    private readonly Entry _email = Ui.TextField("E-posta", Keyboard.Email, 200);
    private readonly Entry _city = Ui.TextField("Şehir", maxLength: 100);
    private readonly Entry _district = Ui.TextField("İlçe", maxLength: 100);
    private readonly Entry _address = Ui.TextField("Adres", maxLength: 500);
    private readonly Entry _taxOffice = Ui.TextField("Vergi dairesi", maxLength: 100);
    private readonly Entry _taxNumber = Ui.TextField("Vergi / TC no", Keyboard.Numeric, 20);
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private bool _done;

    private CustomerCreatePage(string? name)
    {
        Title = "Yeni müşteri";
        Ui.ApplyPageChrome(this);
        _name.Text = name?.Trim();
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Listede olmayan müşteri için ERP'de yeni kayıt açılır; kod otomatik verilir. Aynı adla kayıt varsa mevcut kaydı seçmeniz önerilir."),
                    Ui.Section("Müşteri", _name, _contact, _phone, _email),
                    Ui.Section("Adres (isteğe bağlı)", _city, _district, _address),
                    Ui.Section("Vergi bilgisi (isteğe bağlı)", _taxOffice, _taxNumber),
                    Ui.Primary("Müşteriyi kaydet", async (_, _) => await SaveAsync(allowDuplicate: false)),
                    _busy
                }
            }
        };
    }

    // Returns the new or chosen customer, or null when the user leaves without saving.
    public static async Task<CustomerItem?> CreateAsync(INavigation navigation, string? name)
    {
        CustomerCreatePage page = new(name);
        await navigation.PushAsync(page);
        return await page._result.Task;
    }

    protected override void OnDisappearing()
    {
        base.OnDisappearing();
        if (!_done)
        {
            _result.TrySetResult(null);
        }
    }

    private static string? Value(Entry entry) => string.IsNullOrWhiteSpace(entry.Text) ? null : entry.Text.Trim();

    private async Task SaveAsync(bool allowDuplicate)
    {
        if (Value(_name) is null)
        {
            await DisplayAlertAsync("Yeni müşteri", "Müşteri adı zorunludur.", "Tamam");
            return;
        }

        bool createAnyway = false;
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                CustomerCreated created = await ApiClient.Instance.CreateCustomerAsync(new
                {
                    name = Value(_name),
                    contactName = Value(_contact),
                    phone = Value(_phone),
                    email = Value(_email),
                    city = Value(_city),
                    district = Value(_district),
                    address = Value(_address),
                    taxOffice = Value(_taxOffice),
                    taxNumber = Value(_taxNumber),
                    allowDuplicate
                });
                await DisplayAlertAsync("Kaydedildi", created.Message, "Tamam");
                await FinishAsync(created.Customer);
            }
            catch (ApiException duplicate) when (duplicate.ExistingCustomer() is CustomerItem found)
            {
                string? choice = await DisplayActionSheetAsync(duplicate.Message, "Vazgeç", null, UseExisting, CreateAnyway);
                if (choice == UseExisting)
                {
                    await FinishAsync(found);
                }

                createAnyway = choice == CreateAnyway;
            }
        });

        if (createAnyway && !allowDuplicate)
        {
            await SaveAsync(allowDuplicate: true);
        }
    }

    private async Task FinishAsync(CustomerItem customer)
    {
        _done = true;
        await Navigation.PopAsync();
        _result.TrySetResult(customer);
    }
}
