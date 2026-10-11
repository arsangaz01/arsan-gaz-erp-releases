using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Data.Common;
using ArsanGazERP.Data;

namespace ArsanGazERP.Services;

internal sealed record CrmEmailCandidate(int Id, string Name, string? City, string? Website);

internal sealed record CrmEmailFinding(string Email, string SourceUrl, string? DiscoveredWebsite);

/// <summary>
/// Fills empty CRM e-mail fields from the company's own website (home and contact pages).
/// Only addresses on the company's domain, or free-mail addresses published on its own site, are accepted;
/// directory and social pages are never used as a source. Every change is appended to changes.jsonl for undo.
/// </summary>
internal static partial class CrmEmailFinderService
{
    public const int DefaultBatch = 50;
    public const int MaximumBatch = 400;
    private const int Parallelism = 3;
    private static readonly TimeSpan RetryAfter = TimeSpan.FromDays(30);
    private static readonly UTF8Encoding Utf8 = new(false);
    private static readonly SemaphoreSlim StateGate = new(1, 1);
    private static int running;

    private static readonly string[] DirectoryHosts =
    [
        "eczanelik.com", "instagram.com", "facebook.com", "fb.com", "twitter.com", "x.com", "linkedin.com",
        "youtube.com", "tiktok.com", "google.com", "goo.gl", "maps.app.goo.gl", "yandex.com", "yandex.com.tr",
        "bing.com", "wikipedia.org", "sahibinden.com", "bulurum.com", "cylex.com.tr", "infoisinfo.com.tr",
        "firmasec.com", "firmarehberi.com.tr", "eniyiler.com", "tripadvisor.com", "tripadvisor.com.tr",
        "foursquare.com", "yelp.com", "hepsiburada.com", "trendyol.com", "n11.com", "linktr.ee",
        "nobetcieczane.com", "eczaneler.gen.tr", "doktortakvimi.com", "neredekal.com", "armut.com",
        "yellowpages.com.tr", "rehberim.com", "turkiyefirmarehberi.com", "isletmerehberi.com", "whatsapp.com", "wa.me"
    ];

    private static readonly string[] FreeMailDomains =
    [
        "gmail.com", "hotmail.com", "hotmail.com.tr", "outlook.com", "outlook.com.tr", "yahoo.com", "yahoo.com.tr",
        "yandex.com", "yandex.com.tr", "icloud.com", "mynet.com", "windowslive.com", "live.com", "msn.com"
    ];

    private static readonly string[] IgnoredEmailParts =
    [
        "example", "sentry", "wixpress", "domain.com", "email.com", "yourdomain", "sitename",
        "test.com", "kullanici", "adiniz", "isim@", "name@", "user@", "@2x", "noreply", "no-reply"
    ];

    private static string StateDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "CrmEmailFinder");
    private static string StatusPath => Path.Combine(StateDirectory, "status.json");
    private static string AttemptsPath => Path.Combine(StateDirectory, "attempts.json");
    internal static string ChangesPath => Path.Combine(StateDirectory, "changes.jsonl");
    private static string LockPath => Path.Combine(StateDirectory, "run.lock");

    public static string CrmDatabasePath => ArsanGazERP.Data.CrmDbContext.SharedDatabasePath;

    /// <summary>Starts a background batch and returns immediately with a short Turkish status line.</summary>
    public static async Task<string> StartBatchAsync(int limit, bool searchWeb, string? city, CancellationToken cancellationToken)
    {
        if (!ArsanDatabase.Exists(DatabaseKind.Crm)) return "CRM veritabanı bulunamadı: " + CrmDatabasePath;
        int batch = Math.Clamp(limit <= 0 ? DefaultBatch : limit, 1, MaximumBatch);
        Directory.CreateDirectory(StateDirectory);
        if (Interlocked.CompareExchange(ref running, 1, 0) != 0)
        {
            return "E-posta tarama işi zaten çalışıyor. " + ReadStatusText();
        }

        FileStream? lockStream;
        try
        {
            lockStream = new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (IOException)
        {
            Interlocked.Exchange(ref running, 0);
            return "E-posta tarama işi başka bir uygulamada (ERP veya mobil sunucu) çalışıyor. " + ReadStatusText();
        }

        IReadOnlyList<CrmEmailCandidate> candidates;
        try
        {
            Dictionary<int, DateTime> attempts = await ReadAttemptsAsync(cancellationToken);
            candidates = await LoadCandidatesAsync(searchWeb, city, attempts, batch, cancellationToken);
        }
        catch
        {
            lockStream.Dispose();
            Interlocked.Exchange(ref running, 0);
            throw;
        }

        if (candidates.Count == 0)
        {
            lockStream.Dispose();
            Interlocked.Exchange(ref running, 0);
            return searchWeb
                ? "E-postası boş olup son 30 günde taranmamış CRM firması kalmadı."
                : "Web sitesi kayıtlı, e-postası boş ve son 30 günde taranmamış firma kalmadı. " +
                  "Web sitesi olmayan firmalar için web araması ile tarama başlatılabilir (search_web: true).";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunBatchAsync(candidates, searchWeb, CancellationToken.None);
            }
            finally
            {
                lockStream.Dispose();
                Interlocked.Exchange(ref running, 0);
            }
        });

        int withSite = candidates.Count(candidate => !string.IsNullOrWhiteSpace(candidate.Website));
        return $"CRM e-posta taraması arka planda başladı: {candidates.Count} firma ({withSite} tanesinin web sitesi kayıtlı" +
               (searchWeb ? $", {candidates.Count - withSite} tanesi için web'de resmi site aranacak" : string.Empty) + "). " +
               "Yalnızca firmanın kendi sitesinde yayımlanan adresler boş e-posta alanına yazılır; kimseye e-posta gönderilmez. " +
               "Bitince Telegram'a özet gelir; ilerleme için crm_email_status kullan.";
    }

    public static string ReadStatusText()
    {
        if (!File.Exists(StatusPath)) return "Henüz bir CRM e-posta taraması yapılmadı.";
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(StatusPath, Utf8));
            JsonElement root = document.RootElement;
            string Get(string name) => root.TryGetProperty(name, out JsonElement value) ? value.ToString() : string.Empty;
            bool active = root.TryGetProperty("Running", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
            string head = active ? "Tarama sürüyor" : "Son tarama bitti";
            return $"{head}: {Get("Processed")}/{Get("Total")} firma işlendi, {Get("Found")} e-posta bulundu ve CRM'e yazıldı." +
                   (Get("LastCompany").Length > 0 ? $" Son firma: {Get("LastCompany")}." : string.Empty) +
                   (Get("Summary").Length > 0 ? "\n" + Get("Summary") : string.Empty);
        }
        catch (Exception exception) when (exception is IOException or JsonException)
        {
            return "Tarama durumu okunamadı: " + exception.Message;
        }
    }

    /// <summary>Looks up one company by name and fills its e-mail synchronously (used for single requests).</summary>
    public static async Task<string> FindForCompanyAsync(string name, CancellationToken cancellationToken)
    {
        if (!ArsanDatabase.Exists(DatabaseKind.Crm)) return "CRM veritabanı bulunamadı.";
        string trimmed = name.Trim();
        if (trimmed.Length < 3) return "Firma adı en az 3 karakter olmalı.";
        List<CrmEmailCandidate> matches = [];
        List<string> existing = [];
        await using (DbConnection connection = await OpenAsync(readOnly: true, cancellationToken))
        await using (DbCommand command = ArsanDatabase.Command(connection, null, $"""
                SELECT Id, CompanyName, City, Website, Email FROM CrmCompanies
                WHERE CompanyName {ArsanDatabase.Like} $name ESCAPE '\' ORDER BY length(CompanyName) LIMIT 5
                """, ("$name", "%" + trimmed.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%")))
        {
            await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                string email = reader.IsDBNull(4) ? string.Empty : reader.GetString(4).Trim();
                if (email.Length > 0)
                {
                    existing.Add($"{reader.GetString(1)}: {email}");
                    continue;
                }

                matches.Add(new CrmEmailCandidate(reader.GetInt32(0), reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
            }
        }

        if (matches.Count == 0)
        {
            return existing.Count > 0
                ? "Bu firmaların e-postası zaten kayıtlı: " + string.Join("; ", existing)
                : $"CRM'de \"{trimmed}\" adında firma bulunamadı.";
        }

        CrmEmailCandidate target = matches[0];
        CrmEmailFinding? finding = await FindAsync(target, searchWeb: true, cancellationToken);
        await RecordAttemptsAsync([target.Id], cancellationToken);
        if (finding is null)
        {
            return $"{target.Name} için firmanın kendi sitesinde yayımlanmış e-posta adresi bulunamadı" +
                   (string.IsNullOrWhiteSpace(target.Website) ? " (CRM'de web sitesi yok, web'de resmi site de doğrulanamadı)." : $" (site: {target.Website}).");
        }

        bool saved = await SaveAsync(target, finding, cancellationToken);
        return saved
            ? $"{target.Name}: {finding.Email} bulundu ({finding.SourceUrl}) ve CRM'e yazıldı."
            : $"{target.Name}: {finding.Email} bulundu ({finding.SourceUrl}) ama kayıt bu arada değiştiği için CRM'e yazılmadı.";
    }

    private static async Task RunBatchAsync(IReadOnlyList<CrmEmailCandidate> candidates, bool searchWeb, CancellationToken cancellationToken)
    {
        int processed = 0, found = 0;
        DateTime startedUtc = DateTime.UtcNow;
        List<string> examples = [];
        object sync = new();
        await WriteStatusAsync(true, candidates.Count, 0, 0, null, null, startedUtc);
        try
        {
            using SemaphoreSlim slots = new(Parallelism);
            using SemaphoreSlim searchGate = new(1, 1);
            Task[] work = candidates.Select(async candidate =>
            {
                await slots.WaitAsync(cancellationToken);
                try
                {
                    CrmEmailFinding? finding = null;
                    try
                    {
                        if (string.IsNullOrWhiteSpace(candidate.Website) || IsDirectoryUrl(candidate.Website))
                        {
                            await searchGate.WaitAsync(cancellationToken);
                            try
                            {
                                finding = await FindAsync(candidate, searchWeb, cancellationToken);
                                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                            }
                            finally
                            {
                                searchGate.Release();
                            }
                        }
                        else
                        {
                            finding = await FindAsync(candidate, searchWeb, cancellationToken);
                        }

                        if (finding is not null && await SaveAsync(candidate, finding, cancellationToken))
                        {
                            lock (sync)
                            {
                                found++;
                                if (examples.Count < 8) examples.Add($"{candidate.Name}: {finding.Email}");
                            }
                        }
                    }
                    catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or IOException
                                                          or DbException or UriFormatException or InvalidOperationException)
                    {
                        AppendLog($"{candidate.Name}: {exception.GetBaseException().Message}");
                    }

                    await RecordAttemptsAsync([candidate.Id], cancellationToken);
                    int done;
                    int hits;
                    lock (sync)
                    {
                        done = ++processed;
                        hits = found;
                    }

                    if (done % 5 == 0 || done == candidates.Count)
                    {
                        await WriteStatusAsync(true, candidates.Count, done, hits, candidate.Name, null, startedUtc);
                    }
                }
                finally
                {
                    slots.Release();
                }
            }).ToArray();
            await Task.WhenAll(work);
        }
        finally
        {
            string summary = found == 0
                ? "Bu turda firmaların kendi sitelerinde yayımlanmış e-posta bulunamadı."
                : "Örnekler: " + string.Join("; ", examples) + (found > examples.Count ? " …" : string.Empty);
            await WriteStatusAsync(false, candidates.Count, processed, found, null, summary, startedUtc);
            TelegramOutbox.TryQueue(
                $"📧 CRM e-posta taraması bitti: {processed}/{candidates.Count} firma işlendi, {found} e-posta bulundu ve boş alanlara yazıldı.\n" +
                summary + "\nKimseye e-posta gönderilmedi. Değişiklik kaydı: CrmEmailFinder\\changes.jsonl");
        }
    }

    internal static async Task<CrmEmailFinding?> FindAsync(CrmEmailCandidate candidate, bool searchWeb, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(candidate.Website) && !IsDirectoryUrl(candidate.Website))
        {
            string site = candidate.Website.Trim();
            if (!site.StartsWith("http", StringComparison.OrdinalIgnoreCase)) site = "https://" + site;
            CrmEmailFinding? direct = await ScanSiteAsync(site, cancellationToken);
            if (direct is not null) return direct;
        }

        if (!searchWeb) return null;
        string query = $"{candidate.Name} {candidate.City} iletişim e-posta".Trim();
        List<(string Title, string Url, string Snippet)> results = await WebResearchService.SearchResultsAsync(query, cancellationToken);
        foreach ((string _, string url, string _) in results.Take(6))
        {
            if (IsDirectoryUrl(url) || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || !HostMatchesName(uri.Host, candidate.Name))
            {
                continue;
            }

            CrmEmailFinding? found = await ScanSiteAsync(uri.GetLeftPart(UriPartial.Authority), cancellationToken);
            if (found is not null)
            {
                return found with { DiscoveredWebsite = string.IsNullOrWhiteSpace(candidate.Website) ? uri.GetLeftPart(UriPartial.Authority) : null };
            }
        }

        return null;
    }

    private static async Task<CrmEmailFinding?> ScanSiteAsync(string site, CancellationToken cancellationToken)
    {
        WebResearchService.RawPage home = await WebResearchService.FetchHtmlAsync(site, cancellationToken);
        if (home.Error is not null || home.Uri is null) return null;
        string host = NormalizeHost(home.Uri.Host);
        if (IsDirectoryHost(host)) return null;

        string? email = PickEmail(ExtractEmails(home.Html), host);
        if (email is not null) return new CrmEmailFinding(email, home.Uri.AbsoluteUri, null);

        List<Uri> pages = ContactLinks(home.Html, home.Uri).Take(3).ToList();
        if (pages.Count == 0)
        {
            pages.Add(new Uri(home.Uri, "/iletisim"));
            pages.Add(new Uri(home.Uri, "/contact"));
        }

        foreach (Uri page in pages)
        {
            WebResearchService.RawPage contact = await WebResearchService.FetchHtmlAsync(page.AbsoluteUri, cancellationToken);
            if (contact.Error is not null || contact.Uri is null || NormalizeHost(contact.Uri.Host) != host) continue;
            email = PickEmail(ExtractEmails(contact.Html), host);
            if (email is not null) return new CrmEmailFinding(email, contact.Uri.AbsoluteUri, null);
        }

        if (WebResearchService.HtmlToText(home.Html).Length < 400)
        {
            string rendered = await WebResearchService.ReadWithJinaAsync(home.Uri.AbsoluteUri, 20_000, cancellationToken);
            email = PickEmail(ExtractEmails(rendered), host);
            if (email is not null) return new CrmEmailFinding(email, home.Uri.AbsoluteUri, null);
        }

        return null;
    }

    internal static IReadOnlyList<string> ExtractEmails(string html)
    {
        string decoded = WebUtility.HtmlDecode(html.Replace("&#64;", "@").Replace("%40", "@"));
        decoded = AtWordRegex().Replace(decoded, "@");
        HashSet<string> emails = new(StringComparer.OrdinalIgnoreCase);
        List<string> ordered = [];
        foreach (Match match in EmailRegex().Matches(decoded))
        {
            string email = match.Value.Trim('.', '-', '_').ToLowerInvariant();
            if (IsPlausibleEmail(email) && emails.Add(email)) ordered.Add(email);
        }

        return ordered;
    }

    internal static string? PickEmail(IReadOnlyList<string> emails, string siteHost)
    {
        string host = NormalizeHost(siteHost);
        string? own = emails.FirstOrDefault(email => DomainMatches(EmailDomain(email), host));
        if (own is not null) return own;
        return emails.FirstOrDefault(email => FreeMailDomains.Contains(EmailDomain(email)));
    }

    internal static bool IsPlausibleEmail(string email)
    {
        if (email.Length is < 6 or > 80) return false;
        string local = email[..email.IndexOf('@')];
        if (local.StartsWith('.') || local.EndsWith('.') || email.Contains("..", StringComparison.Ordinal)) return false;
        string domain = EmailDomain(email);
        if (domain.Length < 4 || !domain.Contains('.')) return false;
        string extension = domain[(domain.LastIndexOf('.') + 1)..];
        if (extension is "png" or "jpg" or "jpeg" or "gif" or "webp" or "svg" or "css" or "js") return false;
        return !IgnoredEmailParts.Any(part => email.Contains(part, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsDirectoryUrl(string url)
    {
        string text = url.Trim();
        if (!text.StartsWith("http", StringComparison.OrdinalIgnoreCase)) text = "https://" + text;
        return !Uri.TryCreate(text, UriKind.Absolute, out Uri? uri) || IsDirectoryHost(NormalizeHost(uri.Host));
    }

    private static bool IsDirectoryHost(string host) =>
        DirectoryHosts.Any(directory => host == directory || host.EndsWith("." + directory, StringComparison.Ordinal));

    private static readonly string[] GenericNameWords =
    [
        "ltd", "sti", "san", "tic", "limited", "sirketi", "anonim", "ticaret", "sanayi", "hastanesi", "hastane",
        "aile", "sagligi", "saglik", "merkezi", "merkez", "eczanesi", "eczane", "tip", "egitim", "arastirma", "bina",
        "dispanseri", "verem", "savas", "ozel", "devlet", "klinik", "klinigi", "poliklinik", "poliklinigi", "laboratuvar",
        "laboratuvari", "otomotiv", "servis", "servisi", "insaat", "gida", "market", "magaza", "firma", "grup", "group",
        "adana", "mersin", "hatay", "osmaniye", "gaziantep", "sanliurfa", "ceyhan", "kozan", "iskenderun", "seyhan",
        "cukurova", "yuregir", "saricam", "tarsus", "turkiye", "makina", "makinalari", "makinalar", "tarim", "demir"
    ];

    /// <summary>True when the host's own name carries the company's distinctive words (two of them when it has several).</summary>
    internal static bool HostMatchesName(string host, string companyName)
    {
        string normalized = NormalizeHost(host);
        if (normalized.Contains(".edu.", StringComparison.Ordinal) || normalized.EndsWith(".edu", StringComparison.Ordinal) ||
            normalized.Contains(".gov.", StringComparison.Ordinal) || normalized.Contains(".k12.", StringComparison.Ordinal) ||
            normalized.Contains(".bel.", StringComparison.Ordinal) || normalized.Contains(".blogspot.", StringComparison.Ordinal))
        {
            return false;
        }

        string label = WebResearchService.FoldTurkish(normalized.Split('.')[0]).Replace("-", string.Empty);
        string[] tokens = WebResearchService.FoldTurkish(companyName.ToLowerInvariant())
            .Split([' ', '.', ',', '-', '&', '/', '(', ')', '\''], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= 3 && !GenericNameWords.Contains(token))
            .Distinct()
            .ToArray();
        if (tokens.Length == 0) return false;
        int matched = tokens.Count(token => label.Contains(token, StringComparison.Ordinal));
        return tokens.Length == 1
            ? matched == 1 && tokens[0].Length >= 5 && label.Length <= tokens[0].Length + 12
            : matched >= 2;
    }

    private static IEnumerable<Uri> ContactLinks(string html, Uri baseUri)
    {
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in HrefRegex().Matches(html))
        {
            string href = WebUtility.HtmlDecode(match.Groups[1].Value).Trim();
            string folded = WebResearchService.FoldTurkish(href.ToLowerInvariant());
            if (!(folded.Contains("iletisim") || folded.Contains("contact") || folded.Contains("bize-ulas") || folded.Contains("hakkimizda")))
            {
                continue;
            }

            if (Uri.TryCreate(baseUri, href, out Uri? link) &&
                (link.Scheme == Uri.UriSchemeHttp || link.Scheme == Uri.UriSchemeHttps) &&
                NormalizeHost(link.Host) == NormalizeHost(baseUri.Host) &&
                seen.Add(link.GetLeftPart(UriPartial.Path)))
            {
                yield return link;
            }
        }
    }

    private static string EmailDomain(string email) => email[(email.IndexOf('@') + 1)..].ToLowerInvariant();

    private static string NormalizeHost(string host)
    {
        string lower = host.Trim().TrimEnd('.').ToLowerInvariant();
        return lower.StartsWith("www.", StringComparison.Ordinal) ? lower[4..] : lower;
    }

    private static bool DomainMatches(string emailDomain, string host) =>
        emailDomain == host || host.EndsWith("." + emailDomain, StringComparison.Ordinal) ||
        emailDomain.EndsWith("." + host, StringComparison.Ordinal);

    private static async Task<IReadOnlyList<CrmEmailCandidate>> LoadCandidatesAsync(
        bool searchWeb,
        string? city,
        Dictionary<int, DateTime> attempts,
        int limit,
        CancellationToken cancellationToken)
    {
        List<CrmEmailCandidate> withSite = [];
        List<CrmEmailCandidate> withoutSite = [];
        await using DbConnection connection = await OpenAsync(readOnly: true, cancellationToken);
        await using DbCommand command = ArsanDatabase.Command(connection, null, $"""
            SELECT Id, CompanyName, City, Website FROM CrmCompanies
            WHERE COALESCE(trim(Email), '') = '' AND ($city = '' OR City {ArsanDatabase.Like} $city)
            ORDER BY Score DESC, Id
            """, ("$city", string.IsNullOrWhiteSpace(city) ? string.Empty : city.Trim()));
        await using DbDataReader reader = await command.ExecuteReaderAsync(cancellationToken);
        DateTime cutoff = DateTime.UtcNow - RetryAfter;
        while (await reader.ReadAsync(cancellationToken))
        {
            int id = reader.GetInt32(0);
            if (attempts.TryGetValue(id, out DateTime last) && last > cutoff) continue;
            CrmEmailCandidate candidate = new(id, reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3));
            if (!string.IsNullOrWhiteSpace(candidate.Website) && !IsDirectoryUrl(candidate.Website)) withSite.Add(candidate);
            else if (searchWeb) withoutSite.Add(candidate);
        }

        return withSite.Concat(withoutSite).Take(limit).ToList();
    }

    private static async Task<bool> SaveAsync(CrmEmailCandidate candidate, CrmEmailFinding finding, CancellationToken cancellationToken)
    {
        await using DbConnection connection = await OpenAsync(readOnly: false, cancellationToken);
        await using DbCommand command = ArsanDatabase.Command(connection, null, """
            UPDATE CrmCompanies
            SET Email = $email,
                Website = CASE WHEN COALESCE(trim(Website), '') = '' AND CAST($website AS TEXT) IS NOT NULL THEN CAST($website AS TEXT) ELSE Website END
            WHERE Id = $id AND COALESCE(trim(Email), '') = ''
            """, ("$email", finding.Email), ("$website", finding.DiscoveredWebsite), ("$id", candidate.Id));
        bool saved = await command.ExecuteNonQueryAsync(cancellationToken) == 1;
        if (saved)
        {
            string line = JsonSerializer.Serialize(new
            {
                candidate.Id,
                Company = candidate.Name,
                finding.Email,
                finding.SourceUrl,
                WebsiteAdded = finding.DiscoveredWebsite,
                Utc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            });
            await StateGate.WaitAsync(cancellationToken);
            try
            {
                Directory.CreateDirectory(StateDirectory);
                await File.AppendAllTextAsync(ChangesPath, line + Environment.NewLine, Utf8, cancellationToken);
            }
            finally
            {
                StateGate.Release();
            }
        }

        return saved;
    }

    private static Task<DbConnection> OpenAsync(bool readOnly, CancellationToken cancellationToken) =>
        ArsanDatabase.OpenAsync(DatabaseKind.Crm, readOnly, cancellationToken);

    private static async Task<Dictionary<int, DateTime>> ReadAttemptsAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(AttemptsPath)) return [];
        try
        {
            return JsonSerializer.Deserialize<Dictionary<int, DateTime>>(
                await File.ReadAllTextAsync(AttemptsPath, Utf8, cancellationToken)) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task RecordAttemptsAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        await StateGate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(StateDirectory);
            Dictionary<int, DateTime> attempts = await ReadAttemptsAsync(cancellationToken);
            foreach (int id in ids) attempts[id] = DateTime.UtcNow;
            await File.WriteAllTextAsync(AttemptsPath + ".tmp", JsonSerializer.Serialize(attempts), Utf8, cancellationToken);
            File.Move(AttemptsPath + ".tmp", AttemptsPath, overwrite: true);
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static async Task WriteStatusAsync(
        bool active, int total, int processed, int found, string? lastCompany, string? summary, DateTime startedUtc)
    {
        await StateGate.WaitAsync();
        try
        {
            Directory.CreateDirectory(StateDirectory);
            string json = JsonSerializer.Serialize(new
            {
                Running = active,
                Total = total,
                Processed = processed,
                Found = found,
                LastCompany = lastCompany,
                Summary = summary,
                StartedUtc = startedUtc.ToString("o", CultureInfo.InvariantCulture),
                UpdatedUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)
            });
            await File.WriteAllTextAsync(StatusPath + ".tmp", json, Utf8);
            File.Move(StatusPath + ".tmp", StatusPath, overwrite: true);
        }
        finally
        {
            StateGate.Release();
        }
    }

    private static void AppendLog(string line)
    {
        try
        {
            Directory.CreateDirectory(StateDirectory);
            File.AppendAllText(Path.Combine(StateDirectory, "errors.log"),
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}", Utf8);
        }
        catch (IOException)
        {
        }
    }

    [GeneratedRegex(@"[A-Za-z0-9][A-Za-z0-9._%+\-]{0,63}@[A-Za-z0-9](?:[A-Za-z0-9\-]{0,62}[A-Za-z0-9])?(?:\.[A-Za-z0-9](?:[A-Za-z0-9\-]{0,62}[A-Za-z0-9])?)*\.[A-Za-z]{2,24}")]
    private static partial Regex EmailRegex();

    [GeneratedRegex(@"\s*[\[\(]\s*(?:at|et)\s*[\]\)]\s*", RegexOptions.IgnoreCase)]
    private static partial Regex AtWordRegex();

    [GeneratedRegex("href\\s*=\\s*[\"']([^\"'#]+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex HrefRegex();
}
