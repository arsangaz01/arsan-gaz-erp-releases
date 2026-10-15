using ArsanTupTakip.Mobile.Services;

namespace ArsanTupTakip.Mobile.Pages;

// On-site count: the cylinders scanned at a customer are compared with what the records say is there.
public sealed class CylinderAuditPage : ContentPage
{
    private readonly Label _customerInfo = new()
    {
        FontSize = 15,
        FontFamily = "OpenSansSemibold",
        LineBreakMode = LineBreakMode.WordWrap,
        Text = "🔎 Sayım yapılacak müşteriyi arayıp seçin."
    };
    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no (elle)", ReturnType = ReturnType.Done };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı)" };
    private readonly Label _expected = Ui.Note(string.Empty);
    private readonly Label _scannedText = new() { FontSize = 14 };
    private readonly Label _result = new() { FontSize = 14 };
    private readonly Border _resultCard;
    private readonly Button _stockButton;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly List<string> _scanned = [];
    private CustomerItem? _selectedCustomer;

    public CylinderAuditPage()
    {
        Title = "Müşteride sayım";
        _code.Completed += (_, _) => AddManual();
        _resultCard = Ui.Card(_result);
        _resultCard.IsVisible = false;
        _stockButton = Ui.Secondary("📋 Müşterideki tüpleri gör", async (_, _) =>
        {
            if (_selectedCustomer is CustomerItem customer)
            {
                await Navigation.PushAsync(new CustomerStockPage(customer));
            }
        });
        _stockButton.IsVisible = false;

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
                        _customerInfo,
                        Ui.Primary("🔎 Müşteri ara / seç", async (_, _) => await PickCustomerAsync()),
                        _expected,
                        _stockButton),
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

    private void ShowSelected()
    {
        if (_selectedCustomer is not CustomerItem customer)
        {
            _customerInfo.Text = "🔎 Sayım yapılacak müşteriyi arayıp seçin.";
            _expected.Text = string.Empty;
            _stockButton.IsVisible = false;
            return;
        }

        _customerInfo.Text = $"{customer.Name}\n{customer.Subtitle}";
        _expected.Text = $"Kayıtlara göre {customer.Name} müşterisinde {customer.Cylinders} tüp var.";
        _stockButton.IsVisible = ApiClient.Instance.Can(Perm.CylinderView);
    }

    private async Task PickCustomerAsync()
    {
        CustomerItem? picked = await CustomerPickPage.OpenAsync(Navigation, "Sayım müşterisi");
        if (picked is null)
        {
            return;
        }

        _selectedCustomer = picked;
        ShowSelected();
    }

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

    private async Task SubmitAsync()
    {
        if (_selectedCustomer is not CustomerItem customer)
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
