using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.Win32;

namespace ArsanGazERP.Views;

public sealed partial class CampaignWindow : Window
{
    private readonly CancellationTokenSource closing = new();
    private CampaignTransport? transport;
    private CampaignService? service;
    private CampaignSnapshot? current;
    private ApprovalPreview? approval;
    private WhatsAppTemplateApproval? verifiedTemplate;
    private readonly List<Guid> attachmentIds = [];
    private int[] editorContactIds = [];
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBox name = new() { Text = "Tanıtım kampanyası" };
    private readonly ComboBox channel = new() { ItemsSource = Enum.GetValues<CampaignChannel>(), SelectedIndex = 0 };
    private readonly ComboBox population = new() { ItemsSource = Enum.GetValues<CampaignPopulation>(), SelectedIndex = 0 };
    private readonly ComboBox sender = new() { DisplayMemberPath = nameof(CampaignSenderOption.DisplayName), MinWidth = 260 };
    private readonly CheckBox all = new() { Content = "Tüm uygun alıcılar (onayda liste dondurulur)", IsChecked = false };
    private readonly TextBox search = new();
    private readonly ListBox contacts = new() { SelectionMode = SelectionMode.Extended, Height = 160, DisplayMemberPath = nameof(CampaignContactOption.DisplayName) };
    private readonly TextBox evidence = new();
    private readonly TextBox subject = new();
    private readonly TextBox body = new() { AcceptsReturn = true, Height = 140, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox template = new();
    private readonly TextBox language = new() { Text = "tr" };
    private readonly TextBox businessAccount = new();
    private readonly TextBox parameters = new() { AcceptsReturn = true, Height = 90, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox zone = new() { Text = TimeZoneInfo.Local.Id };
    private readonly TextBox start = new() { Text = DateTime.Now.AddHours(1).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
    private readonly TextBox end = new();
    private readonly TextBox windows = new() { Text = "09:00-18:00", AcceptsReturn = true, Height = 65 };
    private readonly TextBox spacing = new() { Text = "60" };
    private readonly TextBox cap = new() { Text = "1000" };
    private readonly TextBox cost = new() { Text = "0" };
    private readonly CheckBox costAcknowledged = new() { Content = "Sağlayıcı ücret/kota koşullarını kontrol ettim; tahmin garanti değildir." };
    private readonly TextBox detail = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MinHeight = 200 };
    private readonly ListBox campaigns = new() { Height = 220, DisplayMemberPath = "Input.Name" };
    private readonly DataGrid recipients = new() { IsReadOnly = true, AutoGenerateColumns = true, Height = 220 };
    private bool busy;

    public CampaignWindow(CampaignService? campaignService = null)
    {
        service = campaignService;
        Title = "Müşteri Kampanyaları - E-posta / WhatsApp";
        Width = 1100;
        Height = 820;
        MinWidth = 700;
        MinHeight = 500;
        WindowWorkAreaService.Fit(this);
        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Gruplar ve hızlı gönderim", Content = BuildGroupTab() });
        var editor = new StackPanel { Margin = new Thickness(18) };
        editor.Children.Add(new TextBlock
        {
            Text = "Taslak > alıcı/içerik onayı > genel gönderim izni > etkinleştirme. Her alıcıya ayrı gönderim yapılır; API'nin kabul etmesi teslimat anlamına gelmez. Bilgisayar ve Watcher açık olmalı.",
            TextWrapping = TextWrapping.Wrap,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 0, 0, 12)
        });
        Add(editor, "Kampanya adı", name);
        Add(editor, "Kanal (E-posta / WhatsApp)", channel);
        Add(editor, "Alıcı kaynağı (CRM firmaları / ERP müşterileri)", population);
        Add(editor, "Gönderici", sender);
        Actions(editor, ("Göndericileri yenile", LoadSendersAsync), ("Microsoft Mail.Send izni ver", MailConsentAsync));
        editor.Children.Add(all);
        Add(editor, "Müşteri ara (ad, e-posta, telefon, il, sektör veya CRM durumu)", search);
        Actions(editor, ("Müşterileri getir", LoadContactsAsync), ("Listelenenlerin hepsini seç", SelectListedContactsAsync), ("Uygun alıcıları önizle", PreviewAsync));
        editor.Children.Add(contacts);
        Add(editor, "Seçili müşterilerin kanal izni kanıtı / ret açıklaması", evidence);
        Actions(editor, ("Kanal iznini kaydet", () => ConsentAsync(true)), ("Kanal iznini geri çek", () => ConsentAsync(false)), ("İzin geçmişi", ShowConsentsAsync));
        Add(editor, "E-posta konusu", subject);
        Add(editor, "E-posta metni (WhatsApp için boş kalır)", body);
        Add(editor, "WhatsApp onaylı MARKETING şablon adı", template);
        Add(editor, "Şablon dil kodu", language);
        Add(editor, "WhatsApp Business Account ID (WABA; Phone Number ID değildir)", businessAccount);
        Actions(editor, ("WABA kimliğini yerel ayarlara kaydet", SaveBusinessAccountAsync));
        Add(editor, "WhatsApp gövde parametreleri (her satıra bir parametre)", parameters);
        Actions(editor, ("WhatsApp şablonunu sağlayıcıda doğrula", VerifyTemplateAsync));
        Add(editor, "Saat dilimi kimliği", zone);
        Add(editor, "Başlangıç yerel tarih/saat (yyyy-MM-dd HH:mm)", start);
        Add(editor, "Bitiş yerel tarih/saat (isteğe bağlı)", end);
        Add(editor, "Günlük saat pencereleri (satır başına HH:mm-HH:mm, gece aralığı desteklenir)", windows);
        Add(editor, "Alıcılar arası en az saniye (30-86400)", spacing);
        Add(editor, "En fazla alıcı (1-1000)", cap);
        Add(editor, "Tahmini toplam sağlayıcı maliyeti", cost);
        editor.Children.Add(costAcknowledged);
        Actions(editor, ("Taslağı kaydet", SaveAsync), ("Dosya ekle (önce taslağı kaydet)", AttachAsync), ("Eki kaldır ve taslağı kaydet", RemoveAttachmentAsync));
        Actions(editor, ("Kesin onay önizlemesi", PrepareAsync), ("Önizlenen kampanyayı onayla", ApproveAsync));
        tabs.Items.Add(new TabItem { Header = "Gelişmiş kampanya", Content = new ScrollViewer { Content = editor, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var monitor = new StackPanel { Margin = new Thickness(18) };
        Actions(monitor, ("Listeyi yenile", RefreshAsync), ("Seçili kampanyayı aç", OpenSelectedAsync), ("Yeni taslak", NewAsync));
        monitor.Children.Add(campaigns);
        Actions(monitor, ("Genel gönderim iznini aç", EnableAsync), ("Tüm gönderimleri durdur", DisableAsync));
        Actions(monitor, ("Onaylı kampanyayı etkinleştir", ActivateAsync), ("Duraklat", PauseAsync), ("Devam et", ResumeAsync), ("İptal et", CancelAsync));
        monitor.Children.Add(recipients);
        tabs.Items.Add(new TabItem { Header = "İzle ve yönet", Content = new ScrollViewer { Content = monitor } });
        var preview = new StackPanel { Margin = new Thickness(18) };
        preview.Children.Add(new TextBlock { Text = "Unknown: sonuç belirsiz, otomatik tekrar yok. Accepted: sağlayıcı kabul etti, teslimat kanıtı değil.", TextWrapping = TextWrapping.Wrap });
        preview.Children.Add(detail);
        tabs.Items.Add(new TabItem { Header = "Önizleme / durum", Content = new ScrollViewer { Content = preview } });
        UseTurkishLabels(channel, population, groupPopulation, quickChannel);
        var root = new DockPanel();
        status.Margin = new Thickness(12);
        DockPanel.SetDock(status, Dock.Bottom);
        root.Children.Add(status);
        root.Children.Add(tabs);
        Content = root;
        Loaded += async (_, _) => await RunAsync(async () =>
        {
            if (service is null)
            {
                transport = await CampaignTransport.CreateForCurrentWindowsUserAsync(cancellationToken: closing.Token);
                service = new CampaignService(CampaignStore.CreateForCurrentWindowsUser(), transport);
            }
            await LoadGroupsAsync();
            await LoadQuickSendersAsync();
            await RefreshAsync();
            await LoadSendersAsync();
        });
        Closing += (_, _) => closing.Cancel();
        Closed += (_, _) => transport?.Dispose();
        channel.SelectionChanged += (_, _) =>
        {
            verifiedTemplate = null;
            approval = null;
            sender.ItemsSource = null;
        };
    }

    private CampaignService Service => service ?? throw new InvalidOperationException("Kampanya hizmeti henüz hazır değil.");
    private CampaignChannel Channel => (CampaignChannel)(channel.SelectedItem ?? throw new InvalidOperationException("Kanal seçin."));
    private CampaignPopulation Population => (CampaignPopulation)(population.SelectedItem ?? throw new InvalidOperationException("Alıcı kaynağı seçin."));
    private CampaignSnapshot Current => current ?? throw new InvalidOperationException("Önce taslağı kaydedin veya kampanya seçin.");
    private AudienceSelection Selection
    {
        get
        {
            // Prefer the IDs loaded with the campaign so a filtered contact list cannot drop recipients on save.
            int[] fromList = contacts.SelectedItems.Cast<CampaignContactOption>().Select(c => c.ContactId).ToArray();
            int[] ids = editorContactIds.Length > 0
                ? editorContactIds.Union(fromList).Distinct().ToArray()
                : fromList;
            if (editorContactIds.Length > 0 && all.IsChecked != true)
            {
                int[] listed = contacts.Items.Cast<CampaignContactOption>().Select(c => c.ContactId).ToArray();
                int[] deselected = listed.Except(fromList).ToArray();
                if (deselected.Length > 0)
                    ids = ids.Except(deselected).ToArray();
            }
            return new(Population, ids, all.IsChecked == true);
        }
    }
    private static void UseTurkishLabels(params ComboBox[] boxes)
    {
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding { Converter = new EnumLabelConverter() });
        var itemTemplate = new DataTemplate { VisualTree = text };
        foreach (ComboBox box in boxes)
            box.ItemTemplate = itemTemplate;
    }
    private sealed class EnumLabelConverter : System.Windows.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value switch
        {
            CampaignChannel.Email => "E-posta",
            CampaignChannel.WhatsApp => "WhatsApp",
            CampaignPopulation.CrmCompanies => "CRM firmaları",
            CampaignPopulation.ErpCustomers => "ERP müşterileri",
            _ => value?.ToString() ?? ""
        };
        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
    }
    private static void Add(Panel panel, string label, Control control)
    {
        panel.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 10, 0, 4) });
        control.HorizontalAlignment = HorizontalAlignment.Stretch;
        panel.Children.Add(control);
    }
    private void Actions(Panel panel, params (string Label, Func<Task> Action)[] actions)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var action in actions)
        {
            var button = new Button { Content = action.Label, Margin = new Thickness(0, 0, 8, 6), Padding = new Thickness(12, 7, 12, 7) };
            button.Click += async (_, _) => await RunAsync(action.Action);
            row.Children.Add(button);
        }
        panel.Children.Add(row);
    }
    private async Task RunAsync(Func<Task> action)
    {
        if (busy) { status.Text = "İşlem sürüyor; bitmesini bekleyin."; return; }
        busy = true;
        try { await action(); status.Text = $"İşlem tamamlandı. Genel gönderim: {(service?.ExecutionEnabled == true ? "AÇIK" : "KAPALI")}"; }
        catch (OperationCanceledException) when (closing.IsCancellationRequested) { }
        catch (Exception exception)
        {
            status.Text = "İşlem başarısız: " + exception.Message;
            MessageBox.Show(this, exception.Message, "Kampanya işlemi başarısız", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { busy = false; }
    }
    private bool Confirm(string text) => MessageBox.Show(this, text, "Açık kullanıcı onayı", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
    private void Show(object value) => detail.Text = JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true });
    private async Task LoadSendersAsync()
    {
        sender.ItemsSource = await Service.GetAvailableSendersAsync(Channel, closing.Token);
        sender.SelectedIndex = 0;
        if (sender.Items.Count == 0) detail.Text = "Gönderici bulunamadı. E-posta için Microsoft hesabınızla uygulamanın Bağlantılar alanından giriş yapın; WhatsApp için Business API ayarlarını yapın.";
    }
    private async Task MailConsentAsync()
    {
        if (Channel != CampaignChannel.Email) throw new InvalidOperationException("Mail.Send yalnızca e-posta içindir.");
        var chosen = sender.SelectedItem as CampaignSenderOption ?? throw new InvalidOperationException("Gönderici seçin.");
        if (!Confirm("Seçili Microsoft hesabı için Mail.Send izni istenecek ve tarayıcı açılabilir. Bu işlem mesaj göndermez. Devam edilsin mi?")) return;
        await Service.RequestMailSendConsentAsync(chosen.Identity, closing.Token);
        Show(await Service.GetProviderReadinessAsync(Channel, chosen.Identity, closing.Token));
    }
    private Task SelectListedContactsAsync()
    {
        all.IsChecked = false;
        contacts.SelectAll();
        return Task.CompletedTask;
    }

    private async Task LoadContactsAsync() => contacts.ItemsSource = await Service.GetAudienceContactsAsync(Population, search.Text, 1000, closing.Token);
    private async Task PreviewAsync() => Show(await Service.PreviewAudienceAsync(Selection, Channel, closing.Token));
    private async Task ConsentAsync(bool granted)
    {
        var selected = contacts.SelectedItems.Cast<CampaignContactOption>().ToArray();
        if (selected.Length == 0 || string.IsNullOrWhiteSpace(evidence.Text)) throw new InvalidOperationException("Müşteri seçin ve izin/ret kanıtını girin. İzin varsayılmaz.");
        if (!Confirm($"{selected.Length} seçili kayıt için {Channel} {(granted ? "izin" : "ret")} kaydı yazılacak. Gerçek kanıtı kontrol ettiniz mi?")) return;
        foreach (var contact in selected)
        {
            string endpoint = Channel == CampaignChannel.Email ? contact.Email : contact.Phone;
            try
            {
                await Service.RecordConsentAsync(contact.Population, contact.ContactId, Channel, endpoint, granted, evidence.Text, closing.Token);
            }
            catch (CrmOptOutException) when (granted && Channel == CampaignChannel.WhatsApp)
            {
                if (!Confirm($"{contact.DisplayName}: CRM kartında WhatsApp pazarlama ret'i var. Bu kayıt ret'i kaldırır (KVKK). Yine de yazılsın mı?"))
                    throw;
                await Service.RecordConsentAsync(contact.Population, contact.ContactId, Channel, endpoint, granted, evidence.Text, closing.Token, overrideCrmOptOut: true);
            }
        }
        await ShowConsentsAsync();
    }
    private async Task ShowConsentsAsync()
    {
        var selected = contacts.SelectedItem as CampaignContactOption;
        Show(await Service.GetConsentLedgerAsync(Population, selected?.ContactId, closing.Token));
    }
    private Task SaveBusinessAccountAsync()
    {
        if (Channel != CampaignChannel.WhatsApp) throw new InvalidOperationException("Önce WhatsApp kanalını seçin.");
        WhatsAppBusinessService whatsApp = new();
        if (!whatsApp.IsConfigured)
            throw new InvalidOperationException("Önce WhatsApp ayarlarından erişim jetonunu kaydedin; WABA kimliği tek başına yazılamaz.");
        if (!Confirm("WABA kimliği şifreli yerel WhatsApp ayarlarına yazılacak. Bu işlem sağlayıcıya istek veya müşteriye mesaj göndermez. Devam edilsin mi?")) return Task.CompletedTask;
        whatsApp.SetBusinessAccountId(businessAccount.Text);
        verifiedTemplate = null;
        detail.Text = "WABA kaydedildi. Şablonu doğrulama düğmesiyle kontrol edin.";
        return Task.CompletedTask;
    }
    private async Task VerifyTemplateAsync()
    {
        if (Channel != CampaignChannel.WhatsApp) throw new InvalidOperationException("Önce WhatsApp kanalını seçin.");
        if (!Confirm("Meta API'den şablon onay durumu okunacak. Şablon adı/dili sağlayıcıya iletilir; müşteriye mesaj gönderilmez. Devam edilsin mi?")) return;
        verifiedTemplate = await Service.VerifyWhatsAppTemplateAsync(template.Text, language.Text, closing.Token);
        Show(verifiedTemplate);
    }
    private DateTimeOffset LocalUtc(string text)
    {
        var value = DateTime.SpecifyKind(DateTime.ParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture), DateTimeKind.Unspecified);
        var tz = CampaignWindowSchedule.ResolveTimeZone(zone.Text.Trim());
        if (tz.IsInvalidTime(value) || tz.IsAmbiguousTime(value)) throw new InvalidOperationException("Seçili saat yaz/kış saati geçişinde geçersiz veya belirsiz. Başka bir saat seçin.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, tz), TimeSpan.Zero);
    }
    private async Task SaveAsync()
    {
        var chosen = sender.SelectedItem as CampaignSenderOption ?? throw new InvalidOperationException("Göndericileri yenileyin ve seçin.");
        var ranges = windows.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Select(line =>
        {
            string[] parts = line.Trim().Split('-');
            if (parts.Length != 2) throw new FormatException("Saat penceresi HH:mm-HH:mm biçiminde olmalı.");
            return new CampaignDailyWindow(TimeOnly.ParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture), TimeOnly.ParseExact(parts[1].Trim(), "HH:mm", CultureInfo.InvariantCulture));
        }).ToArray();
        WhatsAppTemplateApproval? selectedTemplate = null;
        if (Channel == CampaignChannel.WhatsApp)
        {
            selectedTemplate = verifiedTemplate ?? throw new InvalidOperationException("WhatsApp şablonunu önce doğrulayın.");
            if (selectedTemplate.Name != template.Text.Trim() || selectedTemplate.LanguageCode != language.Text.Trim())
                throw new InvalidOperationException("Şablon adı/dili değişti; yeniden doğrulayın.");
        }
        var values = parameters.Text.Length == 0 ? [] : parameters.Text.Replace("\r", "").Split('\n');
        IReadOnlyList<WhatsAppTemplateComponent> components = Channel == CampaignChannel.WhatsApp && values.Length > 0 ? [new("body", values)] : [];
        var input = new CampaignDraftInput(name.Text, Channel, Selection, chosen.Identity,
            Channel == CampaignChannel.Email ? subject.Text : "", Channel == CampaignChannel.Email ? body.Text : "",
            selectedTemplate, components, attachmentIds.ToArray(), zone.Text.Trim(), LocalUtc(start.Text),
            string.IsNullOrWhiteSpace(end.Text) ? null : LocalUtc(end.Text), ranges,
            TimeSpan.FromSeconds(double.Parse(spacing.Text, CultureInfo.InvariantCulture)), int.Parse(cap.Text, CultureInfo.InvariantCulture),
            decimal.Parse(cost.Text, CultureInfo.InvariantCulture), costAcknowledged.IsChecked == true);
        var saved = await Service.SaveDraftAsync(current?.Id, input, current?.Revision ?? 0, closing.Token);
        current = await Service.GetAsync(saved.Id, closing.Token) ?? throw new InvalidOperationException("Kaydedilen kampanya okunamadı.");
        editorContactIds = current.Input.Audience.ContactIds.ToArray();
        approval = null;
        Show(current);
    }
    private async Task AttachAsync()
    {
        var campaign = Current;
        if (campaign.State != CampaignState.Draft) throw new InvalidOperationException("Yalnızca taslağa ek eklenebilir.");
        var picker = new OpenFileDialog
        {
            Filter = Channel == CampaignChannel.WhatsApp ? "PDF (*.pdf)|*.pdf" : "Belgeler ve görseller|*.pdf;*.docx;*.xlsx;*.pptx;*.txt;*.png;*.jpg;*.jpeg",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;
        var attached = await Service.StageAttachmentAsync(campaign.Id, picker.FileName, closing.Token);
        if (Channel == CampaignChannel.WhatsApp)
        {
            attachmentIds.Clear();
            attachmentIds.Add(attached.Id);
        }
        else
        {
            if (!attachmentIds.Contains(attached.Id))
                attachmentIds.Add(attached.Id);
            if (attachmentIds.Count > CampaignStore.MaximumEmailAttachmentCount)
                throw new InvalidOperationException($"E-postaya en fazla {CampaignStore.MaximumEmailAttachmentCount} dosya eklenebilir.");
        }
        await SaveAsync();
        Show(attached);
    }
    private async Task RemoveAttachmentAsync() { attachmentIds.Clear(); await SaveAsync(); }
    private async Task PrepareAsync() { approval = await Service.PrepareApprovalAsync(Current.Id, Current.Revision, closing.Token); Show(approval); }
    private async Task ApproveAsync()
    {
        var preview = approval ?? throw new InvalidOperationException("Önce kesin onay önizlemesini açın.");
        if (!Confirm($"Dondurulan {preview.Audience.EligibleCount} alıcı, gönderici, ekler, mesaj ve saat planı onaylanacak. Önizleme sekmesindeki listenin tamamını kontrol ettiniz ve maliyeti kabul ettiniz mi? Gönderim henüz başlamaz.")) return;
        current = await Service.ApproveAsync(Current.Id, Current.Revision, preview.ApprovalManifestHash, new(true, true, true), closing.Token);
        approval = null;
        Show(current);
    }
    private async Task RefreshAsync()
    {
        await Service.RefreshExecutionStateAsync(closing.Token);
        var listed = new List<CampaignSnapshot>();
        CampaignPage page;
        do
        {
            page = await Service.ListAsync(new(listed.Count, 100), closing.Token);
            listed.AddRange(page.Items);
        } while (listed.Count < page.TotalCount && page.Items.Count > 0);
        campaigns.ItemsSource = listed;
        if (current is not null)
        {
            current = await Service.GetAsync(current.Id, closing.Token);
            if (current is not null)
            {
                var outcomes = new List<CampaignRecipient>();
                RecipientPage recipientPage;
                do
                {
                    recipientPage = await Service.GetRecipientsAsync(current.Id, outcomes.Count, 500, closing.Token);
                    outcomes.AddRange(recipientPage.Items);
                } while (outcomes.Count < recipientPage.TotalCount && recipientPage.Items.Count > 0);
                recipients.ItemsSource = outcomes;
                Show(new { Campaign = current, ExecutionEnabled = Service.ExecutionEnabled, NextEligibleUtc = await Service.GetNextEligibleTimeAsync(current.Id, closing.Token) });
            }
        }
    }
    private async Task OpenSelectedAsync()
    {
        var chosen = campaigns.SelectedItem as CampaignSnapshot ?? throw new InvalidOperationException("Kampanya seçin.");
        current = await Service.GetAsync(chosen.Id, closing.Token) ?? throw new InvalidOperationException("Kampanya artık bulunamıyor.");
        var input = current.Input;
        name.Text = input.Name;
        channel.SelectedItem = input.Channel;
        population.SelectedItem = input.Audience.Population;
        all.IsChecked = input.Audience.SelectAllEligible;
        editorContactIds = input.Audience.ContactIds.ToArray();
        search.Text = "";
        await LoadContactsAsync();
        contacts.SelectedItems.Clear();
        foreach (CampaignContactOption contact in contacts.Items)
            if (editorContactIds.Contains(contact.ContactId)) contacts.SelectedItems.Add(contact);
        await LoadSendersAsync();
        sender.SelectedItem = sender.Items.Cast<CampaignSenderOption>().FirstOrDefault(item => item.Identity == input.SenderIdentity);
        subject.Text = input.Subject;
        body.Text = input.Content;
        verifiedTemplate = input.WhatsAppTemplate;
        template.Text = verifiedTemplate?.Name ?? "";
        language.Text = verifiedTemplate?.LanguageCode ?? "tr";
        parameters.Text = string.Join(Environment.NewLine, input.WhatsAppComponents.SelectMany(c => c.Parameters));
        attachmentIds.Clear();
        attachmentIds.AddRange(input.AttachmentIds);
        zone.Text = input.TimeZoneId;
        var tz = CampaignWindowSchedule.ResolveTimeZone(input.TimeZoneId);
        start.Text = TimeZoneInfo.ConvertTime(input.StartsAtUtc, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        end.Text = input.EndsAtUtc is null ? "" : TimeZoneInfo.ConvertTime(input.EndsAtUtc.Value, tz).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        windows.Text = string.Join(Environment.NewLine, input.DailyWindows.Select(w => $"{w.Start:HH:mm}-{w.End:HH:mm}"));
        spacing.Text = input.MinimumSpacing.TotalSeconds.ToString(CultureInfo.InvariantCulture);
        cap.Text = input.MaximumRecipients.ToString(CultureInfo.InvariantCulture);
        cost.Text = input.EstimatedCost.ToString(CultureInfo.InvariantCulture);
        costAcknowledged.IsChecked = input.CostAcknowledged;
        approval = null;
        await RefreshAsync();
    }
    private Task NewAsync()
    {
        current = null;
        approval = null;
        attachmentIds.Clear();
        editorContactIds = [];
        costAcknowledged.IsChecked = false;
        detail.Text = "Yeni taslak. İzin, onay ve etkinleştirme ayrı adımlardır.";
        return Task.CompletedTask;
    }
    private async Task EnableAsync()
    {
        if (!Confirm("GENEL GÖNDERİM İZNİ: onaylanmış ve etkin kampanyalar gerçek müşterilere mesaj gönderebilir, sağlayıcı ücreti doğabilir. Yapay zekâ mesajı değiştirmez; arka plan girişi sessizdir. Açılsın mı?")) return;
        await Service.EnableExecutionAsync(new(true, true, true), closing.Token);
        await RefreshAsync();
    }
    private async Task DisableAsync() { await Service.DisableExecutionAsync(closing.Token); await RefreshAsync(); }
    private async Task ActivateAsync()
    {
        if (!Confirm("Seçili kampanya kesin alıcı, gönderici, maliyet ve zamanlamayla GERÇEK gönderim için etkinleştirilecek. Devam edilsin mi?")) return;
        await ReloadCurrentAsync();
        current = await Service.ActivateAsync(Current.Id, Current.Revision, new(true, true, true), closing.Token);
        await RefreshAsync();
    }
    private async Task ReloadCurrentAsync() =>
        current = await Service.GetAsync(Current.Id, closing.Token) ?? throw new InvalidOperationException("Kampanya artık bulunamıyor.");
    private async Task PauseAsync() { await ReloadCurrentAsync(); current = await Service.PauseAsync(Current.Id, Current.Revision, closing.Token); await RefreshAsync(); }
    private async Task ResumeAsync()
    {
        if (!Confirm("Kampanyanın gerçek gönderimlerine onaylı alıcı/gönderici/maliyet ile devam edilsin mi?")) return;
        await ReloadCurrentAsync();
        current = await Service.ResumeAsync(Current.Id, Current.Revision, new(true, true, true), closing.Token);
        await RefreshAsync();
    }
    private async Task CancelAsync()
    {
        if (!Confirm("Kampanya iptal edilsin mi? Gönderilmiş veya hâlâ sağlayıcıda işlenen mesaj geri alınamaz.")) return;
        await ReloadCurrentAsync();
        current = await Service.CancelAsync(Current.Id, Current.Revision, closing.Token);
        await RefreshAsync();
    }
}
