using System.Globalization;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using ArsanGazERP.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.MobileApi;

internal sealed record CampaignGroupFilter(string? Name, string? City, string? Sector, string? Status, string? Search, string? Require);
internal sealed record CampaignPrepareRequest(
    Guid GroupId,
    string Channel,
    string Name,
    string? Subject,
    string? Content,
    List<string>? Attachments,
    DateTimeOffset? StartsAt,
    string? WindowStart,
    string? WindowEnd,
    int? SpacingSeconds,
    int? RepeatEveryDays,
    int? DurationDays,
    string? TemplateName,
    string? TemplateLanguage,
    List<string>? TemplateParameters);
internal sealed record CampaignLaunchRequest(long Revision, string ManifestHash, bool Confirm);
internal sealed record CampaignRevisionRequest(long Revision);
internal sealed record CampaignContactRequest(int ContactId, string? Channel, string? Note);

internal static class MobileCampaignEndpoints
{
    internal const string B2BConsentEvidence =
        "Tacir/esnaf (B2B) tanıtım iletisi: 6563 s. Kanun ve Ticari İletişim Yönetmeliği md.7/2 kapsamında ön onay istisnası; " +
        "her iletide ret (RET) hakkı bildirilir. Kaydı mobil uygulamada kullanıcı onayladı.";
    internal const string WhatsAppOptOutLine = "Bu tür mesajları almak istemiyorsanız RET yazmanız yeterli.";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly Lazy<CampaignService> Service = new(() =>
        new CampaignService(CampaignStore.CreateForCurrentWindowsUser(), CampaignTransport.CreateWithoutMicrosoft()));
    private static readonly GmailSenderService Gmail = new();
    private static string UploadRoot => Path.Combine(MobileAttachmentStore.Root, "kampanya");

    public static void Map(WebApplication app)
    {
        // Campaign rows belong to a Windows user SID; on Linux the owner must be configured explicitly.
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("ARSANGAZ_OWNER_SID") is not { Length: > 0 })
        {
            app.Use(async (context, next) =>
            {
                if (context.Request.Path.StartsWithSegments("/api/kampanya"))
                {
                    context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                    await context.Response.WriteAsJsonAsync(new { error = "Kampanyalar bu sunucuda henüz etkin değil (ARSANGAZ_OWNER_SID ayarlanmamış)." });
                    return;
                }

                await next(context);
            });
        }

        app.MapGet("/api/kampanya/durum", async (CancellationToken ct) =>
        {
            WhatsAppBusinessService whatsApp = new();
            CampaignService service = Service.Value;
            bool execution = await Guard(() => service.RefreshExecutionStateAsync(ct), false);
            return Results.Ok(new
            {
                email = Gmail.GetSenderOption()?.DisplayName,
                emailIdentity = Gmail.ConfiguredIdentity(),
                emailSetupHint = Gmail.IsConfigured ? null : "Bilgisayarda Tools\\Set-GmailSender.ps1 çalıştırılıp Google Workspace uygulama şifresi girilmeli.",
                whatsAppConfigured = whatsApp.IsConfigured,
                whatsAppHint = "Otomatik WhatsApp için Meta'da onaylı, PDF başlıklı bir pazarlama şablonu gerekir. Şablon onaylanana kadar yarı otomatik WhatsApp kullanın.",
                executionEnabled = execution,
                maxEmailAttachmentKb = CampaignStore.MaximumEmailAttachmentBytes / 1024,
                optOut = WhatsAppOptOutLine
            });
        }).Requires(PanelPermissions.CampaignView);

        app.MapGet("/api/kampanya/filtreler", async () =>
        {
            await using CrmDbContext db = new();
            List<CrmCompany> companies = await db.CrmCompanies.AsNoTracking().ToListAsync();
            return Results.Ok(new
            {
                total = companies.Count,
                withEmail = companies.Count(c => HasEmail(c.Email)),
                withMobile = companies.Count(c => MobilePhone(c.Phone) is not null),
                cities = TopValues(companies.Select(c => c.City)),
                sectors = TopValues(companies.Select(c => c.Sector)),
                statuses = TopValues(companies.Select(c => c.Status))
            });
        }).Requires(PanelPermissions.CampaignView);

        app.MapPost("/api/kampanya/grup/onizle", async (CampaignGroupFilter filter) =>
        {
            List<CrmCompany> matches = await MatchAsync(filter);
            return Results.Ok(new
            {
                count = matches.Count,
                withEmail = matches.Count(c => HasEmail(c.Email)),
                withMobile = matches.Count(c => MobilePhone(c.Phone) is not null),
                sample = matches.Take(8).Select(c => c.CompanyName)
            });
        }).Requires(PanelPermissions.CampaignView);

        app.MapPost("/api/kampanya/grup", async (CampaignGroupFilter filter, CancellationToken ct) =>
        {
            string name = (filter.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > 80)
                return Results.BadRequest(new { error = "Grup adı 1-80 karakter olmalıdır." });
            List<CrmCompany> matches = await MatchAsync(filter);
            if (matches.Count == 0)
                return Results.BadRequest(new { error = "Bu filtreye uyan firma yok." });
            if (matches.Count > CampaignStore.MaximumAudienceSize)
                return Results.BadRequest(new { error = $"Grup en fazla {CampaignStore.MaximumAudienceSize} firma olabilir; filtreyi daraltın ({matches.Count} eşleşme)." });
            return await Guard(async () =>
            {
                CampaignGroup group = await Service.Value.SaveGroupAsync(null, name, CampaignPopulation.CrmCompanies,
                    matches.Select(c => c.Id).ToArray(), ct);
                return Results.Ok(new { id = group.Id, name = group.Name, count = group.ContactIds.Count });
            });
        }).Requires(PanelPermissions.CampaignManage);

        app.MapGet("/api/kampanya/gruplar", async (CancellationToken ct) => await Guard(async () =>
        {
            IReadOnlyList<CampaignGroup> groups = await Service.Value.ListGroupsAsync(ct);
            return Results.Ok(groups
                .Where(g => g.Population == CampaignPopulation.CrmCompanies)
                .OrderByDescending(g => g.UpdatedAtUtc)
                .Select(g => new { id = g.Id, name = g.Name, count = g.ContactIds.Count, updated = g.UpdatedAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish) }));
        })).Requires(PanelPermissions.CampaignView);

        app.MapDelete("/api/kampanya/grup/{id:guid}", async (Guid id, CancellationToken ct) => await Guard(async () =>
        {
            await Service.Value.DeleteGroupAsync(id, ct);
            return Results.Ok(new { message = "Grup silindi (firmalar CRM'de kalır)." });
        })).Requires(PanelPermissions.CampaignManage);

        app.MapGet("/api/kampanya/grup/{id:guid}/whatsapp", async (Guid id, CancellationToken ct) => await Guard(async () =>
        {
            CampaignService service = Service.Value;
            IReadOnlyList<CampaignContactOption> members = await service.GetGroupMembersAsync(id, ct);
            HashSet<int> optedOut = await OptedOutAsync(service, CampaignChannel.WhatsApp, ct);
            var list = members
                .Where(m => m.IsActive && !optedOut.Contains(m.ContactId))
                .Select(m => new { id = m.ContactId, name = m.DisplayName, phone = MobilePhone(m.Phone) })
                .Where(m => m.phone is not null)
                .ToList();
            return Results.Ok(new
            {
                contacts = list,
                skippedOptOut = members.Count(m => optedOut.Contains(m.ContactId)),
                skippedNoMobile = members.Count(m => !optedOut.Contains(m.ContactId) && MobilePhone(m.Phone) is null)
            });
        })).Requires(PanelPermissions.CampaignWhatsApp);

        app.MapPost("/api/kampanya/whatsapp-gonderildi", async (CampaignContactRequest request, HttpContext context) =>
        {
            await using CrmDbContext db = new();
            CrmCompany? company = await db.CrmCompanies.FindAsync(request.ContactId);
            if (company is null)
                return Results.NotFound(new { error = "Firma bulunamadı." });
            string note = string.IsNullOrWhiteSpace(request.Note) ? string.Empty : " " + request.Note.Trim()[..Math.Min(request.Note.Trim().Length, 200)];
            db.CrmActivities.Add(new CrmActivity
            {
                CrmCompanyId = company.Id,
                ActivityType = "WhatsApp tanıtım",
                Description = $"Yarı otomatik WhatsApp tanıtımı gönderildi ({DeviceName(context)}).{note}"
            });
            company.LastContactAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync();
            return Results.Ok(new { message = "Kaydedildi." });
        }).Requires(PanelPermissions.CampaignWhatsApp);

        app.MapPost("/api/kampanya/ret", async (CampaignContactRequest request, HttpContext context, CancellationToken ct) =>
        {
            CampaignChannel[] channels = ParseChannel(request.Channel) is { } single ? [single] : [CampaignChannel.Email, CampaignChannel.WhatsApp];
            CampaignService service = Service.Value;
            CrmCompany? target;
            await using (CrmDbContext db = new())
                target = await db.CrmCompanies.AsNoTracking().FirstOrDefaultAsync(c => c.Id == request.ContactId, ct);
            if (target is null)
                return Results.NotFound(new { error = "Firma bulunamadı." });
            return await Guard(async () =>
            {
                List<string> done = [];
                foreach (CampaignChannel channel in channels)
                {
                    string endpoint = (channel == CampaignChannel.Email ? target.Email : target.Phone) ?? string.Empty;
                    if (string.IsNullOrEmpty(CampaignStore.NormalizeEndpoint(channel, endpoint)))
                        continue;
                    await service.RecordConsentAsync(CampaignPopulation.CrmCompanies, target.Id, channel, endpoint, false,
                        $"Firma RET bildirdi; mobil uygulamadan ({DeviceName(context)}) kaydedildi.", ct);
                    done.Add(channel == CampaignChannel.Email ? "e-posta" : "WhatsApp");
                }
                return done.Count == 0
                    ? Results.BadRequest(new { error = "Firmanın kayıtlı e-posta/telefonu yok." })
                    : Results.Ok(new { message = $"{target.CompanyName}: {string.Join(" ve ", done)} listesinden çıkarıldı." });
            });
        }).Requires(PanelPermissions.CampaignWhatsApp);

        app.MapPost("/api/kampanya/ek", async (HttpContext context) =>
        {
            if (!context.Request.HasFormContentType)
                return Results.BadRequest(new { error = "PDF dosyası bekleniyor." });
            IFormCollection form = await context.Request.ReadFormAsync(context.RequestAborted);
            IFormFile? file = form.Files.FirstOrDefault();
            if (file is null || !string.Equals(Path.GetExtension(file.FileName), ".pdf", StringComparison.OrdinalIgnoreCase))
                return Results.BadRequest(new { error = "Yalnızca PDF eklenebilir." });
            if (file.Length is 0 or > MobileAttachmentStore.MaxFileBytes)
                return Results.BadRequest(new { error = "PDF en fazla 15 MB olabilir." });
            string folder = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N")[..6];
            string directory = Path.Combine(UploadRoot, folder);
            Directory.CreateDirectory(directory);
            string safeName = string.Concat(Path.GetFileNameWithoutExtension(file.FileName)
                .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_')).Trim();
            if (safeName.Length == 0)
                safeName = "tanitim";
            safeName = safeName[..Math.Min(safeName.Length, 60)] + ".pdf";
            await using (FileStream stream = new(Path.Combine(directory, safeName), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                await file.CopyToAsync(stream, context.RequestAborted);
            return Results.Ok(new
            {
                token = folder + "/" + safeName,
                name = safeName,
                sizeKb = file.Length / 1024,
                emailOk = file.Length <= CampaignStore.MaximumEmailAttachmentBytes
            });
        }).Requires(PanelPermissions.CampaignManage);

        app.MapPost("/api/kampanya/hazirla", async (CampaignPrepareRequest request, CancellationToken ct) => await Guard(async () =>
        {
            CampaignChannel channel = ParseChannel(request.Channel)
                ?? throw new ArgumentException("Kanal 'email' veya 'whatsapp' olmalıdır.");
            string name = (request.Name ?? string.Empty).Trim();
            if (name.Length is 0 or > 120)
                throw new ArgumentException("Kampanya adı 1-120 karakter olmalıdır.");
            List<string> attachments = (request.Attachments ?? []).Select(ResolveUpload).ToList();
            TimeOnly windowStart = ParseTime(request.WindowStart, new TimeOnly(9, 0));
            TimeOnly windowEnd = ParseTime(request.WindowEnd, new TimeOnly(18, 0));
            DateTimeOffset startsAt = request.StartsAt?.ToUniversalTime() ?? DateTimeOffset.UtcNow.AddMinutes(2);
            if (startsAt < DateTimeOffset.UtcNow.AddMinutes(1))
                startsAt = DateTimeOffset.UtcNow.AddMinutes(1);
            DateTimeOffset? endsAt = request.DurationDays is > 0 ? startsAt.AddDays(Math.Min(request.DurationDays.Value, 365)) : null;
            int spacing = Math.Clamp(request.SpacingSeconds ?? (channel == CampaignChannel.Email ? 90 : 60), 30, 86_400);

            CampaignService service = Service.Value;
            string sender;
            WhatsAppTemplateApproval? template = null;
            List<WhatsAppTemplateComponent> components = [];
            string subject = string.Empty, content = string.Empty;
            if (channel == CampaignChannel.Email)
            {
                sender = Gmail.ConfiguredIdentity()
                    ?? throw new InvalidOperationException("Gmail gönderici ayarlı değil. Bilgisayarda Tools\\Set-GmailSender.ps1 çalıştırılmalı.");
                subject = (request.Subject ?? string.Empty).Trim();
                if (subject.Length is 0 or > 200)
                    throw new ArgumentException("Konu 1-200 karakter olmalıdır.");
                content = GmailSenderService.WithOptOutFooter(request.Content ?? string.Empty, sender[GmailSenderService.IdentityPrefix.Length..]);
                if (string.IsNullOrWhiteSpace(request.Content))
                    throw new ArgumentException("E-posta metni boş olamaz.");
            }
            else
            {
                IReadOnlyList<CampaignSenderOption> senders = await service.GetAvailableSendersAsync(CampaignChannel.WhatsApp, ct);
                sender = senders.FirstOrDefault()?.Identity
                    ?? throw new InvalidOperationException("WhatsApp Business ayarlı değil.");
                if (string.IsNullOrWhiteSpace(request.TemplateName))
                    throw new ArgumentException("Otomatik WhatsApp için Meta onaylı şablon adı gerekir. Şablon yoksa yarı otomatik WhatsApp'ı kullanın.");
                template = await service.VerifyWhatsAppTemplateAsync(request.TemplateName.Trim(), (request.TemplateLanguage ?? "tr").Trim(), ct);
                if (request.TemplateParameters is { Count: > 0 } parameters)
                    components.Add(new WhatsAppTemplateComponent("body", parameters));
            }

            ConsentBatchResult consent = await service.RecordGroupConsentAsync(request.GroupId, channel, true, B2BConsentEvidence, ct);
            GroupCampaignPreparation prepared = await service.PrepareGroupCampaignAsync(new GroupCampaignRequest(
                request.GroupId, name, channel, sender, subject, content, template, components, attachments,
                TimeZoneInfo.Local.Id, startsAt, endsAt, [new CampaignDailyWindow(windowStart, windowEnd)],
                TimeSpan.FromSeconds(spacing), Math.Clamp(request.RepeatEveryDays ?? 0, 0, CampaignStore.MaximumRepeatEveryDays)), ct);

            int eligible = prepared.Summary.EligibleCount;
            double windowHours = (windowEnd > windowStart ? windowEnd - windowStart : TimeSpan.FromHours(24) - (windowStart - windowEnd)).TotalHours;
            int perDay = Math.Max(1, (int)(windowHours * 3600 / spacing));
            return Results.Ok(new
            {
                campaignId = prepared.Draft.Id,
                revision = prepared.Draft.Revision,
                manifestHash = prepared.Summary.ApprovalManifestHash,
                groupSize = prepared.Summary.GroupSize,
                eligible,
                exclusions = CampaignService.DescribeExclusions(prepared.Summary.Exclusions),
                optedOutSkipped = consent.SkippedWithdrawn,
                attachments = prepared.Summary.AttachmentNames,
                sender = channel == CampaignChannel.Email ? sender[GmailSenderService.IdentityPrefix.Length..] : "WhatsApp Business",
                startsAt = startsAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish),
                window = $"{windowStart:HH\\:mm}-{windowEnd:HH\\:mm}",
                spacingSeconds = spacing,
                estimatedDays = (int)Math.Ceiling(eligible / (double)perDay),
                preview = channel == CampaignChannel.Email ? content : $"Şablon: {template?.Name}"
            });
        })).Requires(PanelPermissions.CampaignManage);

        app.MapPost("/api/kampanya/{id:guid}/baslat", async (Guid id, CampaignLaunchRequest request, HttpContext context, CancellationToken ct) => await Guard(async () =>
        {
            if (!request.Confirm)
                throw new ArgumentException("Gönderim için açık onay gerekiyor.");
            CampaignSnapshot launched = await Service.Value.LaunchGroupCampaignAsync(id, request.Revision, request.ManifestHash, ct);
            TelegramOutbox.TryQueue($"📣 Kampanya onaylandı ({DeviceName(context)}): {launched.Input.Name}\n" +
                $"Kanal: {(launched.Input.Channel == CampaignChannel.Email ? "E-posta (Gmail)" : "WhatsApp")}, " +
                $"başlangıç {launched.Input.StartsAtUtc.ToLocalTime():dd.MM.yyyy HH:mm}, durum {StateName(launched.State)}.");
            return Results.Ok(new { message = $"Kampanya {StateName(launched.State).ToLowerInvariant()}.", state = launched.State.ToString(), revision = launched.Revision });
        })).Requires(PanelPermissions.CampaignManage);

        app.MapGet("/api/kampanya/liste", async (CancellationToken ct) => await Guard(async () =>
        {
            CampaignService service = Service.Value;
            CampaignPage page = await service.ListAsync(new CampaignQuery(0, 30), ct);
            List<object> items = [];
            foreach (CampaignSnapshot campaign in page.Items.OrderByDescending(c => c.UpdatedAtUtc))
            {
                RecipientPage recipients = await service.GetRecipientsAsync(campaign.Id, 0, CampaignStore.MaximumAudienceSize, ct);
                items.Add(new
                {
                    id = campaign.Id,
                    name = campaign.Input.Name,
                    channel = campaign.Input.Channel == CampaignChannel.Email ? "E-posta" : "WhatsApp",
                    state = StateName(campaign.State),
                    stateCode = campaign.State.ToString(),
                    revision = campaign.Revision,
                    startsAt = campaign.Input.StartsAtUtc.ToLocalTime().ToString("dd.MM.yyyy HH:mm", Turkish),
                    total = recipients.TotalCount,
                    sent = recipients.Items.Count(r => r.State == CampaignRecipientState.Accepted),
                    failed = recipients.Items.Count(r => r.State is CampaignRecipientState.Rejected or CampaignRecipientState.Unknown),
                    pending = recipients.Items.Count(r => r.State is CampaignRecipientState.Pending or CampaignRecipientState.Claimed or CampaignRecipientState.Sending),
                    error = campaign.LastErrorCode
                });
            }
            return Results.Ok(items);
        })).Requires(PanelPermissions.CampaignView);

        app.MapPost("/api/kampanya/{id:guid}/duraklat", async (Guid id, CampaignRevisionRequest request, CancellationToken ct) => await Guard(async () =>
        {
            CampaignSnapshot paused = await Service.Value.PauseAsync(id, request.Revision, ct);
            return Results.Ok(new { message = "Kampanya duraklatıldı.", revision = paused.Revision });
        })).Requires(PanelPermissions.CampaignManage);

        app.MapPost("/api/kampanya/{id:guid}/devam", async (Guid id, CampaignRevisionRequest request, CancellationToken ct) => await Guard(async () =>
        {
            CampaignSnapshot resumed = await Service.Value.ResumeAsync(id, request.Revision, new ActivationAcknowledgement(true, true, true), ct);
            return Results.Ok(new { message = "Kampanya devam ediyor.", revision = resumed.Revision });
        })).Requires(PanelPermissions.CampaignManage);

        app.MapPost("/api/kampanya/{id:guid}/iptal", async (Guid id, CampaignRevisionRequest request, CancellationToken ct) => await Guard(async () =>
        {
            CampaignSnapshot cancelled = await Service.Value.CancelAsync(id, request.Revision, ct);
            return Results.Ok(new { message = "Kampanya iptal edildi.", revision = cancelled.Revision });
        })).Requires(PanelPermissions.CampaignManage);
    }

    private static async Task<List<CrmCompany>> MatchAsync(CampaignGroupFilter filter)
    {
        await using CrmDbContext db = new();
        List<CrmCompany> companies = await db.CrmCompanies.AsNoTracking().ToListAsync();
        string require = (filter.Require ?? string.Empty).Trim().ToLowerInvariant();
        return companies
            .Where(c => Equal(c.City, filter.City) && Equal(c.Sector, filter.Sector) && Equal(c.Status, filter.Status))
            .Where(c => string.IsNullOrWhiteSpace(filter.Search) ||
                        Contains(c.CompanyName, filter.Search.Trim()) || Contains(c.Sector, filter.Search.Trim()) || Contains(c.City, filter.Search.Trim()))
            .Where(c => require switch
            {
                "email" => HasEmail(c.Email),
                "whatsapp" or "phone" => MobilePhone(c.Phone) is not null,
                _ => true
            })
            .OrderByDescending(c => c.Score).ThenBy(c => c.CompanyName)
            .ToList();
    }

    private static async Task<HashSet<int>> OptedOutAsync(CampaignService service, CampaignChannel channel, CancellationToken ct) =>
        (await service.GetConsentLedgerAsync(CampaignPopulation.CrmCompanies, null, ct))
            .Where(item => item.Channel == channel)
            .GroupBy(item => item.ContactId)
            .Where(items => items.OrderBy(item => item.RecordedAtUtc).Last().IsWithdrawal)
            .Select(items => items.Key)
            .ToHashSet();

    private static string ResolveUpload(string token)
    {
        string root = Path.GetFullPath(UploadRoot) + Path.DirectorySeparatorChar;
        string full = Path.GetFullPath(Path.Combine(UploadRoot, (token ?? string.Empty).Replace('/', Path.DirectorySeparatorChar)));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
            throw new ArgumentException("Yüklenen PDF bulunamadı; dosyayı yeniden seçin.");
        return full;
    }

    private static async Task<IResult> Guard(Func<Task<IResult>> action)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or KeyNotFoundException or InvalidDataException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }

    private static async Task<T> Guard<T>(Func<Task<T>> action, T fallback)
    {
        try
        {
            return await action();
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException)
        {
            return fallback;
        }
    }

    private static CampaignChannel? ParseChannel(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "email" or "e-posta" or "eposta" or "mail" => CampaignChannel.Email,
        "whatsapp" => CampaignChannel.WhatsApp,
        _ => null
    };

    private static TimeOnly ParseTime(string? value, TimeOnly fallback) =>
        TimeOnly.TryParseExact(value ?? string.Empty, ["HH:mm", "H:mm"], CultureInfo.InvariantCulture, DateTimeStyles.None, out TimeOnly parsed)
            ? parsed
            : fallback;

    private static string StateName(CampaignState state) => state switch
    {
        CampaignState.Draft => "Taslak",
        CampaignState.Approved => "Onaylandı",
        CampaignState.Scheduled => "Zamanlandı",
        CampaignState.Active => "Gönderiliyor",
        CampaignState.Paused => "Duraklatıldı",
        CampaignState.CancelRequested => "İptal ediliyor",
        CampaignState.Cancelled => "İptal edildi",
        CampaignState.Completed => "Tamamlandı",
        CampaignState.Faulted => "Hata",
        _ => state.ToString()
    };

    internal static string? MobilePhone(string? phone)
    {
        string normalized = CampaignStore.NormalizeEndpoint(CampaignChannel.WhatsApp, phone ?? string.Empty);
        return normalized.StartsWith("905", StringComparison.Ordinal) && normalized.Length == 12 ? normalized : null;
    }

    private static bool HasEmail(string? email) =>
        !string.IsNullOrEmpty(CampaignStore.NormalizeEndpoint(CampaignChannel.Email, email ?? string.Empty));

    private static IEnumerable<object> TopValues(IEnumerable<string?> values) => values
        .Where(v => !string.IsNullOrWhiteSpace(v))
        .GroupBy(v => v!.Trim(), StringComparer.Create(Turkish, true))
        .OrderByDescending(g => g.Count())
        .Take(40)
        .Select(g => new { name = g.Key, count = g.Count() });

    private static bool Equal(string? value, string? wanted) =>
        string.IsNullOrWhiteSpace(wanted) || string.Equals(value?.Trim(), wanted.Trim(), StringComparison.CurrentCultureIgnoreCase) ||
        Turkish.CompareInfo.Compare(value?.Trim(), wanted.Trim(), CompareOptions.IgnoreCase) == 0;

    private static bool Contains(string? value, string term) =>
        value is not null && Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;

    private static string DeviceName(HttpContext context) =>
        context.Items["device"] is PairedDevice device ? device.Name : "bilinmeyen cihaz";
}
