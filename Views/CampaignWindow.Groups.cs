using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.Win32;

namespace ArsanGazERP.Views;

public sealed partial class CampaignWindow
{
    private static readonly (string Label, int Days)[] RepeatOptions =
    [
        ("Tek sefer", 0), ("Her gün", 1), ("Her hafta", 7), ("Her 15 günde bir", 15), ("Her ay (30 gün)", 30)
    ];

    private readonly ListBox groupList = new() { Height = 150, DisplayMemberPath = nameof(CampaignGroup.Name) };
    private readonly TextBox groupName = new();
    private readonly ComboBox groupPopulation = new() { ItemsSource = Enum.GetValues<CampaignPopulation>(), SelectedIndex = 0 };
    private readonly ListBox groupMemberList = new() { SelectionMode = SelectionMode.Extended, Height = 170, DisplayMemberPath = nameof(CampaignContactOption.DisplayName) };
    private readonly TextBlock groupInfo = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBox groupSearch = new();
    private readonly ListBox groupSearchResults = new() { SelectionMode = SelectionMode.Extended, Height = 170, DisplayMemberPath = nameof(CampaignContactOption.DisplayName) };
    private readonly TextBox groupEvidence = new();
    private readonly ComboBox quickChannel = new() { ItemsSource = Enum.GetValues<CampaignChannel>(), SelectedIndex = 0 };
    private readonly ComboBox quickSender = new() { DisplayMemberPath = nameof(CampaignSenderOption.DisplayName) };
    private readonly TextBox quickSubject = new();
    private readonly TextBox quickBody = new() { AcceptsReturn = true, Height = 150, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBox quickTemplate = new();
    private readonly TextBox quickLanguage = new() { Text = "tr" };
    private readonly TextBox quickParameters = new() { AcceptsReturn = true, Height = 60, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly ListBox quickAttachments = new() { Height = 90, AllowDrop = true };
    private readonly TextBox quickStart = new() { Text = DateTime.Now.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
    private readonly TextBox quickWindows = new() { Text = "09:00-18:00" };
    private readonly TextBox quickSpacing = new() { Text = "60" };
    private readonly ComboBox quickRepeat = new() { ItemsSource = RepeatOptions.Select(item => item.Label).ToArray(), SelectedIndex = 0 };
    private readonly TextBox quickEnd = new();
    private readonly TextBlock quickResult = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0) };
    private readonly List<CampaignContactOption> groupMembers = [];
    private readonly List<string> quickAttachmentPaths = [];
    private CampaignGroup? selectedGroup;
    private WhatsAppTemplateApproval? quickVerifiedTemplate;

    private UIElement BuildGroupTab()
    {
        var left = new StackPanel { Margin = new Thickness(18, 18, 9, 18) };
        left.Children.Add(Heading("1) Müşteri grupları"));
        left.Children.Add(groupList);
        Add(left, "Grup adı", groupName);
        Add(left, "Müşteri kaynağı (CRM firmaları / ERP müşterileri)", groupPopulation);
        Actions(left, ("Yeni grup", NewGroupAsync), ("Grubu kaydet", SaveGroupAsync), ("Grubu sil", DeleteGroupAsync));
        left.Children.Add(new TextBlock { Text = "Gruptaki müşteriler", Margin = new Thickness(0, 10, 0, 4) });
        left.Children.Add(groupMemberList);
        left.Children.Add(groupInfo);
        Actions(left, ("Seçilileri gruptan çıkar", RemoveGroupMembersAsync));
        Add(left, "Müşteri ara (ad, telefon, e-posta, il, sektör)", groupSearch);
        Actions(left, ("Ara", SearchGroupContactsAsync));
        left.Children.Add(groupSearchResults);
        Actions(left, ("Seçilileri gruba ekle", AddSelectedToGroupAsync), ("Listelenenlerin hepsini ekle", AddListedToGroupAsync));
        left.Children.Add(Heading("2) İletişim izni (KVKK / İYS)"));
        left.Children.Add(new TextBlock
        {
            Text = "Tanıtım mesajı yalnızca izin kaydı olan müşterilere gider. İzni nasıl aldığınızı yazın (ör. 'İYS onaylı - 2026 sözleşme formu'). Seçili kanal sağ taraftaki kanal kutusudur.",
            TextWrapping = TextWrapping.Wrap
        });
        Add(left, "İzin kanıtı / açıklaması", groupEvidence);
        Actions(left, ("Grup üyeleri için izin kaydet", () => GroupConsentAsync(true)), ("İzni geri çek", () => GroupConsentAsync(false)));

        var right = new StackPanel { Margin = new Thickness(9, 18, 18, 18) };
        right.Children.Add(Heading("3) Mesaj"));
        Add(right, "Kanal", quickChannel);
        Add(right, "Gönderen hesap", quickSender);
        Actions(right, ("Gönderenleri yenile", LoadQuickSendersAsync), ("Microsoft Mail.Send izni ver", QuickMailConsentAsync));
        Add(right, "E-posta konusu", quickSubject);
        Add(right, "E-posta metni", quickBody);
        Add(right, "WhatsApp: Meta onaylı şablon adı", quickTemplate);
        Add(right, "WhatsApp: şablon dili", quickLanguage);
        Add(right, "WhatsApp: şablon değişkenleri (her satıra bir değer)", quickParameters);
        Actions(right, ("WhatsApp şablonunu doğrula", QuickVerifyTemplateAsync));
        right.Children.Add(new TextBlock { Text = "Ekler (PDF, Excel, Word, PowerPoint, görsel, CSV/TXT) - dosyaları bu kutuya sürükleyin", Margin = new Thickness(0, 10, 0, 4) });
        right.Children.Add(quickAttachments);
        Actions(right, ("Dosya ekle...", AddQuickAttachmentsAsync), ("Seçili eki kaldır", RemoveQuickAttachmentAsync));
        right.Children.Add(Heading("4) Zamanlama"));
        Add(right, "Başlangıç (yyyy-MM-dd HH:mm)", quickStart);
        Add(right, "Gönderim saat aralığı (ör. 09:00-18:00)", quickWindows);
        Add(right, "İki müşteri arası bekleme (saniye, en az 30)", quickSpacing);
        Add(right, "Tekrar", quickRepeat);
        Add(right, "Bitiş (isteğe bağlı, yyyy-MM-dd HH:mm; tekrarı burada durdurur)", quickEnd);
        var send = new Button
        {
            Content = "GRUBA GÖNDER  (her müşteriye ayrı mesaj)",
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(16, 12, 16, 12),
            FontWeight = FontWeights.Bold
        };
        send.Click += async (_, _) => await RunAsync(SendToGroupAsync);
        right.Children.Add(send);
        right.Children.Add(quickResult);

        quickAttachments.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
                AddAttachmentPaths(files);
        };
        groupList.SelectionChanged += async (_, _) => await RunAsync(OpenSelectedGroupAsync);
        groupPopulation.SelectionChanged += (_, _) =>
        {
            if (selectedGroup is not null && groupPopulation.SelectedItem is CampaignPopulation chosen && chosen == selectedGroup.Population)
                return;
            groupMembers.Clear();
            groupSearchResults.ItemsSource = null;
            RefreshMemberList();
        };
        quickChannel.SelectionChanged += async (_, _) =>
        {
            quickVerifiedTemplate = null;
            await RunAsync(LoadQuickSendersAsync);
        };

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(right, 1);
        grid.Children.Add(left);
        grid.Children.Add(right);
        return new ScrollViewer { Content = grid, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static TextBlock Heading(string text) =>
        new() { Text = text, FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };

    private CampaignPopulation GroupPopulation =>
        (CampaignPopulation)(groupPopulation.SelectedItem ?? throw new InvalidOperationException("Müşteri kaynağını seçin."));
    private CampaignChannel QuickChannel =>
        (CampaignChannel)(quickChannel.SelectedItem ?? throw new InvalidOperationException("Kanal seçin."));

    private async Task LoadGroupsAsync()
    {
        Guid? keep = selectedGroup?.Id;
        IReadOnlyList<CampaignGroup> groups = await Service.ListGroupsAsync(closing.Token);
        groupList.ItemsSource = groups;
        if (keep is not null)
            groupList.SelectedItem = groups.FirstOrDefault(item => item.Id == keep);
    }

    private async Task LoadQuickSendersAsync()
    {
        quickSender.ItemsSource = await Service.GetAvailableSendersAsync(QuickChannel, closing.Token);
        quickSender.SelectedIndex = 0;
        if (quickSender.Items.Count == 0)
            quickResult.Text = QuickChannel == CampaignChannel.Email
                ? "E-posta gönderen hesabı yok: Agent Control Center > Bağlantılar sekmesinden Microsoft hesabıyla giriş yapın, sonra 'Microsoft Mail.Send izni ver'."
                : "WhatsApp Business API ayarlı değil: ana menüden WhatsApp Business ayarlarını yapın.";
    }

    private async Task OpenSelectedGroupAsync()
    {
        if (groupList.SelectedItem is not CampaignGroup group)
            return;
        selectedGroup = group;
        groupName.Text = group.Name;
        groupPopulation.SelectedItem = group.Population;
        groupMembers.Clear();
        groupMembers.AddRange(await Service.GetGroupMembersAsync(group.Id, closing.Token));
        RefreshMemberList();
    }

    private void RefreshMemberList()
    {
        groupMemberList.ItemsSource = null;
        groupMemberList.ItemsSource = groupMembers.OrderBy(item => item.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToArray();
        int withEmail = groupMembers.Count(item => !string.IsNullOrWhiteSpace(item.Email));
        int withPhone = groupMembers.Count(item => !string.IsNullOrWhiteSpace(item.Phone));
        groupInfo.Text = $"{groupMembers.Count} müşteri - e-postası olan: {withEmail}, telefonu olan: {withPhone}" +
            (selectedGroup is null ? " (kaydedilmedi)" : "");
    }

    private Task NewGroupAsync()
    {
        selectedGroup = null;
        groupList.SelectedItem = null;
        groupName.Text = "";
        groupMembers.Clear();
        RefreshMemberList();
        return Task.CompletedTask;
    }

    private async Task SaveGroupAsync()
    {
        selectedGroup = await Service.SaveGroupAsync(selectedGroup?.Id, groupName.Text, GroupPopulation,
            groupMembers.Select(item => item.ContactId).ToArray(), closing.Token);
        await LoadGroupsAsync();
        RefreshMemberList();
        quickResult.Text = $"'{selectedGroup.Name}' grubu kaydedildi ({selectedGroup.ContactIds.Count} müşteri).";
    }

    private async Task DeleteGroupAsync()
    {
        CampaignGroup group = selectedGroup ?? throw new InvalidOperationException("Silinecek grubu seçin.");
        if (!Confirm($"'{group.Name}' grubu silinsin mi? Müşteri kayıtları ve geçmiş gönderimler silinmez.")) return;
        await Service.DeleteGroupAsync(group.Id, closing.Token);
        await NewGroupAsync();
        await LoadGroupsAsync();
    }

    private async Task SearchGroupContactsAsync() =>
        groupSearchResults.ItemsSource = await Service.GetAudienceContactsAsync(GroupPopulation, groupSearch.Text, 1000, closing.Token);

    private Task AddSelectedToGroupAsync() => AddToGroup(groupSearchResults.SelectedItems.Cast<CampaignContactOption>());

    private Task AddListedToGroupAsync() =>
        AddToGroup(groupSearchResults.ItemsSource?.Cast<CampaignContactOption>() ?? []);

    private Task AddToGroup(IEnumerable<CampaignContactOption> contacts)
    {
        HashSet<int> existing = groupMembers.Select(item => item.ContactId).ToHashSet();
        int added = 0;
        foreach (CampaignContactOption contact in contacts)
        {
            if (existing.Add(contact.ContactId))
            {
                groupMembers.Add(contact);
                added++;
            }
        }
        if (groupMembers.Count > CampaignStore.MaximumAudienceSize)
            throw new InvalidOperationException($"Bir grupta en fazla {CampaignStore.MaximumAudienceSize} müşteri olabilir.");
        RefreshMemberList();
        quickResult.Text = $"{added} müşteri eklendi. Kalıcı olması için 'Grubu kaydet'e basın.";
        return Task.CompletedTask;
    }

    private Task RemoveGroupMembersAsync()
    {
        HashSet<int> remove = groupMemberList.SelectedItems.Cast<CampaignContactOption>().Select(item => item.ContactId).ToHashSet();
        groupMembers.RemoveAll(item => remove.Contains(item.ContactId));
        RefreshMemberList();
        quickResult.Text = $"{remove.Count} müşteri çıkarıldı. Kalıcı olması için 'Grubu kaydet'e basın.";
        return Task.CompletedTask;
    }

    private async Task GroupConsentAsync(bool granted)
    {
        CampaignGroup group = selectedGroup ?? throw new InvalidOperationException("Önce grubu kaydedin/seçin.");
        if (string.IsNullOrWhiteSpace(groupEvidence.Text))
            throw new InvalidOperationException("İzin kanıtını/açıklamasını yazın. İzin varsayılmaz.");
        string channelName = QuickChannel == CampaignChannel.Email ? "e-posta" : "WhatsApp";
        if (!Confirm($"'{group.Name}' grubundaki {group.ContactIds.Count} müşteri için {channelName} {(granted ? "İZİN" : "RET / izin geri çekme")} kaydı yazılacak.\n\nKanıt: {groupEvidence.Text.Trim()}\n\nBu müşterilerden gerçekten izin aldığınızı onaylıyor musunuz?")) return;
        ConsentBatchResult result = await Service.RecordGroupConsentAsync(group.Id, QuickChannel, granted, groupEvidence.Text, closing.Token);
        quickResult.Text = $"İzin kaydı: {result.Recorded} kaydedildi, {result.MissingEndpoint} müşterinin {channelName} bilgisi eksik/geçersiz, {result.Failed} kayıt yazılamadı.";
    }

    private async Task QuickMailConsentAsync()
    {
        if (QuickChannel != CampaignChannel.Email) throw new InvalidOperationException("Mail.Send yalnızca e-posta içindir.");
        var chosen = quickSender.SelectedItem as CampaignSenderOption ?? throw new InvalidOperationException("Gönderen hesabı seçin.");
        if (!Confirm("Seçili Microsoft hesabı için Mail.Send izni istenecek, tarayıcı açılabilir. Bu işlem mesaj göndermez. Devam?")) return;
        await Service.RequestMailSendConsentAsync(chosen.Identity, closing.Token);
        ProviderReadiness readiness = await Service.GetProviderReadinessAsync(CampaignChannel.Email, chosen.Identity, closing.Token);
        quickResult.Text = readiness.Ready ? "E-posta gönderimine hazır." : "Hazır değil: " + readiness.BlockReason;
    }

    private async Task QuickVerifyTemplateAsync()
    {
        if (QuickChannel != CampaignChannel.WhatsApp) throw new InvalidOperationException("Önce WhatsApp kanalını seçin.");
        if (!Confirm("Meta'dan şablonun onay durumu okunacak; müşteriye mesaj gönderilmez. Devam?")) return;
        quickVerifiedTemplate = await Service.VerifyWhatsAppTemplateAsync(quickTemplate.Text, quickLanguage.Text, closing.Token);
        quickResult.Text = $"Şablon doğrulandı: {quickVerifiedTemplate.Name} ({quickVerifiedTemplate.LanguageCode}), " +
            $"{quickVerifiedTemplate.BodyParameterCount} değişken, PDF başlığı: {(quickVerifiedTemplate.HasDocumentHeader ? "var" : "yok")}.";
    }

    private Task AddQuickAttachmentsAsync()
    {
        var picker = new OpenFileDialog
        {
            Multiselect = true,
            CheckFileExists = true,
            Filter = QuickChannel == CampaignChannel.WhatsApp
                ? "PDF (*.pdf)|*.pdf"
                : "Belgeler ve görseller|*.pdf;*.docx;*.xlsx;*.pptx;*.txt;*.csv;*.png;*.jpg;*.jpeg"
        };
        if (picker.ShowDialog(this) == true)
            AddAttachmentPaths(picker.FileNames);
        return Task.CompletedTask;
    }

    private void AddAttachmentPaths(IEnumerable<string> paths)
    {
        List<string> rejected = [];
        foreach (string path in paths)
        {
            try
            {
                _ = CampaignAttachmentPolicy.ContentType(path, QuickChannel);
                if (!File.Exists(path)) throw new FileNotFoundException();
                if (!quickAttachmentPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    quickAttachmentPaths.Add(path);
            }
            catch (Exception exception) when (exception is InvalidDataException or IOException)
            {
                rejected.Add(Path.GetFileName(path));
            }
        }
        RefreshAttachmentList();
        if (rejected.Count > 0)
            quickResult.Text = "Desteklenmeyen dosyalar eklenmedi: " + string.Join(", ", rejected);
    }

    private void RefreshAttachmentList()
    {
        quickAttachments.ItemsSource = null;
        quickAttachments.ItemsSource = quickAttachmentPaths
            .Select(path => $"{Path.GetFileName(path)}  ({new FileInfo(path).Length / 1024} KB)")
            .ToArray();
    }

    private Task RemoveQuickAttachmentAsync()
    {
        int index = quickAttachments.SelectedIndex;
        if (index < 0) throw new InvalidOperationException("Kaldırılacak eki seçin.");
        quickAttachmentPaths.RemoveAt(index);
        RefreshAttachmentList();
        return Task.CompletedTask;
    }

    private DateTimeOffset QuickLocalUtc(string text)
    {
        if (!DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime local))
            throw new InvalidOperationException($"Tarih '{text}' anlaşılamadı; yyyy-MM-dd HH:mm biçiminde yazın (ör. 2026-10-12 09:30).");
        var value = DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
        TimeZoneInfo zoneInfo = TimeZoneInfo.Local;
        if (zoneInfo.IsInvalidTime(value) || zoneInfo.IsAmbiguousTime(value))
            throw new InvalidOperationException("Seçili saat yaz/kış saati geçişinde geçersiz veya belirsiz. Başka bir saat seçin.");
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(value, zoneInfo), TimeSpan.Zero);
    }

    private async Task SendToGroupAsync()
    {
        CampaignGroup group = selectedGroup ?? throw new InvalidOperationException("Önce bir grup seçin veya kaydedin.");
        if (groupMembers.Count != group.ContactIds.Count ||
            groupMembers.Any(item => !group.ContactIds.Contains(item.ContactId)))
            throw new InvalidOperationException("Grupta kaydedilmemiş değişiklik var. Önce 'Grubu kaydet'e basın.");
        var chosen = quickSender.SelectedItem as CampaignSenderOption
            ?? throw new InvalidOperationException("Gönderen hesap yok. 'Gönderenleri yenile' ile seçin.");
        CampaignChannel channelValue = QuickChannel;
        WhatsAppTemplateApproval? template = null;
        IReadOnlyList<WhatsAppTemplateComponent> components = [];
        if (channelValue == CampaignChannel.Email)
        {
            if (string.IsNullOrWhiteSpace(quickSubject.Text) || string.IsNullOrWhiteSpace(quickBody.Text))
                throw new InvalidOperationException("E-posta konusu ve metni zorunludur.");
        }
        else
        {
            template = quickVerifiedTemplate ?? throw new InvalidOperationException("WhatsApp şablonunu önce doğrulayın.");
            if (template.Name != quickTemplate.Text.Trim() || template.LanguageCode != quickLanguage.Text.Trim())
                throw new InvalidOperationException("Şablon adı/dili değişti; yeniden doğrulayın.");
            string[] values = quickParameters.Text.Length == 0 ? [] : quickParameters.Text.Replace("\r", "").Split('\n');
            if (values.Length > 0) components = [new WhatsAppTemplateComponent("body", values)];
        }

        string[] parts = quickWindows.Text.Trim().Split('-');
        if (parts.Length != 2 ||
            !TimeOnly.TryParseExact(parts[0].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly windowStart) ||
            !TimeOnly.TryParseExact(parts[1].Trim(), "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly windowEnd))
            throw new InvalidOperationException("Saat aralığını 09:00-18:00 biçiminde yazın.");
        if (!int.TryParse(quickSpacing.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int spacingSeconds) || spacingSeconds < 30)
            throw new InvalidOperationException("İki müşteri arası bekleme en az 30 saniye olmalı.");
        int repeatDays = RepeatOptions[Math.Max(0, quickRepeat.SelectedIndex)].Days;
        DateTimeOffset startUtc = QuickLocalUtc(quickStart.Text);
        if (startUtc < DateTimeOffset.UtcNow.AddMinutes(-1))
            startUtc = DateTimeOffset.UtcNow;
        startUtc = new DateTimeOffset(startUtc.UtcDateTime, TimeSpan.Zero);
        DateTimeOffset? endUtc = string.IsNullOrWhiteSpace(quickEnd.Text) ? null : QuickLocalUtc(quickEnd.Text);
        string title = $"{group.Name} - {(channelValue == CampaignChannel.Email ? quickSubject.Text.Trim() : template!.Name)}";
        if (title.Length > 150) title = title[..150];

        GroupCampaignRequest request = new(group.Id, title, channelValue, chosen.Identity,
            quickSubject.Text, quickBody.Text, template, components, quickAttachmentPaths.ToArray(), TimeZoneInfo.Local.Id,
            startUtc, endUtc, [new CampaignDailyWindow(windowStart, windowEnd)], TimeSpan.FromSeconds(spacingSeconds), repeatDays);
        quickResult.Text = "Alıcılar ve ekler kontrol ediliyor...";
        GroupCampaignPreparation prepared = await Service.PrepareGroupCampaignAsync(request, closing.Token);
        ApprovalPreviewSummary summary = prepared.Summary;
        int minutes = (int)Math.Ceiling(summary.EligibleCount * spacingSeconds / 60.0);
        string repeatText = repeatDays == 0
            ? "Tek sefer"
            : $"{RepeatOptions.First(item => item.Days == repeatDays).Label} tekrar ({(endUtc is null ? "siz durdurana kadar" : "bitiş " + quickEnd.Text.Trim())})";
        string message =
            $"Grup: {group.Name} ({summary.GroupSize} müşteri)\n" +
            $"Gönderilecek: {summary.EligibleCount} müşteri - HER BİRİNE AYRI mesaj, kimse diğerlerinin adres/numarasını görmez.\n" +
            $"{CampaignService.DescribeExclusions(summary.Exclusions)}\n\n" +
            $"Kanal: {(channelValue == CampaignChannel.Email ? "E-posta" : "WhatsApp")} - Gönderen: {chosen.DisplayName}\n" +
            (channelValue == CampaignChannel.Email ? $"Konu: {quickSubject.Text.Trim()}\n" : $"Şablon: {template!.Name}\n") +
            $"Ekler: {(summary.AttachmentNames.Count == 0 ? "yok" : string.Join(", ", summary.AttachmentNames))}\n" +
            $"Başlangıç: {TimeZoneInfo.ConvertTime(startUtc, TimeZoneInfo.Local):dd.MM.yyyy HH:mm}, saat {quickWindows.Text.Trim()} arası, müşteri başına {spacingSeconds} sn (yaklaşık {minutes} dk)\n" +
            $"Tekrar: {repeatText}\n\n" +
            "Bu işlem GERÇEK müşterilere mesaj gönderir. Genel gönderim izni kapalıysa Gelişmiş sekmesinden ayrıca açılmalıdır. Bilgisayar ve Agent Control Center açık olmalı.\n\n" +
            "Gönderim başlatılsın mı? (Bu onay sağlayıcı ücret/kota koşullarını da kabul sayılır.)";
        if (!Confirm(message))
        {
            quickResult.Text = "Gönderim başlatılmadı. Hazırlanan kampanya 'Gelişmiş kampanya' listesinde taslak olarak duruyor.";
            return;
        }
        CampaignSnapshot launched = await Service.LaunchGroupCampaignAsync(prepared.Draft.Id, prepared.Draft.Revision,
            summary.ApprovalManifestHash, closing.Token);
        current = launched;
        quickResult.Text = $"Gönderim planlandı ({launched.State}). {summary.EligibleCount} müşteriye sırayla ayrı ayrı gönderilecek. " +
            "Durumu 'İzle ve yönet' sekmesinden takip edebilirsiniz.";
        await RefreshAsync();
    }
}
