using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// On-site count: the cylinders scanned at a customer are compared with what the records say is there.
public sealed class CylinderAuditPage : ContentPage
{
    private readonly Entry _customerSearch = new() { Placeholder = "Müşteri ara (ad, kod, şehir)", ReturnType = ReturnType.Search };
    private readonly Picker _customer = new() { Title = "Sayım yapılacak müşteri" };
    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no (elle)", ReturnType = ReturnType.Done };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı)" };
    private readonly Label _expected = Ui.Note(string.Empty);
    private readonly Label _scannedText = new() { FontSize = 14 };
    private readonly Label _result = new() { FontSize = 14 };
    private readonly Border _resultCard;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<string> _scanned = [];
    private List<CustomerItem> _customers = [];

    public CylinderAuditPage()
    {
        Title = "Müşteride sayım";
        Ui.LiveSearch(_customerSearch, SearchCustomersAsync);
        _customer.SelectedIndexChanged += (_, _) => ShowExpected();
        _code.Completed += (_, _) => AddManual();
        _resultCard = Ui.Card(_result);
        _resultCard.IsVisible = false;

        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Note("Müşteriyi seçin, sahadaki tüpleri okutun ve sayımı kaydedin. Kayıtlarla karşılaştırılıp eksik ve fazla tüpler listelenir; tüplerin durumu değiştirilmez."),
                    Ui.Section(
                        "1 · Müşteri",
                        _customerSearch,
                        Ui.Secondary("Müşteri ara", async (_, _) => await SearchCustomersAsync()),
                        _customer,
                        _expected),
                    Ui.Section(
                        "2 · Sahadaki tüpler",
                        Ui.Primary("📷 Sahadaki tüpleri okut", async (_, _) => await ScanAsync()),
                        _code,
                        Ui.ActionRow(
                            Ui.Secondary("Kodu ekle", (_, _) => AddManual()),
                            Ui.Secondary("Listeyi temizle", (_, _) => { _scanned.Clear(); RefreshScanned(); })),
                        _scannedText),
                    Ui.Section(
                        "3 · Kaydet",
                        _note,
                        Ui.Primary("Sayımı kaydet ve karşılaştır", async (_, _) => await SubmitAsync())),
                    _busy,
                    _resultCard
                }
            }
        };
        RefreshScanned();
    }

    private CustomerItem? SelectedCustomer => _customer.SelectedIndex >= 0 ? _customers[_customer.SelectedIndex] : null;

    private void ShowExpected() =>
        _expected.Text = SelectedCustomer is CustomerItem customer ? $"Kayıtlara göre {customer.Name} müşterisinde {customer.Cylinders} tüp var." : string.Empty;

    private void RefreshScanned() =>
        _scannedText.Text = _scanned.Count == 0 ? "Henüz tüp okutulmadı." : $"{_scanned.Count} tüp okutuldu:\n{string.Join(", ", _scanned)}";

    private void Add(IEnumerable<string> codes)
    {
        foreach (string code in codes.Select(c => c.Trim()).Where(c => c.Length > 0))
        {
            if (!_scanned.Contains(code, StringComparer.OrdinalIgnoreCase))
            {
                _scanned.Add(code);
            }
        }

        RefreshScanned();
    }

    private void AddManual()
    {
        Add([_code.Text ?? string.Empty]);
        _code.Text = string.Empty;
    }

    private async Task ScanAsync()
    {
        try
        {
            Add(await ScanPage.ScanManyAsync(Navigation, _scanned));
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private Task SearchCustomersAsync(bool live = false) => Ui.RunAsync(this, _busy, async () =>
    {
        string query = _customerSearch.Text ?? string.Empty;
        var found = await ApiClient.Instance.CustomersAsync(query);
        if (live && query != (_customerSearch.Text ?? string.Empty))
        {
            return;
        }

        _customers = found;
        _customer.ItemsSource = _customers.Select(c => $"{c.Name} ({c.Code}) · {c.Cylinders} tüp").ToList();
        if (_customers.Count == 1)
        {
            _customer.SelectedIndex = 0;
        }
        else if (_customers.Count == 0 && !live)
        {
            await DisplayAlertAsync("Müşteri", "Eşleşen müşteri yok.", "Tamam");
        }
    }, quiet: live);

    private async Task SubmitAsync()
    {
        if (SelectedCustomer is not CustomerItem customer)
        {
            await DisplayAlertAsync("Sayım", "Önce müşteriyi seçin.", "Tamam");
            return;
        }

        string question = _scanned.Count == 0
            ? $"Hiç tüp okutulmadı. {customer.Name} müşterisinde tüp bulunmadığı kaydedilsin mi?"
            : $"{customer.Name} için {_scanned.Count} tüplük sayım kaydedilsin mi?";
        if (!await DisplayAlertAsync("Onay", question, "Kaydet", "Vazgeç"))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            CylinderAuditResponse response = await ApiClient.Instance.AuditCustomerAsync(customer.Id, _scanned, _note.Text);
            _result.Text = response.Message;
            _result.TextColor = response.Missing.Count == 0 && response.Unexpected.Count == 0 && response.Unknown.Count == 0
                ? Color.FromArgb("#15803D")
                : Color.FromArgb("#B45309");
            _resultCard.IsVisible = true;
            _scanned.Clear();
            RefreshScanned();
        });
    }
}
