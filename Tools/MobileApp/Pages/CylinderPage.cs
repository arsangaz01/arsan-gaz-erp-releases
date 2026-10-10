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
    private readonly Label _pendingText = new() { FontSize = 13, TextColor = Ui.Accent };
    private readonly VerticalStackLayout _pendingPanel;
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
        _code.Completed += async (_, _) => await LookupAsync(_code.Text, quick: false);
        _customerSearch.Completed += async (_, _) => await SearchCustomersAsync();

        _pendingPanel = new VerticalStackLayout
        {
            Spacing = 6,
            IsVisible = false,
            Children = { _pendingText, Ui.Secondary("Bekleyenleri şimdi gönder", async (_, _) => await FlushAsync(showResult: true)) }
        };
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
                Ui.Primary("Kaydet", async (_, _) => await MoveAsync(confirm: true))
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
                    _pendingPanel,
                    Ui.Primary("📷 Barkod / QR okut", async (_, _) => await ScanAsync()),
                    _code,
                    Ui.Secondary("Sorgula", async (_, _) => await LookupAsync(_code.Text, quick: false)),
                    _busy,
                    _detail,
                    _movePanel
                }
            }
        };

        OfflineMoves.Changed += () => MainThread.BeginInvokeOnMainThread(RefreshPending);
        Connectivity.Current.ConnectivityChanged += async (_, e) =>
        {
            if (e.NetworkAccess == NetworkAccess.Internet)
            {
                await FlushAsync(showResult: false);
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        RefreshPending();
        await FlushAsync(showResult: false);
    }

    private int? SelectedStatus => _action.SelectedIndex >= 0 ? Actions[_action.SelectedIndex].Status : null;

    private void RefreshPending()
    {
        int count = OfflineMoves.Pending.Count;
        _pendingPanel.IsVisible = count > 0;
        _pendingText.Text = $"📶 Gönderilmeyi bekleyen {count} tüp hareketi var. Bağlantı gelince otomatik gönderilir.";
    }

    private async Task FlushAsync(bool showResult)
    {
        if (OfflineMoves.Pending.Count == 0)
        {
            return;
        }

        (int sent, string? error) = await OfflineMoves.FlushAsync();
        if (error is not null)
        {
            await DisplayAlertAsync("Bekleyen hareket reddedildi", error, "Tamam");
        }
        else if (showResult || sent > 0)
        {
            int left = OfflineMoves.Pending.Count;
            await DisplayAlertAsync("Bekleyen hareketler",
                left == 0 ? $"{sent} hareket ERP'ye gönderildi." : $"{sent} gönderildi; {left} hareket bağlantı bekliyor.", "Tamam");
        }
    }

    private async Task ScanAsync()
    {
        try
        {
            string? value = await ScanPage.ScanAsync(Navigation);
            if (value is not null)
            {
                _code.Text = value;
                await LookupAsync(value, quick: true);
            }
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("Kamera", exception.Message, "Tamam");
        }
    }

    private async Task LookupAsync(string? code, bool quick)
    {
        string value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return;
        }

        _current = null;
        _movePanel.IsVisible = false;
        _detail.Text = string.Empty;
        _action.SelectedIndex = -1;
        _note.Text = string.Empty;
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                _current = await ApiClient.Instance.CylinderAsync(value);
                _detail.Text = _current.Detail;
            }
            catch (ServerUnreachableException)
            {
                _current = new CylinderInfo(0, value, value, -1, "bilinmiyor", null, string.Empty);
                _detail.Text = $"📶 Bağlantı yok. Tüp bilgisi alınamadı ({value}).\nHareketi yine de kaydedebilirsiniz; bağlantı gelince ERP'ye gönderilir.";
            }

            _movePanel.IsVisible = true;
        });

        if (quick && _current is not null)
        {
            await QuickMoveAsync();
        }
    }

    private async Task QuickMoveAsync()
    {
        string? choice = await DisplayActionSheetAsync(
            $"{_current!.SerialNumber} · {_current.StatusName}", "Kapat", null, Actions.Select(a => a.Label).ToArray());
        int index = Array.FindIndex(Actions, a => a.Label == choice);
        if (index < 0)
        {
            return;
        }

        _action.SelectedIndex = index;
        if (Actions[index].Status == 3)
        {
            await DisplayAlertAsync("Müşteriye teslim", "Aşağıdan müşteriyi arayıp seçin, sonra Kaydet'e basın.", "Tamam");
            _customerSearch.Focus();
            return;
        }

        await MoveAsync(confirm: false);
    }

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

    private async Task MoveAsync(bool confirm)
    {
        if (_current is null || SelectedStatus is not int status)
        {
            await DisplayAlertAsync("Tüp", "Önce tüpü sorgulayıp hareket seçin.", "Tamam");
            return;
        }

        CustomerItem? customer = status == 3 && _customer.SelectedIndex >= 0 ? _customers[_customer.SelectedIndex] : null;
        if (status == 3 && customer is null)
        {
            await DisplayAlertAsync("Tüp", "Müşteriye teslim için müşteri seçin. Bağlantı yoksa önce bağlantılıyken müşteriyi aratın.", "Tamam");
            return;
        }

        string label = _action.Items[_action.SelectedIndex] + (customer is null ? string.Empty : $" ({customer.Name})");
        if (confirm && !await DisplayAlertAsync("Onay", $"{_current.SerialNumber}: {_current.StatusName} → {label}\nKaydedilsin mi?", "Kaydet", "Vazgeç"))
        {
            return;
        }

        CylinderInfo current = _current;
        await Ui.RunAsync(this, _busy, async () =>
        {
            try
            {
                string message = await ApiClient.Instance.MoveCylinderAsync(current.Barcode, status, customer?.Id, _note.Text);
                await DisplayAlertAsync("Kaydedildi", message, "Tamam");
                await LookupAsync(current.Barcode, quick: false);
            }
            catch (ServerUnreachableException)
            {
                OfflineMoves.Add(new PendingMove(current.Barcode, status, customer?.Id, _note.Text, label, DateTime.Now));
                _movePanel.IsVisible = false;
                _detail.Text = $"📶 Bağlantı yok: {current.SerialNumber} → {label} telefonda saklandı. Bağlantı gelince otomatik gönderilecek.";
            }
        });
    }
}
