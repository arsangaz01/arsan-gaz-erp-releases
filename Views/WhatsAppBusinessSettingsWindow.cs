using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed class WhatsAppBusinessSettingsWindow : Window
{
    private readonly WhatsAppBusinessService _service;
    private readonly PasswordBox _accessToken = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _phoneNumberId = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _apiVersion = new() { Margin = new Thickness(0, 4, 0, 10), Text = "v25.0" };
    private readonly TextBox _templateName = new() { Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBox _languageCode = new() { Margin = new Thickness(0, 4, 0, 10), Text = "tr" };
    private readonly TextBox _testRecipient = new() { Margin = new Thickness(0, 4, 0, 6) };
    private readonly CheckBox _testRecipientConfirmed = new()
    {
        Content = "Bu numara bana ait; WhatsApp test alıcısı olarak Meta'da doğruladım.",
        Margin = new Thickness(0, 0, 0, 8)
    };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button _selfTest = new()
    {
        Content = "Kendime hello_world test mesajı gönder",
        Padding = new Thickness(14, 7, 14, 7),
        Margin = new Thickness(0, 10, 0, 0)
    };

    public WhatsAppBusinessSettingsWindow(WhatsAppBusinessService service)
    {
        _service = service;
        Title = "WhatsApp Business Cloud API";
        Width = 610;
        Height = 800;
        MinHeight = 740;
        WindowWorkAreaService.Fit(this);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.CanResize;

        StackPanel form = new() { Margin = new Thickness(24) };
        string configuredStatus = service.IsConfigured
            ? "Meta erişim bilgileri bu Windows hesabında şifreli kayıtlı."
            : "Meta erişim bilgileri yapılandırılmadı.";
        form.Children.Add(new TextBlock
        {
            Text = configuredStatus,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap
        });
        if (service.IsConfigured)
        {
            try
            {
                WhatsAppBusinessService.Settings settings = service.LoadSettings();
                _phoneNumberId.Text = settings.PhoneNumberId;
                _apiVersion.Text = settings.ApiVersion;
                _templateName.Text = settings.TemplateName;
                _languageCode.Text = settings.LanguageCode;
            }
            catch (Exception exception) when (exception is IOException or CryptographicException or JsonException or InvalidOperationException)
            {
                _status.Text = "Kayıtlı ayarlar okunamadı: " + exception.GetBaseException().Message;
            }
        }
        _accessToken.ToolTip = "Mevcut token güvenlik nedeniyle gösterilmez. Boş bırakırsanız kayıtlı token korunur; değiştirmek için yeni token girin.";
        AddField(form, "Meta erişim token'ı (değiştirmek için yeni token girin)", _accessToken);
        AddField(form, "WhatsApp Phone Number ID", _phoneNumberId);
        AddField(form, "Graph API sürümü", _apiVersion);
        AddField(form, "WhatsApp Manager'da onaylanmış, parametresiz şablon adı", _templateName);
        AddField(form, "Şablon dil kodu (ör. tr veya en_US)", _languageCode);
        form.Children.Add(new TextBlock
        {
            Text = "Token sohbetlere veya loglara yazılmaz; mevcut token arayüzde gösterilmez. Boş token alanıyla ayarları kaydetmek mevcut tokenı korur. Meta uygulamasında whatsapp_business_messaging (gönderim) ve whatsapp_business_management (numara testi) izinleri gerekir. Gerçek müşterilere mesaj Meta ücreti doğurabilir.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 14)
        });

        StackPanel buttons = new() { Orientation = Orientation.Horizontal };
        Button save = new() { Content = "Şifreli kaydet", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0) };
        Button test = new() { Content = "Bağlantıyı test et", Padding = new Thickness(14, 7, 14, 7), Margin = new Thickness(0, 0, 8, 0), Style = (Style)Application.Current.FindResource("SecondaryButton") };
        Button remove = new() { Content = "Ayarları sil", Padding = new Thickness(14, 7, 14, 7), Style = (Style)Application.Current.FindResource("DangerButton") };
        save.Click += (_, _) => SaveSettings();
        test.Click += async (_, _) => await TestConnectionAsync();
        remove.Click += (_, _) => RemoveSettings();
        buttons.Children.Add(save);
        buttons.Children.Add(test);
        buttons.Children.Add(remove);
        form.Children.Add(buttons);

        form.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 10) });
        form.Children.Add(new TextBlock
        {
            Text = "YALNIZCA KENDİ TEST NUMARANIZA",
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 4)
        });
        form.Children.Add(new TextBlock
        {
            Text = "Bu ayrı test, CRM müşteri listesini kullanmaz. Alıcıyı kendiniz girin; Meta'da test alıcısı olarak doğrulanmış ve size ait numara olmalıdır. Sabit hello_world / en_US şablonu dışında mesaj göndermez.",
            TextWrapping = TextWrapping.Wrap
        });
        AddField(form, "Kendi doğrulanmış test alıcınız (ülke koduyla, ör. +905XXXXXXXXX)", _testRecipient);
        form.Children.Add(_testRecipientConfirmed);
        _selfTest.Click += async (_, _) => await SendSelfTestAsync();
        form.Children.Add(_selfTest);
        form.Children.Add(_status);
        Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void AddField(Panel panel, string label, Control input)
    {
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Foreground = (System.Windows.Media.Brush)Application.Current.FindResource("MutedBrush") });
        panel.Children.Add(input);
    }

    private void SaveSettings()
    {
        try
        {
            _service.SaveSettings(
                _accessToken.Password,
                _phoneNumberId.Text,
                _apiVersion.Text,
                _templateName.Text,
                _languageCode.Text);
            bool tokenReplaced = !string.IsNullOrWhiteSpace(_accessToken.Password);
            _accessToken.Clear();
            _status.Text = tokenReplaced
                ? "Ayarlar bu Windows hesabına DPAPI ile şifrelenerek kaydedildi."
                : "Ayarlar kaydedildi; daha önce şifrelenmiş erişim token'ı korundu.";
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or CryptographicException)
        {
            _status.Text = exception.GetBaseException().Message;
        }
    }

    private async Task SendSelfTestAsync()
    {
        if (_testRecipientConfirmed.IsChecked != true)
        {
            _status.Text = "Devam etmek için kendi numaranızın Meta'da doğrulanmış test alıcısı olduğunu onaylayın.";
            return;
        }

        string recipient;
        try
        {
            recipient = WhatsAppBusinessService.NormalizeE164(_testRecipient.Text);
        }
        catch (ArgumentException exception)
        {
            _status.Text = exception.Message;
            return;
        }

        if (!_service.IsConfigured)
        {
            _status.Text = "Önce Meta API ayarlarını kaydedin.";
            return;
        }

        string confirmation =
            $"Yalnızca bu test şablonu gönderilecek:\nhello_world (en_US)\n\nAlıcı: +{recipient}\n\n" +
            "Devam edilsin mi? Meta API isteği kabul etse bile teslimat ayrıca doğrulanmalıdır.";
        if (MessageBox.Show(this, confirmation, "Kendi numarama WhatsApp testi",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        _selfTest.IsEnabled = false;
        try
        {
            _status.Text = "Test mesajı gönderiliyor...";
            string messageId = await _service.SendSelfTestTemplateAsync(recipient);
            _status.Text = $"Meta isteği kabul etti (mesaj kimliği: {messageId}); teslimat telefonunuzda ayrıca kontrol edilmeli.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _status.Text = "Test gönderimi doğrulanamadı: " + exception.GetBaseException().Message;
        }
        finally
        {
            _selfTest.IsEnabled = true;
        }
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            _status.Text = "Meta bağlantısı test ediliyor...";
            _status.Text = await _service.TestConnectionAsync();
        }
        catch (Exception exception) when (exception is InvalidOperationException or HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or CryptographicException or JsonException)
        {
            _status.Text = exception.GetBaseException().Message;
        }
    }

    private void RemoveSettings()
    {
        try
        {
            _service.RemoveSettings();
            _accessToken.Clear();
            _phoneNumberId.Clear();
            _templateName.Clear();
            _status.Text = "WhatsApp Business API ayarları kaldırıldı.";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _status.Text = "WhatsApp API ayarları silinemedi: " + exception.Message;
        }
    }
}
