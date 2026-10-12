using ArsanGazMobile.Services;

namespace ArsanGazMobile.Pages;

public sealed class CampaignPage : ContentPage
{
    private const string AllLabel = "Tümü";
    private static readonly string[] Modes =
    [
        "WhatsApp – yarı otomatik (gönder'e siz basarsınız)",
        "E-posta – zamanlanmış otomatik (Google Workspace)",
        "WhatsApp – zamanlanmış otomatik (Meta onaylı şablon)"
    ];

    private readonly ActivityIndicator _busy = new() { IsVisible = false, Color = Ui.Accent };
    private readonly Label _status = Ui.Note("Yükleniyor…");
    private readonly Picker _group = new() { Title = "Grup seçin" };
    private readonly VerticalStackLayout _newGroup = new() { IsVisible = false, Spacing = 6 };
    private readonly Entry _groupName = new() { Placeholder = "Grup adı (ör. Adana hastaneleri)" };
    private readonly Picker _city = new() { Title = "Şehir" };
    private readonly Picker _sector = new() { Title = "Sektör" };
    private readonly Picker _crmStatus = new() { Title = "CRM durumu" };
    private readonly Picker _require = new() { Title = "Koşul", ItemsSource = new[] { "Hepsi", "E-postası olanlar", "Cep telefonu olanlar" }, SelectedIndex = 0 };
    private readonly Label _groupPreview = Ui.Note(string.Empty);

    private readonly Picker _mode = new() { Title = "Gönderim yöntemi", ItemsSource = Modes, SelectedIndex = 0 };
    private readonly Entry _subject = new() { Placeholder = "E-posta konusu" };
    private readonly Editor _content = new() { Placeholder = "Mesaj metni. {firma} yazarsanız WhatsApp yarı otomatikte firma adıyla değişir.", AutoSize = EditorAutoSizeOption.TextChanges, HeightRequest = 140 };
    private readonly Label _pdfLabel = Ui.Note("PDF eklenmedi.");
    private string? _pdfLocal;
    private UploadedPdf? _pdfUploaded;

    private readonly VerticalStackLayout _semiAuto = new() { Spacing = 6 };
    private readonly Picker _waApp = new() { Title = "WhatsApp uygulaması" };
    private readonly Label _waProgress = Ui.Note(string.Empty);
    private readonly Label _waCurrent = new() { FontSize = 17, FontAttributes = FontAttributes.Bold };
    private List<WhatsAppContact> _waContacts = [];
    private int _waIndex;

    private readonly VerticalStackLayout _scheduled = new() { Spacing = 6, IsVisible = false };
    private readonly DatePicker _startDate = new() { Date = DateTime.Today, MinimumDate = DateTime.Today, Format = "dd.MM.yyyy" };
    private readonly TimePicker _startTime = new() { Time = DateTime.Now.AddMinutes(10).TimeOfDay, Format = "HH:mm" };
    private readonly TimePicker _windowStart = new() { Time = new TimeSpan(9, 0, 0), Format = "HH:mm" };
    private readonly TimePicker _windowEnd = new() { Time = new TimeSpan(18, 0, 0), Format = "HH:mm" };
    private readonly Entry _spacing = new() { Text = "90", Keyboard = Keyboard.Numeric };
    private readonly Picker _repeat = new() { Title = "Tekrar", ItemsSource = new[] { "Tek sefer", "Her 7 günde bir", "Her 14 günde bir", "Her 30 günde bir" }, SelectedIndex = 0 };
    private readonly Entry _template = new() { Placeholder = "Meta şablon adı (ör. arsangaz_tanitim)", IsVisible = false };
    private readonly Label _prepared = Ui.Note(string.Empty);
    private readonly Button _launch;
    private PreparedCampaign? _preparedCampaign;

    private readonly VerticalStackLayout _campaigns = new() { Spacing = 4 };
    private List<CampaignListItem> _campaignItems = [];
    private bool _loaded;

    public CampaignPage()
    {
        Title = "Kampanya";
        _launch = Ui.Primary("✅ Onayla ve başlat", async (_, _) => await LaunchAsync());
        _launch.IsVisible = false;
        _mode.SelectedIndexChanged += (_, _) => ApplyMode();
        _group.SelectedIndexChanged += (_, _) => ResetWhatsAppQueue();
        _waApp.SelectedIndexChanged += (_, _) => Preferences.Default.Set("campaign_wa_app", _waApp.SelectedIndex);

        _newGroup.Children.Add(_groupName);
        _newGroup.Children.Add(Row(_city, _sector));
        _newGroup.Children.Add(Row(_crmStatus, _require));
        _newGroup.Children.Add(Row(
            Ui.Secondary("Önizle", async (_, _) => await PreviewGroupAsync()),
            Ui.Primary("Grubu kaydet", async (_, _) => await CreateGroupAsync())));
        _newGroup.Children.Add(_groupPreview);

        _semiAuto.Children.Add(Ui.Note("Telefonunuzdaki WhatsApp her firma için mesaj ve PDF hazır açılır; siz gönder'e basıp geri dönersiniz. " +
                                       "Numaranızın engellenmemesi için günde 30-50 firmayı geçmemenizi öneririm."));
        _semiAuto.Children.Add(_waApp);
        _semiAuto.Children.Add(Ui.Secondary("📋 Grubun WhatsApp listesini getir", async (_, _) => await LoadWhatsAppQueueAsync()));
        _semiAuto.Children.Add(_waProgress);
        _semiAuto.Children.Add(_waCurrent);
        _semiAuto.Children.Add(Ui.Primary("💬 WhatsApp'ta aç", async (_, _) => await OpenCurrentAsync()));
        _semiAuto.Children.Add(Row(
            Ui.Secondary("✓ Gönderildi, sıradaki", async (_, _) => await MarkCurrentAsync(sent: true)),
            Ui.Secondary("Atla", async (_, _) => await MarkCurrentAsync(sent: false))));
        _semiAuto.Children.Add(Ui.Secondary("🚫 Bu firma RET dedi (listeden çıkar)", async (_, _) => await OptOutCurrentAsync()));

        _scheduled.Children.Add(Ui.Note("Başlangıç tarihi ve saati"));
        _scheduled.Children.Add(Row(_startDate, _startTime));
        _scheduled.Children.Add(Ui.Note("Her gün yalnızca bu saatler arasında gönder"));
        _scheduled.Children.Add(Row(_windowStart, _windowEnd));
        _scheduled.Children.Add(Ui.Note("İki mesaj arası bekleme (saniye, en az 30)"));
        _scheduled.Children.Add(_spacing);
        _scheduled.Children.Add(_repeat);
        _scheduled.Children.Add(_template);
        _scheduled.Children.Add(Ui.Secondary("🔍 Hazırla ve önizle (henüz göndermez)", async (_, _) => await PrepareAsync()));
        _scheduled.Children.Add(_prepared);
        _scheduled.Children.Add(_launch);

        Ui.ApplyPageChrome(this);
        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(16, 12, 16, 24),
                Spacing = 14,
                Children =
                {
                    Ui.Card(new VerticalStackLayout { Spacing = 6, Children = { Ui.Title("Toplu tanıtım"), _status } }),
                    _busy,
                    Ui.Section(
                        "1 · Grup",
                        _group,
                        Row(Ui.Secondary("Yenile", async (_, _) => await LoadAsync()),
                            Ui.Allowed(Ui.Secondary("➕ Yeni grup", (_, _) => _newGroup.IsVisible = !_newGroup.IsVisible), "kampanya-yonet")),
                        _newGroup),
                    Ui.Section(
                        "2 · Mesaj",
                        _mode,
                        _subject,
                        _content,
                        Row(Ui.Secondary("📎 PDF seç", async (_, _) => await PickPdfAsync()),
                            Ui.Secondary("PDF'i kaldır", (_, _) => { _pdfLocal = null; _pdfUploaded = null; _pdfLabel.Text = "PDF eklenmedi."; })),
                        _pdfLabel),
                    Ui.Section("3 · Gönderim", _semiAuto, _scheduled),
                    Ui.Section(
                        "Kampanyalar",
                        Ui.Secondary("Durumları yenile", async (_, _) => await LoadCampaignsAsync()),
                        _campaigns)
                }
            }
        };
        ApplyMode();
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        if (!_loaded)
        {
            _loaded = true;
            await LoadAsync();
        }
    }

    private static Grid Row(View left, View right)
    {
        Grid grid = new() { ColumnDefinitions = [new ColumnDefinition(GridLength.Star), new ColumnDefinition(GridLength.Star)], ColumnSpacing = 8 };
        grid.Add(left, 0, 0);
        grid.Add(right, 1, 0);
        return grid;
    }

    private CampaignGroupItem? SelectedGroup => _group.SelectedItem as CampaignGroupItem;

    private void ApplyMode()
    {
        bool semi = _mode.SelectedIndex == 0;
        bool email = _mode.SelectedIndex == 1;
        _semiAuto.IsVisible = semi && ApiClient.Instance.Can("kampanya-whatsapp");
        _scheduled.IsVisible = !semi && ApiClient.Instance.Can("kampanya-yonet");
        _subject.IsVisible = email;
        _template.IsVisible = _mode.SelectedIndex == 2;
        _spacing.Text = email ? "90" : "60";
        ResetPreparation();
    }

    private void ResetPreparation()
    {
        _preparedCampaign = null;
        _prepared.Text = string.Empty;
        _launch.IsVisible = false;
    }

    private Task LoadAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        CampaignStatusInfo status = await ApiClient.Instance.CampaignStatusAsync();
        CampaignFilters filters = await ApiClient.Instance.CampaignFiltersAsync();
        List<CampaignGroupItem> groups = await ApiClient.Instance.CampaignGroupsAsync();
        Guid? selected = SelectedGroup?.Id;
        _group.ItemsSource = groups;
        _group.SelectedIndex = Math.Max(0, groups.FindIndex(g => g.Id == selected));
        SetChoices(_city, filters.Cities);
        SetChoices(_sector, filters.Sectors);
        SetChoices(_crmStatus, filters.Statuses);
        _status.Text = $"CRM: {filters.Total} firma · {filters.WithEmail} e-postalı · {filters.WithMobile} cep telefonlu.\n" +
                       (status.Email is null ? "E-posta: " + status.EmailSetupHint : "E-posta göndericisi: " + status.Email) + "\n" +
                       $"Her mesaja ret satırı eklenir: \"{status.OptOut}\"\nPDF e-posta için en fazla {status.MaxEmailAttachmentKb / 1024.0:0.#} MB.";

        IReadOnlyList<string> apps = WhatsAppShare.InstalledPackages();
        _waApp.ItemsSource = apps.Select(p => p == WhatsAppShare.Business ? "WhatsApp Business" : "WhatsApp").ToList();
        _waApp.SelectedIndex = apps.Count == 0 ? -1 : Math.Clamp(Preferences.Default.Get("campaign_wa_app", 0), 0, apps.Count - 1);
        await LoadCampaignsAsync(false);
    });

    private static void SetChoices(Picker picker, List<NameCount> values)
    {
        string? current = picker.SelectedItem as string;
        List<string> items = [AllLabel, .. values.Select(v => v.Name)];
        picker.ItemsSource = items;
        picker.SelectedIndex = Math.Max(0, items.IndexOf(current ?? AllLabel));
    }

    private object Filter() => new
    {
        name = _groupName.Text?.Trim(),
        city = Choice(_city),
        sector = Choice(_sector),
        status = Choice(_crmStatus),
        require = _require.SelectedIndex switch { 1 => "email", 2 => "whatsapp", _ => null }
    };

    private static string? Choice(Picker picker) => picker.SelectedItem is string value && value != AllLabel ? value : null;

    private Task PreviewGroupAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        GroupPreview preview = await ApiClient.Instance.PreviewGroupAsync(Filter());
        _groupPreview.Text = $"{preview.Count} firma ({preview.WithEmail} e-postalı, {preview.WithMobile} cep telefonlu). " +
                             (preview.Sample.Count > 0 ? "Örnek: " + string.Join(", ", preview.Sample) : string.Empty);
    });

    private Task CreateGroupAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        CampaignGroupItem created = await ApiClient.Instance.CreateGroupAsync(Filter());
        _newGroup.IsVisible = false;
        _groupName.Text = string.Empty;
        await LoadAsync();
        List<CampaignGroupItem> groups = (List<CampaignGroupItem>)_group.ItemsSource;
        _group.SelectedIndex = groups.FindIndex(g => g.Id == created.Id);
        await DisplayAlertAsync("Grup", $"{created.Name} grubu {created.Count} firmayla kaydedildi.", "Tamam");
    });

    private async Task PickPdfAsync()
    {
        FileResult? file = await FilePicker.Default.PickAsync(new PickOptions
        {
            PickerTitle = "Tanıtım PDF'i",
            FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>>
            {
                [DevicePlatform.Android] = ["application/pdf"]
            })
        });
        if (file is null)
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            _pdfLocal = await WhatsAppShare.StagePdfAsync(file);
            _pdfUploaded = null;
            long kb = new FileInfo(_pdfLocal).Length / 1024;
            _pdfLabel.Text = $"📄 {file.FileName} ({kb} KB)";
            ResetPreparation();
        });
    }

    private string PersonalText(string company) =>
        (_content.Text ?? string.Empty).Replace("{firma}", company, StringComparison.OrdinalIgnoreCase).Trim() +
        "\n\nBu tür mesajları almak istemiyorsanız RET yazmanız yeterli.";

    private string ProgressKey => $"campaign_wa_done_{SelectedGroup?.Id}";

    private HashSet<int> DoneIds() => Preferences.Default.Get(ProgressKey, string.Empty)
        .Split(',', StringSplitOptions.RemoveEmptyEntries)
        .Select(v => int.TryParse(v, out int id) ? id : 0)
        .Where(id => id > 0)
        .ToHashSet();

    private void ResetWhatsAppQueue()
    {
        _waContacts = [];
        _waIndex = 0;
        _waCurrent.Text = string.Empty;
        _waProgress.Text = string.Empty;
        ResetPreparation();
    }

    private Task LoadWhatsAppQueueAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        CampaignGroupItem group = SelectedGroup ?? throw new ApiException("Önce bir grup seçin.");
        WhatsAppList list = await ApiClient.Instance.GroupWhatsAppAsync(group.Id);
        HashSet<int> done = DoneIds();
        _waContacts = list.Contacts;
        _waIndex = _waContacts.FindIndex(c => !done.Contains(c.Id));
        ShowCurrent($"{list.Contacts.Count} firma WhatsApp'a uygun; {list.SkippedNoMobile} cep telefonu yok, {list.SkippedOptOut} RET demiş.");
    });

    private void ShowCurrent(string? header = null)
    {
        HashSet<int> done = DoneIds();
        int remaining = _waContacts.Count(c => !done.Contains(c.Id));
        _waProgress.Text = (header is null ? string.Empty : header + "\n") + $"Kalan: {remaining} / {_waContacts.Count}";
        _waCurrent.Text = _waIndex >= 0 && _waIndex < _waContacts.Count
            ? $"Sıradaki: {_waContacts[_waIndex].Name}"
            : _waContacts.Count == 0 ? string.Empty : "🎉 Bu gruptaki tüm firmalar tamamlandı.";
    }

    private WhatsAppContact? Current => _waIndex >= 0 && _waIndex < _waContacts.Count ? _waContacts[_waIndex] : null;

    private async Task OpenCurrentAsync()
    {
        if (Current is not { } contact)
        {
            await DisplayAlertAsync("WhatsApp", "Önce listeyi getirin.", "Tamam");
            return;
        }

        if (string.IsNullOrWhiteSpace(_content.Text))
        {
            await DisplayAlertAsync("WhatsApp", "Mesaj metnini yazın.", "Tamam");
            return;
        }

        IReadOnlyList<string> apps = WhatsAppShare.InstalledPackages();
        if (apps.Count == 0)
        {
            await DisplayAlertAsync("WhatsApp", "Telefonda WhatsApp bulunamadı.", "Tamam");
            return;
        }

        string package = apps[Math.Clamp(_waApp.SelectedIndex, 0, apps.Count - 1)];
        try
        {
            WhatsAppShare.Open(package, contact.Phone, PersonalText(contact.Name), _pdfLocal);
        }
        catch (Exception exception)
        {
            await DisplayAlertAsync("WhatsApp", "WhatsApp açılamadı: " + exception.Message, "Tamam");
        }
    }

    private Task MarkCurrentAsync(bool sent) => Ui.RunAsync(this, _busy, async () =>
    {
        if (Current is not { } contact)
        {
            return;
        }

        if (sent)
        {
            await ApiClient.Instance.MarkWhatsAppSentAsync(contact.Id, SelectedGroup?.Name);
        }

        HashSet<int> done = DoneIds();
        done.Add(contact.Id);
        Preferences.Default.Set(ProgressKey, string.Join(',', done));
        _waIndex = _waContacts.FindIndex(c => !done.Contains(c.Id));
        ShowCurrent();
    });

    private async Task OptOutCurrentAsync()
    {
        if (Current is not { } contact)
        {
            return;
        }

        if (!await DisplayAlertAsync("RET", $"{contact.Name} bundan sonra hiçbir kampanyada (WhatsApp ve e-posta) yer almasın mı?", "Evet, çıkar", "Vazgeç"))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            string message = await ApiClient.Instance.MarkOptOutAsync(contact.Id, null);
            HashSet<int> done = DoneIds();
            done.Add(contact.Id);
            Preferences.Default.Set(ProgressKey, string.Join(',', done));
            _waIndex = _waContacts.FindIndex(c => !done.Contains(c.Id));
            ShowCurrent();
            await DisplayAlertAsync("RET", message, "Tamam");
        });
    }

    private Task PrepareAsync() => Ui.RunAsync(this, _busy, async () =>
    {
        CampaignGroupItem group = SelectedGroup ?? throw new ApiException("Önce bir grup seçin.");
        bool email = _mode.SelectedIndex == 1;
        if (string.IsNullOrWhiteSpace(_content.Text) && email)
        {
            throw new ApiException("E-posta metnini yazın.");
        }

        if (_pdfLocal is not null && _pdfUploaded is null)
        {
            _prepared.Text = "PDF bilgisayara yükleniyor…";
            _pdfUploaded = await ApiClient.Instance.UploadCampaignPdfAsync(_pdfLocal);
            if (email && !_pdfUploaded.EmailOk)
            {
                _pdfUploaded = null;
                throw new ApiException("PDF e-posta eki için büyük. Daha küçük (sıkıştırılmış) bir PDF seçin.");
            }
        }

        DateTime start = _startDate.Date.GetValueOrDefault(DateTime.Today).Date + _startTime.Time.GetValueOrDefault();
        int.TryParse(_spacing.Text, out int spacing);
        _preparedCampaign = await ApiClient.Instance.PrepareCampaignAsync(new
        {
            groupId = group.Id,
            channel = email ? "email" : "whatsapp",
            name = $"{group.Name} – {DateTime.Now:dd.MM HH:mm}",
            subject = _subject.Text,
            content = _content.Text,
            attachments = _pdfUploaded is null ? Array.Empty<string>() : [_pdfUploaded.Token],
            startsAt = new DateTimeOffset(start),
            windowStart = $"{_windowStart.Time.GetValueOrDefault():hh\\:mm}",
            windowEnd = $"{_windowEnd.Time.GetValueOrDefault():hh\\:mm}",
            spacingSeconds = spacing,
            repeatEveryDays = _repeat.SelectedIndex switch { 1 => 7, 2 => 14, 3 => 30, _ => 0 },
            templateName = _template.Text,
            templateLanguage = "tr"
        });
        PreparedCampaign p = _preparedCampaign;
        _prepared.Text = $"Hazır (henüz gönderilmedi):\n• Gönderen: {p.Sender}\n• Alıcı: {p.Eligible} / {p.GroupSize} firma, her birine ayrı ileti\n" +
                         $"• {p.Exclusions}" + (p.OptedOutSkipped > 0 ? $" ({p.OptedOutSkipped} firma daha önce RET dediği için atlandı)" : string.Empty) +
                         $"\n• Başlangıç {p.StartsAt}, her gün {p.Window}, {p.SpacingSeconds} sn arayla, tahmini {p.EstimatedDays} gün\n" +
                         (p.Attachments.Count > 0 ? $"• Ek: {string.Join(", ", p.Attachments)}\n" : string.Empty) +
                         $"• Metin:\n{p.Preview}";
        _launch.IsVisible = true;
    });

    private async Task LaunchAsync()
    {
        if (_preparedCampaign is not { } p)
        {
            return;
        }

        if (!await DisplayAlertAsync("Gerçek gönderim", $"{p.Eligible} firmaya {p.Sender} üzerinden, {p.StartsAt} itibarıyla gönderim başlasın mı? Bu işlem müşterilere ileti gönderir.", "Evet, başlat", "Vazgeç"))
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            string message = await ApiClient.Instance.LaunchCampaignAsync(p.CampaignId, p.Revision, p.ManifestHash);
            ResetPreparation();
            await LoadCampaignsAsync(false);
            await DisplayAlertAsync("Kampanya", message, "Tamam");
        });
    }

    private async Task LoadCampaignsAsync(bool showBusy = true)
    {
        async Task Load()
        {
            _campaignItems = await ApiClient.Instance.CampaignsAsync();
            _campaigns.Children.Clear();
            if (_campaignItems.Count == 0)
            {
                _campaigns.Children.Add(Ui.Note("Henüz kampanya yok."));
            }

            foreach (CampaignListItem item in _campaignItems.Where(c => c.StateCode != "Draft").Take(15))
            {
                Border row = Ui.Card(new VerticalStackLayout
                {
                    Spacing = 2,
                    Children =
                    {
                        new Label { Text = item.Name, FontSize = 15, FontFamily = "OpenSansSemibold" },
                        Ui.Note(item.Summary)
                    }
                });
                TapGestureRecognizer tap = new();
                tap.Tapped += async (_, _) => await CampaignActionsAsync(item);
                row.GestureRecognizers.Add(tap);
                _campaigns.Children.Add(row);
            }
        }

        if (showBusy)
        {
            await Ui.RunAsync(this, _busy, Load);
        }
        else
        {
            await Load();
        }
    }

    private async Task CampaignActionsAsync(CampaignListItem item)
    {
        List<string> actions = [];
        if (item.StateCode is "Active" or "Scheduled")
        {
            actions.Add("⏸ Duraklat");
        }

        if (item.StateCode == "Paused")
        {
            actions.Add("▶ Devam ettir");
        }

        if (item.StateCode is "Active" or "Scheduled" or "Paused" or "Approved")
        {
            actions.Add("✖ İptal et");
        }

        string? choice = await DisplayActionSheetAsync($"{item.Name}\n{item.Summary}", "Kapat", null, actions.ToArray());
        string? action = choice switch { "⏸ Duraklat" => "duraklat", "▶ Devam ettir" => "devam", "✖ İptal et" => "iptal", _ => null };
        if (action is null)
        {
            return;
        }

        await Ui.RunAsync(this, _busy, async () =>
        {
            string message = await ApiClient.Instance.ChangeCampaignAsync(item.Id, action, item.Revision);
            await LoadCampaignsAsync(false);
            await DisplayAlertAsync("Kampanya", message, "Tamam");
        });
    }
}
