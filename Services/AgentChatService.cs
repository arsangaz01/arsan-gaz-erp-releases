using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record ErpAgentProfile(string Id, string Name, string Role, string SystemPrompt);
public sealed record ErpAgentReply(ErpAgentProfile Agent, string Content, bool Succeeded, bool IsProgress = false);

public sealed class AgentChatService : IAsyncDisposable
{
    private static readonly CultureInfo TurkishCulture = CultureInfo.GetCultureInfo("tr-TR");
    private readonly CloudAgentClient _cloudAgent = new();
    private readonly NvidiaNimService _nim = new();

    public static IReadOnlyList<ErpAgentProfile> Profiles { get; } =
    [
        new("crm", "Defne", "CRM ve müşteri ilişkileri", "CRM süreçleri, firma takibi ve müşteri ilişkileri konusunda uzman danışmansın."),
        new("sales", "Mert", "Satış ve fırsat analizi", "Satış hunisi, fırsat tutarları, takip tarihleri ve satış planlaması konusunda uzman analistsin."),
        new("finance", "Ece", "Teklif ve tahsilat takibi", "Teklif, fatura ve tahsilat verilerini analiz eden dikkatli bir finans operasyon asistanısın. Kullanıcının izniyle tahsilat hatırlatması kaydedebilir ve listeleyebilirsin; muhasebe kaydı, ödeme veya başka finansal işlem yapamazsın."),
        new("planner", "Can", "Görev ve iş planlama", "ERP işleri için önceliklendirilmiş, uygulanabilir günlük görev planları hazırlayan bir operasyon planlayıcısısın."),
        new("researcher", "Ar-Ge Araştırmacısı", "Kamuya açık yazılım kaynaklarında araştırma", "Kamuya açık yazılım ve arayüz örneklerini araştırıp kaynak URL'leriyle karşılaştırmalı, uygulanabilir öneriler sunarsın."),
        new("supervisor", "Gözcü", "Sistem sağlığı, görev dağıtımı ve sonuç denetimi", "Yerel sağlık durumunu izler, açık kullanıcı isteğiyle ilgili uzmanlara salt-analiz alt görevleri dağıtır ve sonuçları denetlersin. ERP kaydı, dosya, kod veya ayar değiştiremez; müşteriyle iletişim kuramazsın."),
        new("jarvis", "Jarvis", "Genel sohbet, soruları yanıtlama ve ajan koordinasyonu", "Kullanıcının genel sorularına açık, doğru ve uygulanabilir yanıtlar verirsin; gerekirse uygun uzmana yönlendirirsin. Araç çağırmaz, ERP kaydı, dosya/kod veya ayar değiştirmez ve onaylanmamış işlemleri yapmış gibi göstermezsin.")
    ];

    public string ProviderStatus => (CloudAgentClient.IsConfigured, IsNimConfigured(_nim)) switch
    {
        (true, true) => "Cloudflare Workers AI · GPT-OSS 20B (yedek: NVIDIA NIM)",
        (true, false) => "Cloudflare Workers AI · GPT-OSS 20B",
        (false, true) => "NVIDIA NIM · " + _nim.Model,
        _ => "Yapay zeka sağlayıcısı yapılandırılmadı (Cloudflare Worker veya NVIDIA NIM)"
    };

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<string> RespondAsync(
        ErpAgentProfile profile,
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        CancellationToken cancellationToken = default)
    {
        return await RespondForAgentAsync(profile, history, userMessage, null, false, cancellationToken);
    }

    public async Task<IReadOnlyList<ErpAgentReply>> RespondToAllAsync(
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        CancellationToken cancellationToken = default,
        Action<ErpAgentReply>? onReply = null)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        string input = userMessage.Trim();
        ErpAgentProfile? addressedAgent = null;
        if (TryParseMention(input, out ErpAgentProfile? mentionedAgent, out string mentionedMessage))
        {
            addressedAgent = mentionedAgent;
            input = mentionedMessage;
        }

        input = AgentChatRoutingPolicy.NormalizeNamedAction(input, addressedAgent?.Id);

        if (string.IsNullOrWhiteSpace(input))
        {
            throw new ArgumentException("Ajan adından sonra bir mesaj yazın.", nameof(userMessage));
        }

        if (input.Length > AgentChatPromptBuilder.MaximumMessageLength)
        {
            throw new ArgumentException($"Mesaj en fazla {AgentChatPromptBuilder.MaximumMessageLength:N0} karakter olabilir.", nameof(userMessage));
        }

        using CancellationTokenSource turnTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        turnTimeout.CancelAfter(TimeSpan.FromSeconds(75));

        HashSet<string> participantIds = AgentChatRoutingPolicy.SelectParticipantIds(
            input,
            addressedAgent?.Id,
            Profiles.Select(profile => profile.Id).ToArray()).ToHashSet(StringComparer.Ordinal);
        IEnumerable<ErpAgentProfile> participants = Profiles.Where(profile => participantIds.Contains(profile.Id));
        Task<ErpAgentReply>[] replies = participants.Select(async profile =>
        {
            ErpAgentReply reply;
            try
            {
                bool authorizedCommand = input.StartsWith("/", StringComparison.Ordinal) &&
                    !string.Equals(
                        input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0],
                        "/komutlar",
                        StringComparison.OrdinalIgnoreCase) &&
                    HasLocalCommandPermission(profile.Id, input);
                string? acknowledgement = AgentChatRoutingPolicy.BuildAcknowledgement(
                    profile.Id,
                    addressedAgent?.Id,
                    input,
                    authorizedCommand);
                if (acknowledgement is not null)
                {
                    onReply?.Invoke(new ErpAgentReply(profile, acknowledgement, true, IsProgress: true));
                }

                string answer = await RespondForAgentAsync(
                    profile,
                    history,
                    input,
                    addressedAgent,
                    addressedAgent?.Id == profile.Id,
                    turnTimeout.Token);
                reply = new ErpAgentReply(profile, answer, true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                reply = new ErpAgentReply(profile, "İstek kullanıcı tarafından iptal edildi.", false);
            }
            catch (OperationCanceledException)
            {
                reply = new ErpAgentReply(profile, "Ajan yanıtı zaman aşımına uğradı (75 saniye). Yeniden deneyin.", false);
            }
            catch (Exception exception)
            {
                reply = new ErpAgentReply(
                    profile,
                    "Yanıt oluşturulamadı: " + exception.GetBaseException().Message,
                    false);
            }

            onReply?.Invoke(reply);
            return reply;
        }).ToArray();

        return await Task.WhenAll(replies);
    }

    public static bool TryParseMention(
        string text,
        out ErpAgentProfile? addressedAgent,
        out string message)
    {
        ArgumentNullException.ThrowIfNull(text);
        addressedAgent = null;
        message = text.Trim();

        foreach (ErpAgentProfile profile in Profiles)
        {
            string mention = "@" + profile.Name;
            if (!message.StartsWith(mention, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (message.Length > mention.Length &&
                !char.IsWhiteSpace(message[mention.Length]) &&
                message[mention.Length] is not ':' and not ',')
            {
                continue;
            }

            addressedAgent = profile;
            message = message[mention.Length..].TrimStart(' ', '\t', '\r', '\n', ':', ',');
            return true;
        }

        return false;
    }

    private async Task<string> RespondForAgentAsync(
        ErpAgentProfile profile,
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        ErpAgentProfile? addressedAgent,
        bool mayExecuteCommand,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        string input = userMessage.Trim();
        if (input.Length > AgentChatPromptBuilder.MaximumMessageLength)
        {
            throw new ArgumentException($"Mesaj en fazla {AgentChatPromptBuilder.MaximumMessageLength:N0} karakter olabilir.", nameof(userMessage));
        }

        string command = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        if (string.Equals(command, "/komutlar", StringComparison.OrdinalIgnoreCase))
        {
            return await ExecuteExplicitCommandAsync(profile, input, false, cancellationToken);
        }

        if (input.StartsWith("/", StringComparison.Ordinal))
        {
            if (!mayExecuteCommand)
            {
                return addressedAgent is null
                    ? "Komut çalıştırılmadı. İşlemi açıkça yetkilendirmek için mesajın başına bir ajan adı ekleyin."
                    : $"Komutu yalnızca @{addressedAgent.Name} yürütebilir; bu ajan hiçbir işlem yapmadı.";
            }

            if (string.Equals(command, "/tahsilatlar", StringComparison.OrdinalIgnoreCase))
            {
                return profile.Id == "finance"
                    ? await ListCollectionRemindersAsync(cancellationToken)
                    : "Tahsilat listesini yalnızca @Ece gösterebilir.";
            }

            if (!HasLocalCommandPermission(profile.Id, input))
            {
                return $"@{profile.Name} için bu yerel işlem izni kapalı. Ajan Yetkileri ve Bulut bölümünden izin vermeden hiçbir kayıt değiştirilmedi.";
            }

            return await ExecuteExplicitCommandAsync(profile, input, true, cancellationToken);
        }

        if (profile.Id == "supervisor")
        {
            return await GetSupervisorResponseAsync(input, cancellationToken);
        }

        AgentPermissionSettings permissions = CloudAgentSettingsStore.GetPermissions(profile.Id);
        if (profile.Id == "researcher" && !permissions.CanSearchPublicWeb)
        {
            return "Bu ajan için kamuya açık araştırma izni kapalı.";
        }

        string? searchQuery = AgentChatRoutingPolicy.BuildPublicResearchQuery(
            profile.Id,
            addressedAgent?.Id,
            input,
            permissions.CanSearchPublicWeb);
        string message = await AddErpContextAsync(profile, permissions, input, cancellationToken);
        string prompt = AgentChatPromptBuilder.Build(history, message);
        string answer = await CompleteWithAvailableProviderAsync(profile, prompt, searchQuery, cancellationToken);

        await RecordAgentRunAsync(profile.Name, answer);
        return answer;
    }

    private static async Task<string> AddErpContextAsync(
        ErpAgentProfile profile,
        AgentPermissionSettings permissions,
        string input,
        CancellationToken cancellationToken)
    {
        if (!permissions.CanReadErpSummary || !ErpContextSnapshotService.IsEligible(profile.Id))
        {
            return input;
        }

        string snapshot = await ErpContextSnapshotService.BuildAsync(profile.Id, cancellationToken);
        string message = snapshot + "\n\nKullanıcı isteği:\n" + input;
        return message.Length <= AgentChatPromptBuilder.MaximumMessageLength ? message : input;
    }

    private async Task<string> CompleteWithAvailableProviderAsync(
        ErpAgentProfile profile,
        string prompt,
        string? searchQuery,
        CancellationToken cancellationToken)
    {
        bool nimAvailable = profile.Id != "researcher" && IsNimConfigured(_nim);
        if (CloudAgentClient.IsConfigured)
        {
            try
            {
                return "[Cloudflare Workers AI] " +
                       await _cloudAgent.CompleteAsync(profile.Id, prompt, cancellationToken, searchQuery);
            }
            catch (Exception exception) when (
                nimAvailable &&
                !cancellationToken.IsCancellationRequested &&
                exception is HttpRequestException or InvalidOperationException or InvalidDataException)
            {
                return "[NVIDIA NIM · Cloudflare yanıt vermedi] " +
                       await CompleteWithNimAsync(profile, prompt, cancellationToken);
            }
        }

        if (nimAvailable)
        {
            return "[NVIDIA NIM] " + await CompleteWithNimAsync(profile, prompt, cancellationToken);
        }

        throw new InvalidOperationException(profile.Id == "researcher"
            ? "Araştırmacı kaynaklı arama için Cloudflare Worker gerektirir. Ajan Yetkileri ve Bulut bölümünden yapılandırın."
            : "Hiçbir yapay zeka sağlayıcısı yapılandırılmadı. Ajan Yetkileri ve Bulut bölümünden Cloudflare Worker'ı veya NVIDIA NIM ekranından API anahtarını ayarlayın.");
    }

    private Task<string> CompleteWithNimAsync(ErpAgentProfile profile, string prompt, CancellationToken cancellationToken)
    {
        string system = $"Sen Arsan Gaz ERP içindeki {profile.Name} adlı ajansın. {profile.SystemPrompt} " +
                        "Yalnızca kullanıcının mesajını ve mesajda 'ERP özeti' olarak verilen salt-okunur veriyi kullan. " +
                        "Kayıt, müşteri iletişimi, ödeme veya başka bir harici işlemi yapmış gibi gösterme. " +
                        "Türkçe, açık ve uygulanabilir yanıt ver; belirsizliği belirt.";
        return _nim.CompleteChatAsync(
            [new NvidiaNimService.ChatMessage("system", system), new NvidiaNimService.ChatMessage("user", prompt)],
            cancellationToken);
    }

    private static bool IsNimConfigured(NvidiaNimService nim)
    {
        try
        {
            return nim.IsConfigured;
        }
        catch (Exception exception) when (
            exception is System.Security.Cryptography.CryptographicException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static async Task<string> ListCollectionRemindersAsync(CancellationToken cancellationToken)
    {
        DateTime today = DateTime.Today;
        List<CollectionReminder> pending = await new CollectionReminderService().ListAsync(false, cancellationToken);
        if (pending.Count == 0)
        {
            return "Bekleyen tahsilat hatırlatması yok. Eklemek için: @Ece /tahsilat Firma | YYYY-MM-DD | tutar | not";
        }

        return $"Bekleyen {pending.Count} tahsilat:" + Environment.NewLine +
               string.Join(Environment.NewLine, pending.Take(20).Select(reminder =>
                   "• " + CollectionReminderService.Describe(reminder, today)));
    }

    private static async Task<string> AddCollectionReminderAsync(string arguments, CancellationToken cancellationToken)
    {
        const string usage = "Kullanım: /tahsilat Firma adı | YYYY-MM-DD | tutar (ör. 12.500,00) | not (isteğe bağlı)";
        string[] fields = arguments.Split('|', 4, StringSplitOptions.TrimEntries);
        if (fields.Length < 3 ||
            !DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly dueDate) ||
            !CollectionReminderService.TryParseAmount(fields[2], out decimal amount))
        {
            return usage;
        }

        try
        {
            CollectionReminder reminder = await new CollectionReminderService().AddAsync(
                new CollectionReminderInput(
                    fields[0],
                    amount,
                    "TRY",
                    dueDate.ToDateTime(TimeOnly.MinValue),
                    1,
                    false,
                    fields.Length == 4 ? fields[3] : null),
                cancellationToken);
            return "Tahsilat hatırlatması kaydedildi: " + CollectionReminderService.Describe(reminder, DateTime.Today) +
                   ". Ödeme gününden 1 gün önce masaüstünde ve yapılandırılmışsa Telegram'da hatırlatılacak.";
        }
        catch (ArgumentException exception)
        {
            return exception.Message + " " + usage;
        }
    }

    private static bool HasLocalCommandPermission(string agentId, string input)
    {
        AgentPermissionSettings permissions = CloudAgentSettingsStore.GetPermissions(agentId);
        string command = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0];
        bool permissionEnabled = command.ToLowerInvariant() switch
        {
            "/not" => permissions.CanCreateCrmNote,
            "/takip" => permissions.CanScheduleCrmFollowUp,
            "/gorev" => permissions.CanCreateErpTask,
            "/tahsilat" => permissions.CanManageCollectionReminders,
            "/denetle" => permissions.CanDelegateTasks,
            _ => false
        };
        return AgentChatRoutingPolicy.CanExecuteLocalCommand(agentId, input, permissionEnabled);
    }

    private static async Task<string> ExecuteExplicitCommandAsync(
        ErpAgentProfile profile,
        string input,
        bool mayExecuteCommand,
        CancellationToken cancellationToken)
    {
        string[] parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string command = parts[0].ToLowerInvariant();
        string arguments = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        switch (command)
        {
            case "/komutlar":
                return """
                Güvenli ERP chat komutları:
                @Defne /not Firma adı | Not metni
                @Defne /takip Firma adı | YYYY-MM-DD
                @Can /gorev Görev açıklaması | YYYY-MM-DD (tarih isteğe bağlı)
                @Ece /tahsilat Firma adı | YYYY-MM-DD | tutar | not — ödeme günü hatırlatması kaydeder.
                @Ece /tahsilatlar — bekleyen tahsilatları listeler.
                @Gözcü /denetle Görev açıklaması — uzman ajanlara görev dağıtır ve sonuçları denetler.
                @Jarvis /denetle Görev açıklaması — eski komut biçimi; aynı Gözcü denetimli akışı başlatır.
                /komutlar
                Etiketsiz konuşma mesajlarına Jarvis yanıt verir; @ajan etiketli mesajlar yalnızca seçilen uzmana gider. Etiketsiz komutlar Gözcü'de reddedilir; yazma işlemi için ilgili ajanı açıkça çağırıp iznini etkinleştirin.
                E-posta, fatura, ödeme, silme ve dosya/kod değişikliği komutları desteklenmez.
                """;
            case "/tahsilat":
                if (!mayExecuteCommand || profile.Id != "finance")
                {
                    return "Tahsilat hatırlatmasını yalnızca @Ece açıkça çağrıldığında kaydedebilirsiniz.";
                }

                return await AddCollectionReminderAsync(arguments, cancellationToken);
            case "/not":
                if (!mayExecuteCommand || profile.Id != "crm")
                {
                    return "CRM notunu yalnızca @Defne açıkça çağrıldığında kaydedebilirsiniz.";
                }

                return await AddCrmNoteAsync(arguments, cancellationToken);
            case "/takip":
                if (!mayExecuteCommand || profile.Id != "crm")
                {
                    return "CRM takibini yalnızca @Defne açıkça çağrıldığında planlayabilirsiniz.";
                }

                return await ScheduleFollowUpAsync(arguments, cancellationToken);
            case "/gorev":
                if (!mayExecuteCommand || profile.Id != "planner")
                {
                    return "ERP görevini yalnızca @Can açıkça çağrıldığında oluşturabilirsiniz.";
                }

                return await CreateErpTaskAsync(arguments, cancellationToken);
            case "/denetle":
                if (!mayExecuteCommand || profile.Id is not ("supervisor" or "jarvis"))
                {
                    return "Ajan ekibi görevini yalnızca @Gözcü açıkça çağrıldığında kuyruğa ekleyebilirsiniz.";
                }

                return await QueueAgentWatcherTaskAsync(arguments, cancellationToken);
            default:
                return "Bu komut izin verilen dahili işlemler arasında değil. Kullanılabilir komutlar için /komutlar yazın.";
        }
    }

    private static async Task<string> AddCrmNoteAsync(string arguments, CancellationToken cancellationToken)
    {
        string[] fields = arguments.Split('|', 2, StringSplitOptions.TrimEntries);
        if (fields.Length != 2 || string.IsNullOrWhiteSpace(fields[0]) || string.IsNullOrWhiteSpace(fields[1]))
        {
            return "Kullanım: /not Firma adı | Not metni";
        }

        if (fields[1].Length > 1500)
        {
            return "Not en fazla 1.500 karakter olabilir.";
        }

        await using CrmDbContext db = new();
        CrmCompany? company = await FindUniqueCompanyAsync(db, fields[0], cancellationToken);
        if (company is null)
        {
            return await DescribeCompanyMatchAsync(db, fields[0], cancellationToken);
        }

        db.CrmNotes.Add(new CrmNote { CrmCompanyId = company.Id, NoteText = fields[1] });
        db.CrmActivities.Add(new CrmActivity
        {
            CrmCompanyId = company.Id,
            ActivityType = "Chat notu",
            Description = $"ERP chat üzerinden {Environment.UserName} tarafından CRM notu eklendi."
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"Not {company.CompanyName} CRM kartına kaydedildi.";
    }

    private static async Task<string> ScheduleFollowUpAsync(string arguments, CancellationToken cancellationToken)
    {
        string[] fields = arguments.Split('|', 2, StringSplitOptions.TrimEntries);
        if (fields.Length != 2 ||
            !DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly dueDate))
        {
            return "Kullanım: /takip Firma adı | YYYY-MM-DD";
        }

        await using CrmDbContext db = new();
        CrmCompany? company = await FindUniqueCompanyAsync(db, fields[0], cancellationToken);
        if (company is null)
        {
            return await DescribeCompanyMatchAsync(db, fields[0], cancellationToken);
        }

        DateTime? previous = company.FollowUpAtUtc;
        company.FollowUpAtUtc = DateTime.SpecifyKind(dueDate.ToDateTime(TimeOnly.MinValue), DateTimeKind.Local).ToUniversalTime();
        db.CrmActivities.Add(new CrmActivity
        {
            CrmCompanyId = company.Id,
            ActivityType = "Takip planı",
            Description = $"{previous?.ToLocalTime().ToString("dd.MM.yyyy") ?? "Önceki takip yok"} tarihinden {dueDate:dd.MM.yyyy} tarihine güncellendi; kaynak: ERP chat."
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"{company.CompanyName} için takip {dueDate:dd.MM.yyyy} tarihine kaydedildi.";
    }

    private static async Task<string> CreateErpTaskAsync(string arguments, CancellationToken cancellationToken)
    {
        string[] fields = arguments.Split('|', 2, StringSplitOptions.TrimEntries);
        string title = fields[0].Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Length > 200)
        {
            return "Görev açıklaması 1-200 karakter arasında olmalıdır. Kullanım: /gorev Görev açıklaması | YYYY-MM-DD";
        }

        DateTime? dueDate = null;
        if (fields.Length == 2)
        {
            if (!DateOnly.TryParseExact(fields[1], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateOnly parsed))
            {
                return "Tarih yyyy-MM-dd biçiminde olmalıdır. Örnek: /gorev Teklifi ara | 2026-10-03";
            }

            dueDate = parsed.ToDateTime(TimeOnly.MinValue);
        }

        await using ArsanGazDbContext db = new();
        db.Tasks.Add(new ErpTask
        {
            Title = title,
            Description = "ERP ajan sohbeti üzerinden açık kullanıcı komutuyla oluşturuldu.",
            DueDate = dueDate,
            Category = "Ajan görevi",
            Priority = "Normal"
        });
        await db.SaveChangesAsync(cancellationToken);
        return $"ERP görevi oluşturuldu: {title}" + (dueDate is DateTime date ? $" (son tarih {date:dd.MM.yyyy})." : ".");
    }

    private static async Task<string> QueueAgentWatcherTaskAsync(string arguments, CancellationToken cancellationToken)
    {
        string title = arguments.Trim();
        if (title.Length is < 4 or > 4096)
        {
            return "Görev açıklaması 4-4096 karakter olmalıdır. Kullanım: @Gözcü /denetle Görev açıklaması (eski biçim: @Jarvis).";
        }

        string queueDirectory = AgentWatcherPaths.QueueDirectory;
        Directory.CreateDirectory(queueDirectory);

        long? notificationChatId = GetTelegramNotificationChatId();
        string id = Guid.NewGuid().ToString("N");
        string destination = Path.Combine(queueDirectory, id + ".task.json");
        string temporary = destination + ".tmp";
        var task = new
        {
            Id = id,
            Title = title,
            Type = "Ajan Ekibi Sohbeti",
            Priority = "Normal",
            Provider = "Cloudflare Workers AI",
            Status = "Bekliyor",
            Created = DateTime.Now,
            NotificationChatId = notificationChatId
        };
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(task, new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
        File.Move(temporary, destination);
        return notificationChatId is null
            ? "Görev Gözcü kuyruğuna eklendi. Telegram bildirimi için AgentWatcher'da bot ve izinli chat ID yapılandırılmamış; sonuç ERP görev raporlarında kalacak."
            : "Görev Gözcü kuyruğuna eklendi. Tamamlanma/başarısızlık raporu yapılandırılmış Telegram sohbetine iletilecek; dış işlem veya ERP kaydı değişikliği yapılmayacak.";
    }

    internal static long? GetTelegramNotificationChatId()
    {
        if (!File.Exists(AgentWatcherPaths.SettingsFile))
        {
            return null;
        }

        try
        {
            using JsonDocument settings = JsonDocument.Parse(File.ReadAllText(AgentWatcherPaths.SettingsFile));
            if (!settings.RootElement.TryGetProperty("TelegramChatId", out JsonElement chatIdValue))
            {
                return null;
            }

            string? chatIdText = chatIdValue.ValueKind switch
            {
                JsonValueKind.String => chatIdValue.GetString(),
                JsonValueKind.Number => chatIdValue.GetRawText(),
                _ => null
            };
            if (string.IsNullOrWhiteSpace(chatIdText))
            {
                return null;
            }

            if (!long.TryParse(chatIdText, NumberStyles.Integer, CultureInfo.InvariantCulture, out long chatId) ||
                chatId == 0)
            {
                throw new InvalidDataException("AgentWatcher Telegram chat ID sayısal ve sıfırdan farklı olmalıdır.");
            }

            return chatId;
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("AgentWatcher ayar dosyası geçerli JSON değil; görev kuyruğa alınmadı.", exception);
        }
    }

    private static async Task<CrmCompany?> FindUniqueCompanyAsync(CrmDbContext db, string query, CancellationToken cancellationToken)
    {
        string normalized = query.Trim();
        if (normalized.Length is < 2 or > 150)
        {
            return null;
        }

        List<CrmCompany> matches = await FindCompanyMatches(db, normalized)
            .ToListAsync(cancellationToken);

        if (matches.Count != 1)
        {
            return null;
        }

        return await db.CrmCompanies.FirstAsync(company => company.Id == matches[0].Id, cancellationToken);
    }

    private static async Task<string> DescribeCompanyMatchAsync(CrmDbContext db, string query, CancellationToken cancellationToken)
    {
        if (query.Trim().Length < 2)
        {
            return "Firma adı en az iki karakter olmalıdır.";
        }

        List<string> matches = await FindCompanyMatches(db, query.Trim())
            .Select(company => company.CompanyName)
            .ToListAsync(cancellationToken);

        return matches.Count switch
        {
            0 => "Firma bulunamadı. CRM kartındaki firma adını kontrol edin.",
            1 => "Firma eşleştirmesi yapılamadı; firma adını CRM'deki biçimiyle girip tekrar deneyin.",
            _ => "Birden fazla firma eşleşti. Lütfen daha ayırt edici ad girin: " + string.Join(", ", matches)
        };
    }

    private static IQueryable<CrmCompany> FindCompanyMatches(CrmDbContext db, string query) =>
        db.CrmCompanies
            .AsNoTracking()
            .Where(company => EF.Functions.Like(company.CompanyName, $"%{EscapeLike(query)}%", "\\"))
            .OrderBy(company => company.CompanyName)
            .Take(6);

    private static string EscapeLike(string value) =>
        value.Replace("\\", "\\\\", StringComparison.Ordinal)
             .Replace("%", "\\%", StringComparison.Ordinal)
             .Replace("_", "\\_", StringComparison.Ordinal);

    private static async Task<string> GetSupervisorResponseAsync(string input, CancellationToken cancellationToken)
    {
        SupervisorReport? report = await SystemSupervisorService.ReadLatestReportAsync();
        if (report is null)
        {
            return "Gözcü mesajınızı aldı; henüz ilk sağlık kontrolünü tamamlamadı. Biraz bekleyip tekrar sorun.";
        }

        string checks = string.Join(Environment.NewLine, report.Checks.Select(check =>
            $"{(check.IsHealthy ? "OK" : "UYARI")} · {check.Name}: {check.Details}"));
        TimeSpan reportAge = DateTime.Now - report.CheckedAtLocal;
        string freshness = reportAge > TimeSpan.FromMinutes(2)
            ? $"GÜNCEL DEĞİL · son kontrol {reportAge.TotalMinutes:N0} dakika önce"
            : "Güncel";
        return $"Gözcü sistem sağlık raporu · {freshness}" + Environment.NewLine +
               $"Son kontrol: {report.CheckedAtLocal:dd.MM.yyyy HH:mm:ss}{Environment.NewLine}" +
               $"Genel durum: {(report.IsHealthy ? "Sağlıklı" : "İnceleme gerekli")}{Environment.NewLine}" +
               checks;
    }

    private static async Task RecordAgentRunAsync(string agentName, string answer)
    {
        await using ArsanGazDbContext db = new();
        DateTime completedAt = DateTime.UtcNow;
        db.AgentRuns.Add(new AgentRun
        {
            StartedAtUtc = completedAt,
            CompletedAtUtc = completedAt,
            Succeeded = true,
            FindingCount = 0,
            CriticalCount = 0,
            Summary = $"{agentName}: {answer[..Math.Min(answer.Length, 2000)]}",
            FindingsJson = JsonSerializer.Serialize(Array.Empty<string>())
        });
        await db.SaveChangesAsync();
    }
}
