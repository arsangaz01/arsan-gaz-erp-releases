using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Views;

public partial class CrmWindow : Window
{
    private List<CrmCompany> _companies = new();
    private CrmCompany? _selected;
    public CrmWindow(){InitializeComponent();WindowWorkAreaService.Fit(this);Loaded += async (_,_) => await LoadAsync();}

    private async Task LoadAsync()
    {
        try
        {
            await new CrmSchemaService().EnsureAsync();
            await using CrmDbContext db = new();
            _companies = await db.CrmCompanies.AsNoTracking()
                .OrderBy(company => company.FollowUpAtUtc == null)
                .ThenBy(company => company.FollowUpAtUtc)
                .ThenByDescending(company => company.Score)
                .ThenBy(company => company.CompanyName)
                .ToListAsync();
            ApplyFilter();
            int overdue = _companies.Count(company => company.FollowUpAtUtc < DateTime.UtcNow);
            decimal pipelineValue = _companies
                .Where(company => company.Status != "Müşteri Oldu" && company.Status != "Kaybedildi")
                .Sum(company => company.ExpectedValue);
            StatusText.Text =
                $"Paylaşılan CRM · {_companies.Count:N0} firma · {overdue:N0} gecikmiş takip · " +
                $"Açık fırsat: {pipelineValue.ToString("C", CultureInfo.CurrentCulture)} · " +
                $"Son yenileme {DateTime.Now:HH:mm}";
        }
        catch (Exception exception)
        {
            StatusText.Text = "Ortak CRM açılamadı: " + exception.GetBaseException().Message;
            MessageBox.Show(
                this,
                StatusText.Text,
                "CRM erişim hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
    private void ApplyFilter()
    {
        string query = SearchBox.Text.Trim();
        string status = (StatusFilter.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Tümü";
        bool hasQuery = !string.IsNullOrWhiteSpace(query);

        CompaniesGrid.ItemsSource = _companies
            .Where(company =>
            {
                bool matchesQuery = !hasQuery
                    || company.CompanyName.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                    || (company.City?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false)
                    || (company.Sector?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
                return matchesQuery && (status == "Tümü" || company.Status == status);
            })
            .ToList();
    }
    private async Task LoadCardAsync(int id)
    {
        await using CrmDbContext db = new();
        _selected = await db.CrmCompanies.AsNoTracking().FirstAsync(company => company.Id == id);
        CompanyTitle.Text = _selected.CompanyName;
        PhoneBox.Text = _selected.Phone ?? "";
        EmailBox.Text = _selected.Email ?? "";
        WebsiteBox.Text = _selected.Website ?? "";
        AddressBox.Text = _selected.Address ?? "";
        AssignedToBox.Text = _selected.AssignedTo ?? "";
        ExpectedValueBox.Text = _selected.ExpectedValue.ToString("N2", CultureInfo.CurrentCulture);
        FollowUpDatePicker.SelectedDate = _selected.FollowUpAtUtc?.ToLocalTime().Date;
        WhatsAppOptInCheckBox.IsChecked = _selected.WhatsAppMarketingOptIn;
        WhatsAppOptInDatePicker.SelectedDate = _selected.WhatsAppOptInAtUtc?.ToLocalTime().Date;
        WhatsAppOptInEvidenceBox.Text = _selected.WhatsAppOptInEvidence ?? "";
        WhatsAppOptOutText.Text = _selected.WhatsAppOptOutAtUtc is DateTime optOutAt
            ? $"Pazarlama izni geri çekildi: {optOutAt.ToLocalTime():dd.MM.yyyy HH:mm}"
            : "";

        foreach (ComboBoxItem item in CompanyStatus.Items)
        {
            if (item.Content?.ToString() == _selected.Status)
            {
                CompanyStatus.SelectedItem = item;
                break;
            }
        }

        NotesGrid.ItemsSource = await db.CrmNotes.AsNoTracking()
            .Where(note => note.CrmCompanyId == id)
            .OrderByDescending(note => note.CreatedAtUtc)
            .Select(note => new NoteRow(note.CreatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"), note.NoteText))
            .ToListAsync();
        ActivitiesGrid.ItemsSource = await db.CrmActivities.AsNoTracking()
            .Where(activity => activity.CrmCompanyId == id)
            .OrderByDescending(activity => activity.ActivityAtUtc)
            .Select(activity => new ActivityRow(
                activity.ActivityAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm"),
                activity.ActivityType,
                activity.Description))
            .ToListAsync();
    }
    private async void ImportProspects_Click(object s, RoutedEventArgs e)
    {
        try
        {
            await new CrmSchemaService().EnsureAsync();
            await using ArsanGazDbContext sourceDb = new();
            var prospects = await sourceDb.Prospects.AsNoTracking().ToListAsync();
            await using CrmDbContext db = new();
            var existing = await db.CrmCompanies
                .Where(company => company.SourceProspectId != null)
                .Select(company => company.SourceProspectId!.Value)
                .ToListAsync();
            HashSet<int> existingIds = existing.ToHashSet();
            foreach (var prospect in prospects.Where(prospect => !existingIds.Contains(prospect.Id)))
            {
                db.CrmCompanies.Add(new CrmCompany
                {
                    SourceProspectId = prospect.Id,
                    CompanyName = prospect.CompanyName,
                    City = prospect.City,
                    Sector = prospect.SectorQuery,
                    Phone = prospect.Phone,
                    Website = prospect.Website,
                    Address = prospect.Address,
                    Score = prospect.OpportunityScore,
                    Status = "Yeni"
                });
            }

            int imported = prospects.Count(prospect => !existingIds.Contains(prospect.Id));
            await db.SaveChangesAsync();
            StatusText.Text = $"{imported:N0} aday CRM'e aktarıldı.";
            await LoadAsync();
        }
        catch (Exception exception)
        {
            string message = "Adaylar CRM'e aktarılamadı. CRM listesini yenileyip sonucu doğrulayın; gerekirse tekrar deneyin.";
            StatusText.Text = message;
            MessageBox.Show(
                this,
                message + Environment.NewLine + Environment.NewLine + "Teknik ayrıntı: "
                    + exception.GetBaseException().Message,
                "Aday içe aktarma hatası",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }
    private async void CompaniesGrid_SelectionChanged(object s,SelectionChangedEventArgs e){if(CompaniesGrid.SelectedItem is CrmCompany c)await LoadCardAsync(c.Id);}
    private void SearchBox_TextChanged(object s,TextChangedEventArgs e)=>ApplyFilter();
    private void StatusFilter_SelectionChanged(object s,SelectionChangedEventArgs e){if(IsLoaded)ApplyFilter();}
    private async void Refresh_Click(object s,RoutedEventArgs e)=>await LoadAsync();
    private async void SaveCompany_Click(object s, RoutedEventArgs e)
    {
        if (_selected is null)
            return;
        if (!decimal.TryParse(ExpectedValueBox.Text, NumberStyles.Number, CultureInfo.CurrentCulture, out decimal expectedValue) || expectedValue < 0)
        {
            StatusText.Text = "Beklenen satış tutarını sıfır veya daha büyük geçerli bir sayı olarak girin.";
            return;
        }

        bool hasMarketingConsent = WhatsAppOptInCheckBox.IsChecked == true;
        DateTime? consentDate = WhatsAppOptInDatePicker.SelectedDate?.Date;
        string? consentEvidence = string.IsNullOrWhiteSpace(WhatsAppOptInEvidenceBox.Text)
            ? null
            : WhatsAppOptInEvidenceBox.Text.Trim();
        if (hasMarketingConsent &&
            (consentDate is null || consentDate > DateTime.Today || string.IsNullOrWhiteSpace(consentEvidence)))
        {
            StatusText.Text = "WhatsApp izni için geçmiş/geçerli bir tarih ve izin kanıtı/kaynağı girin.";
            return;
        }

        await using CrmDbContext db = new();
        CrmCompany? company = await db.CrmCompanies.FindAsync(_selected.Id);
        if (company is null)
            return;

        string previousStatus = company.Status;
        string? previousOwner = company.AssignedTo;
        DateTime? previousFollowUp = company.FollowUpAtUtc;
        bool previousMarketingConsent = company.WhatsAppMarketingOptIn;
        DateTime? previousConsentDate = company.WhatsAppOptInAtUtc;
        string? previousConsentEvidence = company.WhatsAppOptInEvidence;
        company.Phone = PhoneBox.Text.Trim();
        company.Email = EmailBox.Text.Trim();
        company.Website = WebsiteBox.Text.Trim();
        company.Address = AddressBox.Text.Trim();
        company.AssignedTo = string.IsNullOrWhiteSpace(AssignedToBox.Text) ? null : AssignedToBox.Text.Trim();
        company.ExpectedValue = expectedValue;
        company.FollowUpAtUtc = FollowUpDatePicker.SelectedDate is DateTime followUpDate
            ? DateTime.SpecifyKind(followUpDate.Date, DateTimeKind.Local).ToUniversalTime()
            : null;
        company.Status = (CompanyStatus.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? company.Status;

        if (hasMarketingConsent)
        {
            company.WhatsAppMarketingOptIn = true;
            company.WhatsAppOptInAtUtc = DateTime.SpecifyKind(consentDate!.Value, DateTimeKind.Local).ToUniversalTime();
            company.WhatsAppOptInEvidence = consentEvidence;
            company.WhatsAppOptOutAtUtc = null;
        }
        else if (company.WhatsAppMarketingOptIn)
        {
            company.WhatsAppMarketingOptIn = false;
            company.WhatsAppOptOutAtUtc = DateTime.UtcNow;
        }

        if (previousStatus != company.Status || previousOwner != company.AssignedTo || previousFollowUp != company.FollowUpAtUtc)
        {
            db.CrmActivities.Add(new CrmActivity
            {
                CrmCompanyId = company.Id,
                ActivityType = "Satış güncellemesi",
                Description =
                    $"Durum: {previousStatus} → {company.Status}; sorumlu: {previousOwner ?? "Atanmadı"} → {company.AssignedTo ?? "Atanmadı"}; " +
                    $"takip tarihi: {previousFollowUp?.ToLocalTime().ToString("dd.MM.yyyy") ?? "Yok"} → {company.FollowUpAtUtc?.ToLocalTime().ToString("dd.MM.yyyy") ?? "Yok"}"
            });
        }

        if (previousMarketingConsent != company.WhatsAppMarketingOptIn ||
            (company.WhatsAppMarketingOptIn &&
             (previousConsentDate != company.WhatsAppOptInAtUtc ||
              !string.Equals(previousConsentEvidence, company.WhatsAppOptInEvidence, StringComparison.Ordinal))))
        {
            db.CrmActivities.Add(new CrmActivity
            {
                CrmCompanyId = company.Id,
                ActivityType = "WhatsApp izin kaydı",
                Description = company.WhatsAppMarketingOptIn
                    ? previousMarketingConsent
                        ? $"Pazarlama izni kaydı güncellendi: {previousConsentDate?.ToLocalTime():dd.MM.yyyy} / {previousConsentEvidence ?? "kaynak belirtilmemiş"} → {company.WhatsAppOptInAtUtc?.ToLocalTime():dd.MM.yyyy} / {company.WhatsAppOptInEvidence}"
                        : $"Pazarlama izni kaydedildi ({company.WhatsAppOptInAtUtc?.ToLocalTime():dd.MM.yyyy}); kaynak: {company.WhatsAppOptInEvidence}"
                    : $"Pazarlama izni geri çekildi ({company.WhatsAppOptOutAtUtc?.ToLocalTime():dd.MM.yyyy HH:mm}); önceki izin kanıtı saklandı."
            });
        }

        await db.SaveChangesAsync();
        StatusText.Text = "Firma, satış takibi ve WhatsApp izin kaydı kaydedildi.";
        await LoadAsync();
        await LoadCardAsync(company.Id);
    }
    private async void AddNote_Click(object s,RoutedEventArgs e){if(_selected is null||string.IsNullOrWhiteSpace(NoteBox.Text))return;await using CrmDbContext db=new();db.CrmNotes.Add(new CrmNote{CrmCompanyId=_selected.Id,NoteText=NoteBox.Text.Trim()});db.CrmActivities.Add(new CrmActivity{CrmCompanyId=_selected.Id,ActivityType="Not",Description="CRM notu eklendi"});await db.SaveChangesAsync();NoteBox.Clear();await LoadCardAsync(_selected.Id);}
    private void Phone_Click(object s,RoutedEventArgs e){if(!string.IsNullOrWhiteSpace(PhoneBox.Text))Process.Start(new ProcessStartInfo("tel:"+PhoneBox.Text.Trim()){UseShellExecute=true});}
    private void WhatsApp_Click(object s,RoutedEventArgs e){string digits=new(PhoneBox.Text.Where(char.IsDigit).ToArray());if(digits.StartsWith("0"))digits="90"+digits[1..];if(!string.IsNullOrWhiteSpace(digits))Process.Start(new ProcessStartInfo("https://wa.me/"+digits){UseShellExecute=true});}
    private void WhatsAppSettings_Click(object s, RoutedEventArgs e)
    {
        new WhatsAppBusinessSettingsWindow(new WhatsAppBusinessService()) { Owner = this }.ShowDialog();
    }

    private async void SendWhatsAppTemplate_Click(object s, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            StatusText.Text = "Önce CRM listesinden bir firma seçin.";
            return;
        }

        WhatsAppBusinessService service = new();
        if (!service.IsConfigured)
        {
            MessageBox.Show(this, "Önce WhatsApp Business API ayarlarını tamamlayın.", "WhatsApp ayarları",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        SendWhatsAppButton.IsEnabled = false;
        try
        {
            await using CrmDbContext db = new();
            CrmCompany? company = await db.CrmCompanies.FindAsync(_selected.Id);
            if (company is null)
            {
                StatusText.Text = "Seçili CRM firması artık bulunamıyor.";
                return;
            }

            DateTime? formConsentDate = WhatsAppOptInDatePicker.SelectedDate?.Date;
            DateTime? savedConsentDate = company.WhatsAppOptInAtUtc?.ToLocalTime().Date;
            bool unsavedChanges =
                !string.Equals(company.Phone ?? "", PhoneBox.Text.Trim(), StringComparison.Ordinal) ||
                company.WhatsAppMarketingOptIn != (WhatsAppOptInCheckBox.IsChecked == true) ||
                formConsentDate != savedConsentDate ||
                !string.Equals(company.WhatsAppOptInEvidence ?? "", WhatsAppOptInEvidenceBox.Text.Trim(), StringComparison.Ordinal);
            if (unsavedChanges)
            {
                StatusText.Text = "Gönderimden önce telefon ve izin bilgilerini kaydedin.";
                return;
            }

            if (!company.WhatsAppMarketingOptIn || company.WhatsAppOptOutAtUtc is not null ||
                company.WhatsAppOptInAtUtc is null || string.IsNullOrWhiteSpace(company.WhatsAppOptInEvidence))
            {
                StatusText.Text = "Bu firmada açık WhatsApp pazarlama izni kaydı yok; mesaj gönderilmedi.";
                return;
            }

            string recipient;
            try
            {
                recipient = WhatsAppBusinessService.NormalizeE164(company.Phone ?? "");
            }
            catch (ArgumentException exception)
            {
                StatusText.Text = exception.Message;
                return;
            }

            WhatsAppBusinessService.Settings settings = service.LoadSettings();
            DateTime retryBlockStartsAt = DateTime.UtcNow.AddHours(-24);
            bool recentAttempt = await db.CrmActivities.AnyAsync(activity =>
                activity.CrmCompanyId == company.Id &&
                activity.ActivityType == "WhatsApp API gönderimi" &&
                activity.ActivityAtUtc >= retryBlockStartsAt);
            if (recentAttempt)
            {
                StatusText.Text = "Bu firmaya son 24 saat içinde WhatsApp API gönderim denemesi yapılmış; tekrar engellendi.";
                return;
            }

            string confirmation =
                $"Firma: {company.CompanyName}\nAlıcı: +{recipient}\nOnaylı şablon: {settings.TemplateName} ({settings.LanguageCode})\n\n" +
                "Meta ücret uygulayabilir. Devam edilsin mi? İstek kabulü teslimatın doğrulandığı anlamına gelmez.";
            if (MessageBox.Show(this, confirmation, "WhatsApp şablonunu gönder",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            await db.Entry(company).ReloadAsync();
            if (!company.WhatsAppMarketingOptIn || company.WhatsAppOptOutAtUtc is not null ||
                company.WhatsAppOptInAtUtc is null || company.WhatsAppOptInAtUtc > DateTime.UtcNow ||
                string.IsNullOrWhiteSpace(company.WhatsAppOptInEvidence) ||
                !string.Equals(WhatsAppBusinessService.NormalizeE164(company.Phone ?? ""), recipient, StringComparison.Ordinal))
            {
                StatusText.Text = "Onay penceresi açıkken telefon veya izin kaydı değişti; güvenlik için gönderim yapılmadı. Kartı yenileyip tekrar kontrol edin.";
                return;
            }

            CrmActivity attempt = new()
            {
                CrmCompanyId = company.Id,
                ActivityType = "WhatsApp API gönderimi",
                Description = $"İstek başlatıldı; şablon: {settings.TemplateName}; alıcı: +{recipient}. Tekrar denemeyi önlemek için 24 saatlik bekleme uygulandı."
            };

            DateTime attemptAtUtc = DateTime.UtcNow;
            DateTime retryCutoffUtc = attemptAtUtc.AddHours(-24);
            string attemptTime = attemptAtUtc.ToString("O", CultureInfo.InvariantCulture);
            string retryCutoff = retryCutoffUtc.ToString("O", CultureInfo.InvariantCulture);
            int cooldownReserved = await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO CrmWhatsAppSendCooldowns (CrmCompanyId, LastAttemptAtUtc)
                VALUES ({company.Id}, {attemptTime})
                ON CONFLICT(CrmCompanyId) DO UPDATE SET LastAttemptAtUtc = excluded.LastAttemptAtUtc
                WHERE CrmWhatsAppSendCooldowns.LastAttemptAtUtc <= {retryCutoff};
                """);
            if (cooldownReserved == 0)
            {
                StatusText.Text = "Başka bir oturum son 24 saat içinde gönderim hakkını ayırdı; tekrar engellendi.";
                return;
            }

            attempt.ActivityAtUtc = attemptAtUtc;
            db.CrmActivities.Add(attempt);
            await db.SaveChangesAsync();

            string messageId;
            try
            {
                messageId = await service.SendApprovedTemplateAsync(recipient);
            }
            catch (Exception sendException)
            {
                attempt.Description =
                    $"Gönderim sonucu doğrulanamadı veya API isteği reddedildi; şablon: {settings.TemplateName}; " +
                    $"alıcı: +{recipient}; hata: {sendException.GetBaseException().Message}. Çift gönderimi önlemek için 24 saatlik bekleme sürüyor.";
                try
                {
                    await db.SaveChangesAsync();
                }
                catch (Exception logException)
                {
                    StatusText.Text =
                        $"Gönderim sonucu belirsiz: {sendException.GetBaseException().Message}. " +
                        $"CRM aktivitesi de güncellenemedi: {logException.GetBaseException().Message}. Yeniden göndermeyin; aktivite kaydını kontrol edin.";
                    MessageBox.Show(this, StatusText.Text, "WhatsApp gönderim sonucu",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return;
                }

                StatusText.Text = "WhatsApp gönderimi doğrulanamadı: " + sendException.GetBaseException().Message;
                MessageBox.Show(this, StatusText.Text, "WhatsApp gönderim sonucu",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                await LoadCardAsync(company.Id);
                return;
            }

            attempt.Description =
                $"Meta API isteği kabul etti; şablon: {settings.TemplateName}; alıcı: +{recipient}; mesaj kimliği: {messageId}. Teslimat teyit edilmedi.";
            try
            {
                await db.SaveChangesAsync();
            }
            catch (Exception logException)
            {
                StatusText.Text =
                    $"Meta API isteği kabul etti (mesaj kimliği: {messageId}), ancak CRM aktivitesi güncellenemedi: " +
                    $"{logException.GetBaseException().Message}. 24 saat içinde yeniden göndermeyin.";
                MessageBox.Show(this, StatusText.Text, "WhatsApp gönderim kaydı",
                    MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            StatusText.Text = "Meta API isteği kabul etti; teslimat durumu doğrulanmadı.";

            try
            {
                await LoadCardAsync(company.Id);
            }
            catch (Exception refreshException)
            {
                StatusText.Text =
                    $"Meta API isteği kabul etti ve aktivite kaydedildi; CRM kartı yenilenemedi: {refreshException.GetBaseException().Message}";
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "WhatsApp işlemi tamamlanamadı: " + exception.GetBaseException().Message;
            MessageBox.Show(this, StatusText.Text, "WhatsApp API hatası",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SendWhatsAppButton.IsEnabled = true;
        }
    }
    private void Website_Click(object s,RoutedEventArgs e){string url=WebsiteBox.Text.Trim();if(!string.IsNullOrWhiteSpace(url))Process.Start(new ProcessStartInfo(url.StartsWith("http",StringComparison.OrdinalIgnoreCase)?url:"https://"+url){UseShellExecute=true});}
    private sealed record NoteRow(string CreatedAtLocal,string NoteText);
    private sealed record ActivityRow(string ActivityAtLocal,string ActivityType,string Description);
}
