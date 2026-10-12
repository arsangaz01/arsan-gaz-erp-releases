using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ArsanGazERP.AgentWatcher;
using ArsanGazERP.ErpMcpServer;
using ArsanGazERP.Services;

namespace ArsanGazERP.ServerAgent;

// Server-side Telegram commands. ERP questions are answered here from PostgreSQL; anything that needs Cursor or the
// office PC is written to the shared CopilotInbox, which the PC pulls (Sync-ServerInbox.ps1) when it is on.
internal sealed class ServerCommands(
    TelegramCopilotInbox copilotInbox,
    Func<int> pendingOutbox,
    ReleaseClient? releaseClient = null,
    Func<string, bool>? notify = null,
    TimeSpan? apkPollInterval = null)
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly DateTime StartedUtc = DateTime.UtcNow;
    private static readonly Regex CommitId = new("^[0-9a-f]{7,40}$", RegexOptions.CultureInvariant);
    private readonly ReleaseClient release = releaseClient ?? ReleaseClient.Default;
    private readonly Func<string, bool> notify = notify ?? TelegramOutbox.TryQueue;
    private readonly TimeSpan apkPollInterval = apkPollInterval ?? TimeSpan.FromMinutes(3);
    private int releaseRunning;

    public Task? BackgroundRelease { get; private set; }

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
        "/yayin — Cursor'un hazırladığı değişiklikler, CI sonucu ve onay komutu\n" +
        "/yayin_onay <kod> [zorla] — onaylanan değişikliği main'e alır, web/MobileApi'yi yayınlar\n" +
        "/apk — mobil uygulamanın imzalı APK'sını GitHub'dan alıp /indir'e koyar\n" +
        "/geri_al — web/MobileApi'yi önceki sürüme döndürme (onaylı)\n" +
        "/yayin_durum — sunucudaki sürümler ve yedekler\n" +
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
            case "/yayin":
                return await PendingReleaseAsync(cancellationToken);
            case "/yayin_onay":
                return StartPublish(argument);
            case "/apk":
                return StartBackground("📱 APK kontrol ediliyor; sonuç buraya gelecek.",
                    async token => notify((await FollowApkAsync(string.Empty, token)).Text));
            case "/geri_al":
                return await RollbackPreviewAsync(cancellationToken);
            case "/geri_al_onay":
                return StartBackground("↩️ Geri alma başladı; sonuç 1-2 dakika içinde buraya gelecek.", RollbackAsync);
            case "/yayin_durum":
                return (await release.RunAsync("status", string.Empty, string.Empty, TimeSpan.FromMinutes(2), cancellationToken)).Text;
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

    private async Task<string> PendingReleaseAsync(CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref releaseRunning) == 1)
        {
            return "⏳ Bir yayın işlemi sürüyor; bitince sonuç buraya gelecek.";
        }

        ReleaseResult result = await release.RunAsync("pending", string.Empty, string.Empty, TimeSpan.FromMinutes(3), cancellationToken);
        if (!result.Succeeded)
        {
            return result.Finished ? "❌ " + result.Text.TrimStart('❌', ' ') : "⏳ " + result.Text + " Birkaç dakika sonra /yayin yazın.";
        }

        string target = result.Value("TARGET");
        string code = target.Length >= 7 ? target[..7] : target;
        bool somethingToPublish = result.Value("PENDING") == "1" || result.Value("MOBILEAPI") == "1";
        StringBuilder text = new(result.Text);
        text.AppendLine().AppendLine();
        if (result.Value("CONFLICT") == "1")
        {
            text.Append("Çakışma çözülmeden yayın yapılamaz.");
        }
        else if (!somethingToPublish || code.Length == 0)
        {
            text.Append("Yayınlanacak bir şey yok.");
            if (CommitId.IsMatch(target))
            {
                ReleaseResult live = await release.RunAsync("verify", target, string.Empty, TimeSpan.FromMinutes(3), cancellationToken);
                text.AppendLine().AppendLine().AppendLine("Canlı durum (main " + code + "):")
                    .Append(live.Finished && live.Text.Length > 0 ? live.Text : "❓ doğrulanamadı " + live.Text);
            }
        }
        else if (result.Value("CI") == "success")
        {
            text.Append($"Yayınlamak için: /yayin_onay {code}");
        }
        else
        {
            text.Append(result.Value("CI") == "pending"
                ? "CI hâlâ sürüyor; birkaç dakika sonra /yayin ile tekrar bakın."
                : $"CI başarılı görünmüyor. Yine de yayınlamak için: /yayin_onay {code} zorla");
        }

        return text.ToString();
    }

    private string StartPublish(string argument)
    {
        string[] parts = argument.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length is 0 or > 2 || !CommitId.IsMatch(parts[0]) || (parts.Length == 2 && parts[1] != "zorla"))
        {
            return "Kullanım: /yayin_onay <kod> [zorla]. Kodu /yayin mesajından alın.";
        }

        string commit = parts[0];
        string force = parts.Length == 2 ? "force" : string.Empty;
        return StartBackground(
            $"🚀 Yayın başladı ({commit}). Birleştirme, derleme, test örneği ve prod kontrolü 5-15 dakika sürer; sonuç buraya gelecek.",
            token => PublishAsync(commit, force, token));
    }

    private string StartBackground(string startedText, Func<CancellationToken, Task> work)
    {
        if (Interlocked.CompareExchange(ref releaseRunning, 1, 0) != 0)
        {
            return "⏳ Bir yayın işlemi zaten sürüyor; bitince sonuç buraya gelecek.";
        }

        BackgroundRelease = Task.Run(async () =>
        {
            try
            {
                await work(CancellationToken.None);
            }
            catch (Exception exception)
            {
                Log.Write("RELEASE", "Yayın işi hata verdi: " + exception);
                notify("❌ Yayın işi beklenmedik şekilde durdu: " + exception.GetBaseException().Message);
            }
            finally
            {
                Volatile.Write(ref releaseRunning, 0);
            }
        });
        return startedText;
    }

    // The final message says "tamamlandı" only when `arsangaz-release verify` confirms every server-side part is live.
    private async Task PublishAsync(string commit, string force, CancellationToken cancellationToken)
    {
        ReleaseResult result = await release.RunAsync("publish", commit, force, TimeSpan.FromHours(2), cancellationToken);
        if (!result.Finished)
        {
            notify($"⏳ Yayın sürüyor ({commit}); 2 saatte bitmedi. Durum için /yayin_durum yazın.\n\n{result.Text}");
            return;
        }

        string? apkText = null;
        if (result.Succeeded && result.Value("APK") == "needed")
        {
            notify($"🚀 Sunucu adımları bitti ({commit}); telefon APK'sı bekleniyor. Son kontrol listesi APK adımından sonra gelecek.\n\n{result.Text}");
            apkText = (await FollowApkAsync(result.Value("MAIN"), cancellationToken)).Text;
        }

        string main = result.Value("MAIN");
        ReleaseResult verify = await release.RunAsync(
            "verify", CommitId.IsMatch(main) ? main : commit, string.Empty, TimeSpan.FromMinutes(5), cancellationToken);
        notify(FormatReleaseReport(commit, result, verify, apkText));
    }

    internal static string FormatReleaseReport(string commit, ReleaseResult publish, ReleaseResult verify, string? apkText)
    {
        bool verified = publish.Succeeded && verify.Succeeded && verify.Value("VERIFIED") == "1";
        string[] missing = verify.Value("MISSING").Split(',', StringSplitOptions.RemoveEmptyEntries);
        StringBuilder text = new();
        if (verified)
        {
            text.AppendLine($"✅ Yayın tamamlandı ({commit}): sunucudaki tüm parçalar doğrulandı.");
        }
        else if (!publish.Succeeded)
        {
            text.AppendLine($"❌ Yayın tamamlanamadı ({commit}).");
        }
        else
        {
            string names = missing.Length == 0
                ? "doğrulama çalışmadı"
                : string.Join(", ", missing.Select(part => part switch
                {
                    "main" => "GitHub main",
                    "web" => "web sitesi/MobileApi",
                    "apk" => "telefon APK'sı",
                    _ => part
                }));
            text.AppendLine($"⚠️ Yayın eksik ({commit}): {names}.");
        }

        text.AppendLine().AppendLine("Kontrol listesi:");
        text.AppendLine(verify.Finished && verify.Text.Length > 0 ? verify.Text : "❓ Doğrulama yapılamadı: " + verify.Text);
        text.AppendLine(publish.Value("DESKTOP") == "1"
            ? "⏳ Masaüstü ERP/AgentWatcher: main'de; bilgisayar açılınca AutoSync uygular (sunucudan doğrulanamaz)."
            : "➖ Masaüstü ERP/AgentWatcher: bu yayında değişiklik yok.");

        if (!verified)
        {
            List<string> next = [];
            if (!publish.Succeeded) next.Add("hata nedenini aşağıda okuyun, düzeltip /yayin ile tekrar bakın");
            if (missing.Contains("apk")) next.Add("/apk ile tekrar deneyin (sunucu da 15 dakikada bir kendisi dener)");
            if (missing.Contains("web")) next.Add("/yayin_durum ile bakın, gerekirse /yayin ile yeniden yayınlayın");
            if (missing.Contains("main")) next.Add("/yayin ile main durumunu kontrol edin");
            if (next.Count == 0) next.Add("/yayin_durum ile durumu kontrol edin");
            text.AppendLine().AppendLine("Sonraki adım: " + string.Join("; ", next) + ".");
        }

        if (apkText is { Length: > 0 })
        {
            text.AppendLine().AppendLine("APK adımı:").AppendLine(apkText);
        }

        text.AppendLine().AppendLine("Yayın adımları:").Append(publish.Text);
        return text.ToString().TrimEnd();
    }

    private const int MaxApkRetries = 5;

    // The signed APK is built by GitHub Actions after main is pushed; poll until it is published or clearly stuck.
    // Transient problems (GitHub API, download, a crashed runner) are retried with backoff before giving up.
    private async Task<(bool Published, string Text)> FollowApkAsync(string mainCommit, CancellationToken cancellationToken)
    {
        string argument = CommitId.IsMatch(mainCommit) && mainCommit.Length == 40 ? mainCommit : string.Empty;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(90);
        bool announcedWaiting = false;
        int failures = 0;
        while (true)
        {
            ReleaseResult result = await release.RunAsync("apk", argument, string.Empty, TimeSpan.FromMinutes(15), cancellationToken);
            string state = result.Value("APK_STATE");
            if (result.Finished && state == "published")
            {
                return (true, result.Text);
            }

            if (result.Finished && state is "failed" or "blocked")
            {
                return (false, result.Text + "\n❌ APK yayınlanamadı. Sorun giderilince /apk ile tekrar deneyin.");
            }

            TimeSpan delay = apkPollInterval;
            if (state != "waiting")
            {
                failures++;
                Log.Write("RELEASE", $"APK adımı geçici hata ({failures}/{MaxApkRetries}): {Shorten(result.Text, 300)}");
                if (failures >= MaxApkRetries)
                {
                    return (false, $"❌ APK adımı {failures} denemede başarısız oldu.\nNeden: {Shorten(result.Text, 800)}\n/apk ile tekrar deneyin.");
                }

                delay = TimeSpan.FromTicks(Math.Min(apkPollInterval.Ticks << (failures - 1), TimeSpan.FromMinutes(15).Ticks));
            }
            else if (!announcedWaiting)
            {
                notify(result.Text + " Hazır olunca bildireceğim.");
                announcedWaiting = true;
            }

            if (DateTime.UtcNow + delay > deadline)
            {
                return (false, "❌ APK 90 dakika içinde hazır olmadı. GitHub Actions'taki CI çalışmasını kontrol edin; sonra /apk ile tekrar deneyin.\n" + result.Text);
            }

            await Task.Delay(delay, cancellationToken);
        }
    }

    private string? apkWatchAlerted;
    private DateTime? apkBehindSinceUtc;

    // Safety net run every 15 minutes by ServerAgentHost: if main's ApplicationVersion is ahead of what phones are
    // offered, the release script publishes the CI artifact; a failure or a long wait is reported once per commit.
    public async Task<string?> WatchApkOnceAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref releaseRunning, 1, 0) != 0)
        {
            return null;
        }

        try
        {
            ReleaseResult result = await release.RunAsync("apkwatch", string.Empty, string.Empty, TimeSpan.FromMinutes(20), cancellationToken);
            if (!result.Finished)
            {
                return null;
            }

            string alertFile = Path.Combine(ServerPaths.StateRoot, "apkwatch-alerted.txt");
            apkWatchAlerted ??= File.Exists(alertFile) ? File.ReadAllText(alertFile).Trim() : string.Empty;
            if (result.Succeeded && result.Value("APK_BEHIND") == "0")
            {
                apkBehindSinceUtc = null;
                return "current";
            }

            string state = result.Value("APK_STATE");
            if (result.Succeeded && state == "held")
            {
                apkBehindSinceUtc = null;
                return "held";
            }

            if (result.Value("APK_BEHIND") == "1" && state == "published")
            {
                apkBehindSinceUtc = null;
                notify("📱 Otomatik kontrol: yayınlanmamış kalan APK bulundu ve /indir'e kondu.\n" + result.Text);
                return "published";
            }

            apkBehindSinceUtc ??= DateTime.UtcNow;
            bool stuck = state is "failed" or "blocked" || !result.Succeeded || result.Value("APK_BEHIND") != "1"
                         || DateTime.UtcNow - apkBehindSinceUtc > TimeSpan.FromHours(2);
            string key = (result.Value("MAIN") is { Length: > 0 } main ? main : "hata") + ":" + (stuck ? "stuck" : "wait");
            if (stuck && apkWatchAlerted != key)
            {
                apkWatchAlerted = key;
                File.WriteAllText(alertFile, key);
                notify("⚠️ Otomatik APK kontrolü: main'deki mobil uygulama sürümü telefonlara sunulmuyor.\n" + result.Text +
                       "\n/apk ile tekrar deneyin; düzelmezse GitHub Actions'taki CI çalışmasına bakın.");
            }

            return state.Length > 0 ? state : "error";
        }
        finally
        {
            Volatile.Write(ref releaseRunning, 0);
        }
    }

    private static string Shorten(string text, int length) => text.Length <= length ? text : text[..length] + "…";

    private async Task<string> RollbackPreviewAsync(CancellationToken cancellationToken)
    {
        ReleaseResult result = await release.RunAsync("status", string.Empty, string.Empty, TimeSpan.FromMinutes(2), cancellationToken);
        return result.Text + "\n\nWeb sitesi/MobileApi en son yedeğe döndürülür. Onaylıyorsanız: /geri_al_onay";
    }

    private async Task RollbackAsync(CancellationToken cancellationToken)
    {
        ReleaseResult result = await release.RunAsync("rollback", string.Empty, string.Empty, TimeSpan.FromMinutes(20), cancellationToken);
        notify((result.Succeeded ? "" : "❌ Geri alma tamamlanamadı\n") + result.Text);
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
