using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ArsanGazERP.AgentWatcher;
using ArsanGazERP.ErpMcpServer;
using ArsanGazERP.Services;

namespace ArsanGazERP.ServerAgent;

// Server-side Telegram commands. ERP questions are answered here from PostgreSQL; anything that needs Cursor or the
// office PC is written to the shared CopilotInbox, which the PC pulls (Sync-ServerInbox.ps1) when it is on.
internal sealed class ServerCommands(TelegramCopilotInbox copilotInbox, Func<int> pendingOutbox)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly DateTime StartedUtc = DateTime.UtcNow;

    private static readonly HashSet<string> PcOnlyCommands = new(StringComparer.Ordinal)
    {
        "/gorev", "/ekip", "/ara", "/saglik-gorev", "/workflow-onay", "/workflows", "/incele", "/copilot-yanit",
        "/yanit", "/ek", "/test-saatlik-baslat", "/test-saatlik-durdur"
    };

    public static string Help =>
        "☁️ Arsan Gaz sunucu asistanı (7/24, bilgisayar kapalıyken de çalışır)\n" +
        "/durum — sunucu, bilgisayar ve kuyruk durumu\n" +
        "/ozet — ERP + CRM özeti\n" +
        "/musteri <ad> — müşteri ara\n" +
        "/tup <barkod/seri> — tüp bul\n" +
        "/musterideki [müşteri] — müşterideki tüpler\n" +
        "/uzun [gün] — müşteride uzun kalan tüpler\n" +
        "/test [gün] — testi yaklaşan tüpler\n" +
        "/hareketler [gün] — son hareketler\n" +
        "/tahmin — sipariş tahmini\n" +
        "/crm <firma> — CRM firma ara\n" +
        "/takipler [gün] — CRM takipleri\n" +
        "/tahsilat — bekleyen tahsilat hatırlatmaları\n" +
        "/emir <istek> veya düz mesaj — Cursor'a iletilir (bilgisayar kapalıysa kuyruğa alınır)\n" +
        "/gorev, /ekip, /ara gibi AgentWatcher komutları bilgisayar gerektirir.";

    public static bool IsPcOnline(out DateTimeOffset? lastSeen)
    {
        lastSeen = null;
        try
        {
            if (!File.Exists(ServerPaths.PcSyncStatusFile))
            {
                return false;
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ServerPaths.PcSyncStatusFile));
            if (document.RootElement.TryGetProperty("updatedUtc", out JsonElement value)
                && DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset updated))
            {
                lastSeen = updated;
                return DateTimeOffset.UtcNow - updated < TimeSpan.FromSeconds(90);
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException)
        {
        }

        return false;
    }

    public static (bool Ready, string Text) CursorAgentState()
    {
        try
        {
            if (!File.Exists(ServerPaths.CursorAgentStatusFile))
            {
                return (false, "kurulu değil");
            }

            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(ServerPaths.CursorAgentStatusFile));
            JsonElement root = document.RootElement;
            if (!root.TryGetProperty("updatedUtc", out JsonElement value)
                || !DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset updated)
                || DateTimeOffset.UtcNow - updated > TimeSpan.FromSeconds(90))
            {
                return (false, "çalışmıyor");
            }

            if (root.TryGetProperty("current", out JsonElement current) && current.ValueKind == JsonValueKind.Object)
            {
                return (true, "bir isteği işliyor");
            }

            bool ready = root.TryGetProperty("ready", out JsonElement readyValue) && readyValue.ValueKind == JsonValueKind.True;
            string? reason = root.TryGetProperty("reason", out JsonElement reasonValue) ? reasonValue.GetString() : null;
            return ready ? (true, "hazır (bilgisayar kapalıyken istekleri işler)") : (false, "bekliyor: " + (reason ?? "bilinmiyor"));
        }
        catch (Exception exception) when (exception is IOException or JsonException or InvalidOperationException or UnauthorizedAccessException)
        {
            return (false, "durum okunamadı");
        }
    }

    public async Task<string> HandleAsync(long updateId, string text, long chatId, CancellationToken cancellationToken)
    {
        string trimmed = text.Trim();
        int separator = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        string command = (separator < 0 ? trimmed : trimmed[..separator]).Split('@')[0].ToLowerInvariant();
        string argument = separator < 0 ? string.Empty : trimmed[(separator + 1)..].Trim();
        if (!trimmed.StartsWith('/'))
        {
            return await QueueForCursorAsync(updateId, chatId, trimmed, null, cancellationToken);
        }

        switch (command)
        {
            case "/start":
            case "/yardim":
            case "/help":
                return Help;
            case "/durum":
                return Status();
            case "/ozet":
                return Erp(() => "ERP\n" + ErpTools.Summary() + "\nCRM\n" + ErpTools.CrmSummary());
            case "/musteri":
                return argument.Length == 0 ? "Kullanım: /musteri <ad>" : Erp(() => ErpTools.FindCustomers(argument));
            case "/tup":
                return argument.Length == 0 ? "Kullanım: /tup <barkod veya seri no>" : Erp(() => ErpTools.FindCylinder(argument));
            case "/musterideki":
                return Erp(() => ErpTools.CylindersAtCustomers(argument.Length == 0 ? null : argument));
            case "/uzun":
                return Erp(() => ErpTools.LongAtCustomer(Days(argument, CylinderTracking.LongAtCustomerDays)));
            case "/test":
                return Erp(() => ErpTools.TestDue(Days(argument, 30)));
            case "/hareketler":
                return Erp(() => ErpTools.RecentMovements(Days(argument, 7)));
            case "/tahmin":
                return Erp(ErpTools.ReorderForecasts);
            case "/crm":
                return argument.Length == 0 ? "Kullanım: /crm <firma>" : Erp(() => ErpTools.FindCrmCompanies(argument));
            case "/takipler":
                return Erp(() => ErpTools.CrmFollowUps(Days(argument, 7)));
            case "/tahsilat":
                return await RemindersAsync(cancellationToken);
            case "/emir":
                return argument.Length == 0
                    ? "Kullanım: /emir <istek>"
                    : await QueueForCursorAsync(updateId, chatId, argument, null, cancellationToken);
            case "/yeni":
                return "Sunucu modunda Cursor konuşma bağlamı tutulmuyor; her istek zaten yeni konuşma olarak iletilir.";
            default:
                return PcOnlyCommands.Contains(command)
                    ? $"{command} komutu bilgisayardaki AgentWatcher'ı gerektirir; sunucu modunda çalışmaz. " +
                      "Aynı işi Cursor'a yaptırmak için /emir <istek> yazın."
                    : "Bilinmeyen komut. Komut listesi için /yardim yazın.";
        }
    }

    public Task<string> HandleAttachmentsAsync(
        long updateId,
        string caption,
        long chatId,
        IReadOnlyList<TelegramAttachment> attachments,
        CancellationToken cancellationToken) =>
        QueueForCursorAsync(updateId, chatId, caption, attachments, cancellationToken);

    public static Task<string> HandleCallbackAsync(long updateId, string data, long chatId, CancellationToken cancellationToken) =>
        Task.FromResult("Bu düğme bilgisayardaki AgentWatcher'a ait; sunucu modunda işlenemedi. Gerekirse /emir ile yazın.");

    private async Task<string> QueueForCursorAsync(
        long updateId,
        long chatId,
        string text,
        IReadOnlyList<TelegramAttachment>? attachments,
        CancellationToken cancellationToken)
    {
        if (text.Length > 4096)
        {
            return "İstek 4096 karakterden uzun olamaz. Lütfen daha kısa yazın.";
        }

        TelegramCopilotEnqueueResult result = await copilotInbox.EnqueueAsync(
            updateId,
            text,
            chatId,
            "Bu istek Telegram'dan sunucu asistanı üzerinden geldi. Onay gereken işlerde 'Onaylıyor musun? Evet/Hayır yazın' diye sor.",
            attachments,
            cancellationToken);
        Log.Write("CURSOR", $"İstek kuyruğa yazıldı: {result.Id}{(result.AlreadyQueued ? " (yineleme)" : string.Empty)}");
        if (result.AlreadyQueued)
        {
            return "Bu istek zaten kuyrukta; ikinci kopya oluşturulmadı.";
        }

        if (IsPcOnline(out _))
        {
            return "İstek bilgisayardaki Cursor ajanına iletiliyor. Tamamlanınca rapor buraya gelecek.";
        }

        return CursorAgentState().Ready
            ? "💤 Bilgisayar kapalı; istek sunucudaki Cursor ajanına verildi. Tamamlanınca rapor buraya gelecek."
            : "💤 Bilgisayar kapalı; istek kuyruğa alındı. Bilgisayar açılınca Cursor işleyecek ve rapor buraya gelecek.";
    }

    private string Status()
    {
        StringBuilder text = new();
        text.AppendLine("☁️ Sunucu asistanı çalışıyor.");
        text.AppendLine($"Çalışma süresi: {FormatSpan(DateTime.UtcNow - StartedUtc)}");
        bool pcOnline = IsPcOnline(out DateTimeOffset? lastSeen);
        text.AppendLine(pcOnline
            ? "Bilgisayar: açık (kuyruk eşitlemesi çalışıyor)"
            : "Bilgisayar: kapalı veya eşitleme yok" + (lastSeen is { } seen ? $" (son görülme {seen.ToLocalTime():dd.MM HH:mm})" : string.Empty));
        text.AppendLine("ERP veritabanı: " + DatabaseStatus());
        text.AppendLine("Mobil sunucu: " + (ServiceActive("arsangaz-mobileapi-prod") ? "çalışıyor" : "kapalı"));
        text.AppendLine("Genel adres tüneli: " + (ServiceActive("cloudflared") ? "çalışıyor" : "kapalı"));
        text.AppendLine("Sunucu Cursor ajanı: " + CursorAgentState().Text);
        text.AppendLine($"Cursor kuyruğunda bekleyen: {PendingCursorRequests()}");
        text.Append($"Telegram giden kutusu: {pendingOutbox()}");
        return text.ToString();
    }

    private static int PendingCursorRequests()
    {
        if (!Directory.Exists(ServerPaths.CopilotInbox))
        {
            return 0;
        }

        return Directory.EnumerateFiles(ServerPaths.CopilotInbox, "*.request.json")
            .Count(path => !File.Exists(path[..^".request.json".Length] + ".completion.json"));
    }

    private static string DatabaseStatus()
    {
        try
        {
            string summary = ErpTools.Summary();
            return "erişilebilir · " + summary.Split('\n', 2)[0].Trim();
        }
        catch (Exception exception)
        {
            return "HATA: " + exception.GetBaseException().Message;
        }
    }

    private static bool ServiceActive(string unit)
    {
        try
        {
            using Process process = Process.Start(new ProcessStartInfo("systemctl", ["is-active", "--quiet", unit])
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            return process.WaitForExit(5000) && process.ExitCode == 0;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static async Task<string> RemindersAsync(CancellationToken cancellationToken)
    {
        try
        {
            List<Models.CollectionReminder> pending = await new CollectionReminderService().ListAsync(false, cancellationToken);
            if (pending.Count == 0)
            {
                return "Bekleyen tahsilat hatırlatması yok.";
            }

            return "Bekleyen tahsilatlar:\n" + string.Join("\n",
                pending.Take(30).Select(reminder => "• " + CollectionReminderService.Describe(reminder, DateTime.Today)));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return "Tahsilat listesi alınamadı: " + exception.GetBaseException().Message;
        }
    }

    private static string Erp(Func<string> query)
    {
        try
        {
            string result = query();
            return string.IsNullOrWhiteSpace(result) ? "Sonuç yok." : result;
        }
        catch (Exception exception)
        {
            return "ERP sorgusu çalışmadı: " + exception.GetBaseException().Message;
        }
    }

    private static int Days(string argument, int fallback) =>
        int.TryParse(argument, NumberStyles.Integer, Turkish, out int days) ? days : fallback;

    private static string FormatSpan(TimeSpan span) =>
        span.TotalDays >= 1 ? $"{(int)span.TotalDays} gün {span.Hours} saat" : $"{span.Hours} saat {span.Minutes} dakika";
}
