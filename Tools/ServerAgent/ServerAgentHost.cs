using System.Text;
using System.Text.Json;
using ArsanGazERP.AgentWatcher;
using ArsanGazERP.Services;

namespace ArsanGazERP.ServerAgent;

internal sealed class ServerAgentHost(ServerAgentOptions options)
{
    private readonly TelegramCopilotInbox copilotInbox = new(
        ServerPaths.CopilotInbox, ServerPaths.TelegramOutbox, message => Log.Write("CURSOR", message));
    private TelegramBotService? bot;
    private OutboxSender? outbox;
    private string relayState = "kapalı";
    private string pollingState = "kapalı";
    private string reminderState = "kapalı";
    private string campaignState = "kapalı";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        ServerPaths.Ensure();
        ServerPaths.EnsureNotificationChat(options.ChatId);
        Log.Write("START", "Sunucu ajanı başlıyor: " + options.Describe());
        Log.Write("START", "Veri klasörü: " + ServerPaths.DataRoot);

        List<Task> work = [RunStatusAsync(cancellationToken)];
        if (options.HasTelegram)
        {
            bot = new TelegramBotService(options.BotToken!, options.ChatId);
            outbox = new OutboxSender(bot, options.ChatId, copilotInbox);
            if (options.OutboxEnabled)
            {
                work.Add(outbox.RunAsync(cancellationToken));
            }

            if (options.RemindersEnabled)
            {
                work.Add(RunRemindersAsync(cancellationToken));
            }

            if (options.CanPoll)
            {
                ServerCommands commands = new(copilotInbox, () => outbox.Pending);
                pollingState = "açık";
                Log.Write("TELEGRAM", "Telegram dinleme (getUpdates) başlatılıyor.");
                work.Add(bot.RunAsync(commands.HandleAsync, ServerCommands.HandleCallbackAsync,
                    message => Log.Write("TELEGRAM", message), cancellationToken, commands.HandleAttachmentsAsync));
                if (options.HasRelay)
                {
                    work.Add(RunRelayAsync(commands, cancellationToken));
                }
            }
            else if (options.PollingEnabled)
            {
                pollingState = "istendi ama 7/24 röle ayarı yok (CLOUD_RELAY_URL/TOKEN)";
                Log.Write("TELEGRAM", "Dinleme istendi ama röle ayarı eksik; Worker ile çakışmamak için başlatılmadı.");
            }
        }
        else
        {
            Log.Write("TELEGRAM", "TELEGRAM_BOT_TOKEN/TELEGRAM_CHAT_ID yok: dinleme ve gönderim kapalı; giden kutusu birikir.");
        }

        if (options.CampaignsEnabled)
        {
            work.Add(RunCampaignsAsync(cancellationToken));
        }

        await Task.WhenAll(work);
        bot?.Dispose();
        Log.Write("STOP", "Sunucu ajanı durdu.");
    }

    private async Task RunRelayAsync(ServerCommands commands, CancellationToken cancellationToken)
    {
        using CloudRelayClient relay = new(options.RelayUrl!, options.RelayToken!);
        using PeriodicTimer timer = new(CloudRelayClient.Interval);
        do
        {
            try
            {
                RelayHeartbeat? heartbeat = await relay.HeartbeatAsync(cancellationToken);
                relayState = heartbeat is null ? "Worker sürümünde röle yok" : $"sinyal {DateTime.Now:HH:mm}";
                if (heartbeat is { Deferred.Count: > 0 } && bot is not null)
                {
                    List<long> handled = [];
                    foreach (RelayDeferredCommand command in heartbeat.Deferred.OrderBy(item => item.UpdateId))
                    {
                        handled.Add(command.UpdateId);
                        if (command.ChatId != options.ChatId)
                        {
                            continue;
                        }

                        string reply;
                        try
                        {
                            reply = await commands.HandleAsync(command.UpdateId, command.Text, command.ChatId, cancellationToken);
                        }
                        catch (Exception exception) when (exception is not OperationCanceledException)
                        {
                            reply = "Emir çalıştırılamadı: " + exception.GetBaseException().Message;
                        }

                        await bot.SendMessageAsync($"☁️ Dinleyici yokken gönderdiğiniz emir işlendi:\n{Shorten(command.Text, 300)}\n\n{reply}", cancellationToken);
                    }

                    await relay.AcknowledgeAsync(handled, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or JsonException
                                                  or InvalidOperationException)
            {
                relayState = "hata: " + exception.GetBaseException().Message;
                Log.Write("RELAY", "7/24 röle sinyali gönderilemedi: " + exception.GetBaseException().Message);
            }
        }
        while (await Tick(timer, cancellationToken));
    }

    // Same digest as the desktop ERP monitor; LastNotifiedOn in the database keeps it to once per reminder per day.
    private async Task RunRemindersAsync(CancellationToken cancellationToken)
    {
        reminderState = "açık";
        using PeriodicTimer timer = new(TimeSpan.FromMinutes(30));
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        do
        {
            try
            {
                if (DateTime.Now.Hour is >= 8 and < 21)
                {
                    CollectionReminderDigest? digest = await new CollectionReminderService().PrepareDailyDigestAsync(DateTime.Today, cancellationToken);
                    if (digest is not null)
                    {
                        bool queued = CollectionReminderService.TryQueueTelegram(digest.Text, out string? detail);
                        reminderState = $"son gönderim {DateTime.Now:dd.MM HH:mm} ({digest.Count})";
                        Log.Write("REMINDER", queued ? $"{digest.Count} tahsilat hatırlatması kuyruğa alındı." : "Kuyruğa alınamadı: " + detail);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                reminderState = "hata: " + exception.GetBaseException().Message;
                Log.Write("REMINDER", "Tahsilat kontrolü başarısız: " + exception.GetBaseException().Message);
            }
        }
        while (await Tick(timer, cancellationToken));
    }

    // Disabled by default: the Gmail/WhatsApp credentials live DPAPI-protected on the PC and must be re-entered on
    // the server first. The database worker lease prevents the PC and the server from sending the same step twice.
    private async Task RunCampaignsAsync(CancellationToken cancellationToken)
    {
        try
        {
            CampaignStore store = CampaignStore.CreateForCurrentWindowsUser();
            using CampaignTransport transport = new(
                (_, _) => throw new InvalidOperationException("Microsoft 365 gönderimi sunucuda yok; Gmail veya WhatsApp kullanın."),
                new WhatsAppBusinessService(),
                gmail: new GmailSenderService());
            CampaignService service = new(store, transport);
            CampaignScheduler scheduler = new(service, exception =>
            {
                campaignState = "hata: " + exception.GetType().Name;
                Log.Write("CAMPAIGN", "Zamanlayıcı engellendi: " + exception.GetBaseException().Message);
            });
            campaignState = "açık";
            Log.Write("CAMPAIGN", "Kampanya zamanlayıcısı başlatıldı.");
            await scheduler.RunAsync(cancellationToken);
            using CancellationTokenSource release = new(TimeSpan.FromSeconds(5));
            await store.ReleaseWorkerLeaseAsync(release.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            campaignState = "başlatılamadı: " + exception.GetBaseException().Message;
            Log.Write("CAMPAIGN", campaignState);
        }
    }

    private async Task RunStatusAsync(CancellationToken cancellationToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(30));
        do
        {
            try
            {
                var status = new
                {
                    updatedUtc = DateTime.UtcNow.ToString("o"),
                    telegram = options.HasTelegram,
                    polling = pollingState,
                    pollingConnected = bot?.IsConnected == true && pollingState == "açık",
                    telegramStatus = bot?.StatusText,
                    relay = relayState,
                    reminders = reminderState,
                    campaigns = campaignState,
                    outboxPending = outbox?.Pending ?? CountOutbox(),
                    pcOnline = ServerCommands.IsPcOnline(out _)
                };
                string temporary = ServerPaths.StatusFile + ".tmp";
                await File.WriteAllTextAsync(temporary, JsonSerializer.Serialize(status, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false), cancellationToken);
                File.Move(temporary, ServerPaths.StatusFile, overwrite: true);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                Log.Write("STATUS", "Durum dosyası yazılamadı: " + exception.Message);
            }
        }
        while (await Tick(timer, cancellationToken));
    }

    private static int CountOutbox() =>
        Directory.Exists(ServerPaths.TelegramOutbox) ? Directory.EnumerateFiles(ServerPaths.TelegramOutbox, "*.json").Count() : 0;

    private static async Task<bool> Tick(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";
}
