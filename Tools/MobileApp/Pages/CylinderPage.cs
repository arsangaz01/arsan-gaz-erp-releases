using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class CylinderPage : ContentPage
{
    private static readonly (string Label, int Status)[] Actions =
    [
        ("Müşteriye teslim", 3),
        ("Depoya iade", 1),
        ("Araca yükle", 2),
        ("Dolum", 4),
        ("Servis", 5)
    ];

    private readonly Entry _code = new() { Placeholder = "Barkod veya seri no", ReturnType = ReturnType.Search };
    private readonly Label _detail = new() { FontSize = 14 };
    private readonly Picker _action = new() { Title = "Hareket" };
    private readonly Entry _customerSearch = new() { Placeholder = "Müşteri ara (ad, kod, şehir)", ReturnType = ReturnType.Search };
    private readonly Picker _customer = new() { Title = "Müşteri seçin" };
    private readonly Entry _note = new() { Placeholder = "Not (isteğe bağlı)" };
    private readonly VerticalStackLayout _movePanel;
    private readonly VerticalStackLayout _customerPanel;
    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private List<CustomerItem> _customers = [];
    private CylinderInfo? _current;

    public CylinderPage()
    {
        Title = "Tüp";
        foreach ((string label, _) in Actions)
        {
            _action.Items.Add(label);
        }

        _action.SelectedIndexChanged += (_, _) => _customerPanel!.IsVisible = SelectedStatus == 3;
        _code.Completed += async (_, _) => await LookupAsync(_code.Text);
        _customerSearch.Completed += async (_, _) => await SearchCustomersAsync();

        _customerPanel = new VerticalStackLayout
        {
            Spacing = 6,
            IsVisible = false,
            Children = { _customerSearch, Ui.Secondary("Müşteri ara", async (_, _) => await SearchCustomersAsync()), _customer }
        };
        _movePanel = new VerticalStackLayout
        {
            Spacing = 8,
            IsVisible = false,
            Children =
            {
                Ui.Title("Hareket kaydet"),
                _action,
                _customerPanel,
                _note,
                Ui.Primary("Kaydet", async (_, _) => await MoveAsync())
            }
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = 16,
                Spacing = 10,
                Children =
                {
                    Ui.Primary("📷 Barkod / QR okut", async (_, _) => await ScanAsync()),
                    _code,
                    Ui.Secondary("Sorgula", async (_, _) => await LookupAsync(_code.Text)),
                    _busy,
                    _detail,
                    _movePanel
                }
            }
        };
    }

    private int? SelectedStatus => _action.SelectedIndex >= 0 ? Actions[_action.SelectedIndex].Status : null;

    private async Task ScanAsync()
    {
        try
        {
            string? value = await ScanPage.ScanAsync(Navigation);
            if (value is not null)
            {
                _code.Text = value;
                await LookupAsync(value);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private Task LookupAsync(string? code) => Ui.RunAsync(this, _busy, async () =>
    {
        string value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return;
        }

        _current = null;
        _movePanel.IsVisible = false;
        _detail.Text = string.Empty;
        _current = await ApiClient.Instance.CylinderAsync(value);
        _detail.Text = _current.Detail;
        _action.SelectedIndex = -1;
        _note.Text = string.Empty;
        _movePanel.IsVisible = true;
    });

    private Task SearchCustomersAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        _customers = await ApiClient.Instance.CustomersAsync(_customerSearch.Text ?? string.Empty);
        _customer.ItemsSource = _customers.Select(c => $"{c.Name} ({c.Code})").ToList();
        if (_customers.Count == 1)
        {
            _customer.SelectedIndex = 0;
        }
        else if (_customers.Count == 0)
        {
            await DisplayAlertAsync("Müşteri", "Eşleşen müşteri yok.", "Tamam");
        }
    });

    private async Task MoveAsync()
    {
        if (_current is null || SelectedStatus is not int status)
        {
            await DisplayAlertAsync("Tüp", "Önce tüpü sorgulayıp hareket seçin.", "Tamam");
            return;
        }

        CustomerItem? customer = status == 3 && _customer.SelectedIndex >= 0 ? _customers[_customer.SelectedIndex] : null;
        if (status == 3 && customer is null)
        {
            await DisplayAlertAsync("Tüp", "Müşteriye teslim için müşteri seçin.", "Tamam");
            return;
        }

        string summary = $"{_current.SerialNumber}: {_current.StatusName} → {_action.Items[_action.SelectedIndex]}" +
                         (customer is null ? string.Empty : $" ({customer.Name})");
        if (!await DisplayAlertAsync("Onay", summary + "\nKaydedilsin mi?", "Kaydet", "Vazgeç"))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            string message = await ApiClient.Instance.MoveCylinderAsync(_current.Barcode, status, customer?.Id, _note.Text);
            await DisplayAlertAsync("Kaydedildi", message, "Tamam");
            await LookupAsync(_current.Barcode);
        });
    }
}
