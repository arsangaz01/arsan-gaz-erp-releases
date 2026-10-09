using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using ArsanGazERP.Services;

namespace ArsanGazERP.Views;

public sealed class CloudAgentSettingsWindow : Window
{
    private readonly TextBox _endpoint = new() { MinWidth = 420, Padding = new Thickness(7) };
    private readonly PasswordBox _token = new() { MinWidth = 420, Padding = new Thickness(7) };
    private readonly ComboBox _agentSelector = new() { MinWidth = 260, Padding = new Thickness(6) };
    private readonly CheckBox _publicResearch = new() { Content = "Kamuya açık GitHub araştırması" };
    private readonly CheckBox _crmNote = new() { Content = "CRM notu oluşturma" };
    private readonly CheckBox _crmFollowUp = new() { Content = "CRM takip tarihi planlama" };
    private readonly CheckBox _erpTask = new() { Content = "ERP görevi oluşturma" };
    private readonly CheckBox _delegateTasks = new() { Content = "Gözcü görev kuyruğuna iş ekleme" };
    private readonly CheckBox _collectionReminders = new() { Content = "Tahsilat hatırlatması kaydetme (@Ece /tahsilat)" };
    private readonly CheckBox _erpSummary = new()
    {
        Content = "ERP özetini okuyabilir (müşteri adı, tutar ve tarihler yapay zeka sağlayıcısına gönderilir)"
    };
    private readonly CheckBox _computerControl = new()
    {
        Content = "Bilgisayar komutları (izinli uygulama/site açma, Masaüstü/Belgeler altında klasör oluşturma)"
    };
    private readonly TextBlock _roleDescription = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrush("MutedBrush")
    };
    private readonly TextBlock _status = new()
    {
        TextWrapping = TextWrapping.Wrap,
        Foreground = ThemeBrush("MutedBrush")
    };
    private readonly TextBlock _permissionHint = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 0, 0, 8)
    };
    private readonly TextBlock _permissionStatus = new()
    {
        TextWrapping = TextWrapping.Wrap,
        FontWeight = FontWeights.SemiBold,
        Margin = new Thickness(0, 4, 0, 0)
    };
    private readonly Dictionary<string, AgentPermissionSettings> _permissions;
    private ErpAgentProfile? _selectedProfile;

    public CloudAgentSettingsWindow()
    {
        Title = "Arsan Gaz ERP — Ajan Yetkileri ve Bulut";
        Width = 780;
        Height = 760;
        MinWidth = 640;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowWorkAreaService.Fit(this);
        Background = ThemeBrush("CanvasBrush");

        _permissions = CloudAgentSettingsStore.LoadPermissions()
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        Content = BuildLayout();
        LoadConnectionSettings();
        LoadSelectedProfile();
    }

    private UIElement BuildLayout()
    {
        StackPanel content = new() { Margin = new Thickness(22) };
        content.Children.Add(new TextBlock
        {
            Text = "Ajan Yetkileri ve Bulut",
            FontSize = 26,
            FontWeight = FontWeights.Bold,
            Foreground = ThemeBrush("InkBrush")
        });
        content.Children.Add(new TextBlock
        {
            Text = "Cloudflare Workers AI bağlantısı ve ajan başına izinler",
            Margin = new Thickness(0, 5, 0, 16),
            Foreground = ThemeBrush("MutedBrush")
        });

        Border privacyNotice = Card();
        StackPanel privacyContent = new() { Margin = new Thickness(14) };
        privacyContent.Children.Add(Heading("Gizlilik ve ücretsiz kullanım"));
        privacyContent.Children.Add(new TextBlock
        {
            Text = "Bulut modeline bu turda yazdığınız mesaj ve kısa sohbet geçmişi gönderilir. ERP/CRM kayıtları yalnızca " +
                   "bir ajan için 'ERP özetini okuyabilir' izni açıksa, salt-okunur kısa bir özet olarak gönderilir; " +
                   "yerel raporlar, veritabanı şeması ve dosyalar gönderilmez. Araştırmacının arama metni GitHub " +
                   "arama API'sine de iletilir. Mikrofon açıksa yerel konuşma tanımanın ürettiği metin buluta gider; " +
                   "ham ses bu bağlantı üzerinden gönderilmez. Cloudflare Worker ve AI ücretsiz kotaları aşılırsa " +
                   "istekler durur; ücretli plana geçiş uygulama tarafından yapılmaz. Cloudflare hesabı ücretli plana " +
                   "alınmışsa ücretsiz AI sınırı üzerindeki kullanım ücret doğurabilir.",
            TextWrapping = TextWrapping.Wrap
        });
        privacyNotice.Child = privacyContent;
        content.Children.Add(privacyNotice);

        Border connectionCard = Card();
        StackPanel connection = new() { Margin = new Thickness(14) };
        connection.Children.Add(Heading("Cloudflare Worker bağlantısı"));
        connection.Children.Add(Field("Worker kök adresi (ör. https://...workers.dev)", _endpoint));
        connection.Children.Add(Field("Worker erişim anahtarı · Windows hesabınıza şifreli kaydedilir", _token));

        WrapPanel buttons = new() { Margin = new Thickness(0, 5, 0, 5) };
        buttons.Children.Add(ActionButton("Ayarları kaydet", SaveConnection));
        buttons.Children.Add(ActionButton("Bağlantıyı test et", TestConnectionAsync));
        buttons.Children.Add(ActionButton("Erişim anahtarını kaldır", RemoveToken));
        connection.Children.Add(buttons);
        connection.Children.Add(_status);
        connectionCard.Child = connection;
        content.Children.Add(connectionCard);

        Border permissionCard = Card();
        StackPanel permission = new() { Margin = new Thickness(14) };
        permission.Children.Add(Heading("Ajan profili ve izinleri"));

        foreach (ErpAgentProfile profile in AgentChatService.Profiles)
        {
            _agentSelector.Items.Add(new AgentProfileChoice(profile));
        }
        _agentSelector.DisplayMemberPath = nameof(AgentProfileChoice.DisplayName);
        _agentSelector.SelectionChanged += (_, _) =>
        {
            SaveSelectedProfileDraft();
            LoadSelectedProfile();
        };
        permission.Children.Add(Field("Ajan", _agentSelector));
        permission.Children.Add(_roleDescription);
        permission.Children.Add(new TextBlock
        {
            Text = "Yazma izinleri yalnızca yerel ERP işlemlerini açar; hiçbir bulut ajanına veritabanı erişimi vermez.",
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 8),
            Foreground = ThemeBrush("MutedBrush")
        });
        permission.Children.Add(_permissionHint);
        foreach (CheckBox option in PermissionOptions)
        {
            option.Margin = new Thickness(0, 2, 0, 4);
            option.Checked += (_, _) => _permissionStatus.Text = "Değişiklik kaydedilmedi; \"Ajan izinlerini kaydet\" düğmesine basın.";
            option.Unchecked += (_, _) => _permissionStatus.Text = "Değişiklik kaydedilmedi; \"Ajan izinlerini kaydet\" düğmesine basın.";
            permission.Children.Add(option);
        }
        Button savePermissions = ActionButton("Ajan izinlerini kaydet", SavePermissions);
        savePermissions.Margin = new Thickness(0, 10, 0, 0);
        permission.Children.Add(savePermissions);
        permission.Children.Add(_permissionStatus);
        permissionCard.Child = permission;
        content.Children.Add(permissionCard);

        ScrollViewer scroll = new()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = content
        };
        return scroll;
    }

    private void LoadConnectionSettings()
    {
        CloudAgentConnectionSettings settings = CloudAgentSettingsStore.LoadConnection();
        _endpoint.Text = settings.EndpointUrl;
        _token.ToolTip = settings.HasAccessToken
            ? "Erişim anahtarı kayıtlı. Boş bırakılırsa mevcut anahtar korunur."
            : "Dağıtım sırasında oluşturduğunuz en az 32 karakterlik Worker erişim anahtarını girin.";
        _status.Text = settings.HasAccessToken
            ? "Erişim anahtarı bu Windows hesabında şifreli saklanıyor."
            : "Worker henüz yapılandırılmadı; NVIDIA NIM anahtarı kayıtlıysa ajanlar NIM ile yanıt verir.";
    }

    private void LoadSelectedProfile()
    {
        _selectedProfile = (_agentSelector.SelectedItem as AgentProfileChoice)?.Profile;
        if (_selectedProfile is null)
        {
            ErpAgentProfile? first = AgentChatService.Profiles.FirstOrDefault();
            if (first is null)
            {
                return;
            }

            _agentSelector.SelectedItem = _agentSelector.Items
                .OfType<AgentProfileChoice>()
                .First(choice => choice.Profile.Id == first.Id);
            return;
        }

        AgentPermissionSettings permission = _permissions[_selectedProfile.Id];
        _roleDescription.Text = $"{_selectedProfile.Role}{Environment.NewLine}{_selectedProfile.SystemPrompt}";
        _publicResearch.IsChecked = permission.CanSearchPublicWeb;
        _crmNote.IsChecked = permission.CanCreateCrmNote;
        _crmFollowUp.IsChecked = permission.CanScheduleCrmFollowUp;
        _erpTask.IsChecked = permission.CanCreateErpTask;
        _delegateTasks.IsChecked = permission.CanDelegateTasks;
        _collectionReminders.IsChecked = permission.CanManageCollectionReminders;
        _erpSummary.IsChecked = permission.CanReadErpSummary;
        _computerControl.IsChecked = permission.CanControlComputer;

        string id = _selectedProfile.Id;
        ShowWhen(_publicResearch, id == "researcher");
        ShowWhen(_crmNote, id == "crm");
        ShowWhen(_crmFollowUp, id == "crm");
        ShowWhen(_erpTask, id == "planner");
        ShowWhen(_delegateTasks, id is "supervisor" or "jarvis");
        ShowWhen(_collectionReminders, id == "finance");
        ShowWhen(_erpSummary, ErpContextSnapshotService.IsEligible(id));
        ShowWhen(_computerControl, id == "jarvis");

        int available = PermissionOptions.Count(option => option.Visibility == Visibility.Visible);
        _permissionHint.Text = available == 0
            ? $"{_selectedProfile.Name} için açılabilecek ek izin yok; bu ajan yalnızca sohbet eder."
            : $"{_selectedProfile.Name} için açılabilecek izinler aşağıda. Tahsilat izni yalnızca Ece'de, " +
              "ERP özeti Defne, Mert, Ece, Can ve Jarvis'te bulunur.";
        _permissionStatus.Text = string.Empty;
    }

    private CheckBox[] PermissionOptions =>
        [_publicResearch, _crmNote, _crmFollowUp, _erpTask, _delegateTasks, _collectionReminders, _erpSummary, _computerControl];

    private static void ShowWhen(CheckBox option, bool applicable)
    {
        option.Visibility = applicable ? Visibility.Visible : Visibility.Collapsed;
        option.IsEnabled = applicable;
    }

    private void SaveSelectedProfileDraft()
    {
        if (_selectedProfile is null)
        {
            return;
        }

        _permissions[_selectedProfile.Id] = new AgentPermissionSettings(
            CanSearchPublicWeb: _selectedProfile.Id == "researcher" && _publicResearch.IsChecked == true,
            CanCreateCrmNote: _selectedProfile.Id == "crm" && _crmNote.IsChecked == true,
            CanScheduleCrmFollowUp: _selectedProfile.Id == "crm" && _crmFollowUp.IsChecked == true,
            CanCreateErpTask: _selectedProfile.Id == "planner" && _erpTask.IsChecked == true,
            CanDelegateTasks: _selectedProfile.Id is "supervisor" or "jarvis" && _delegateTasks.IsChecked == true,
            CanManageCollectionReminders: _selectedProfile.Id == "finance" && _collectionReminders.IsChecked == true,
            CanReadErpSummary: ErpContextSnapshotService.IsEligible(_selectedProfile.Id) && _erpSummary.IsChecked == true,
            CanControlComputer: _selectedProfile.Id == "jarvis" && _computerControl.IsChecked == true);
    }

    private void SavePermissions()
    {
        try
        {
            SaveSelectedProfileDraft();
            CloudAgentSettingsStore.SavePermissions(_permissions);
            _permissionStatus.Foreground = ThemeBrush("AccentBrush");
            _permissionStatus.Text = $"✓ {_selectedProfile?.Name} izinleri kaydedildi ({DateTime.Now:HH:mm:ss}).";
        }
        catch (Exception exception)
        {
            _permissionStatus.Foreground = ThemeBrush("DangerBrush");
            _permissionStatus.Text = "Ajan izinleri kaydedilemedi: " + exception.GetBaseException().Message;
        }
    }

    private void SaveConnection()
    {
        try
        {
            string token = _token.Password.Trim();
            if (token.Length is > 0 and < 32)
            {
                throw new ArgumentException("Worker erişim anahtarı en az 32 karakter olmalıdır.");
            }

            CloudAgentSettingsStore.SaveConnection(_endpoint.Text, token);
            _token.Clear();
            _status.Text = CloudAgentSettingsStore.LoadConnection().HasAccessToken
                ? "Worker adresi kaydedildi. Erişim anahtarı Windows DPAPI ile şifrelendi."
                : "Worker adresi kaydedildi; erişim anahtarı girilmedi.";
        }
        catch (Exception exception)
        {
            _status.Text = "Bulut ayarları kaydedilemedi: " + exception.GetBaseException().Message;
        }
    }

    private async Task TestConnectionAsync()
    {
        try
        {
            _status.Text = "Worker bağlantısı denetleniyor…";
            _status.Text = await new CloudAgentClient().TestConnectionAsync();
        }
        catch (Exception exception)
        {
            _status.Text = "Worker bağlantı testi başarısız: " + exception.GetBaseException().Message;
        }
    }

    private void RemoveToken()
    {
        try
        {
            CloudAgentSettingsStore.RemoveAccessToken();
            _token.Clear();
            _status.Text = "Worker erişim anahtarı yerel bilgisayardan kaldırıldı.";
        }
        catch (Exception exception)
        {
            _status.Text = "Erişim anahtarı kaldırılamadı: " + exception.GetBaseException().Message;
        }
    }

    private static Border Card() => new()
    {
        Margin = new Thickness(0, 0, 0, 12),
        Background = ThemeBrush("SurfaceBrush"),
        BorderBrush = ThemeBrush("BorderBrush"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8)
    };

    private static TextBlock Heading(string text) => new()
    {
        Text = text,
        FontSize = 17,
        FontWeight = FontWeights.Bold,
        Foreground = ThemeBrush("InkBrush"),
        Margin = new Thickness(0, 0, 0, 8)
    };

    private static StackPanel Field(string label, Control control)
    {
        StackPanel field = new() { Margin = new Thickness(0, 4, 0, 7) };
        field.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 0, 0, 4) });
        field.Children.Add(control);
        return field;
    }

    private static Button ActionButton(string text, Action action)
    {
        Button button = CreateButton(text);
        button.Click += (_, _) => action();
        return button;
    }

    private static Button ActionButton(string text, Func<Task> action)
    {
        Button button = CreateButton(text);
        button.Click += async (_, _) =>
        {
            button.IsEnabled = false;
            try
            {
                await action();
            }
            finally
            {
                button.IsEnabled = true;
            }
        };
        return button;
    }

    private static Button CreateButton(string text) => new()
    {
        Content = text,
        Padding = new Thickness(12, 7, 12, 7),
        Margin = new Thickness(0, 0, 8, 6),
        Style = (Style)Application.Current.FindResource(
            text.Contains("kaldır", StringComparison.OrdinalIgnoreCase)
                ? "DangerButton"
                : text.Contains("test", StringComparison.OrdinalIgnoreCase)
                    ? "SecondaryButton"
                    : typeof(Button))
    };

    private static Brush ThemeBrush(string key) => (Brush)Application.Current.FindResource(key);

    private sealed record AgentProfileChoice(ErpAgentProfile Profile)
    {
        public string DisplayName => $"{Profile.Name} — {Profile.Role}";
    }
}
