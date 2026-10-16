using System.Text;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record LabelSequenceRequest(string? Prefix, long Start, int Count, int Digits);
internal sealed record LabelPrintedRequest(int Labels, List<string>? Codes, string? Size, string? Mode = null);
internal sealed record LabelTemplateCodesRequest(string? Serial, int? Count);
internal sealed record LabelTsplItem(string? Code, string? Serial, string? Gas, string? Stt, string? Gtt, string? Owner, string? Extra);
internal sealed record LabelTsplRequest(
    List<LabelTsplItem>? Items,
    int? WidthMm, int? HeightMm, int? Copies, string? Title,
    bool? ShowGas, bool? ShowSerial, bool? ShowTest, bool? ShowCodeText,
    int? GapMm, string? Printer, bool? Send);

// Barkod Yazdır (panel): preferred path is TSPL2 so the TSC TA310 builds QRCODE itself;
// browser SVG print remains a fallback. Server also builds/downloads TSPL and can RAW-print on Windows.
internal static class LabelEndpoints
{
    public static void MapPanel(RouteGroupBuilder api)
    {
        api.MapGet("/etiket/sonraki", async (string? onek) =>
        {
            try
            {
                string prefix = LabelCodes.NormalizePrefix(onek);
                await using TupTakipDbContext db = new();
                List<string> used = prefix.Length == 0
                    ? await db.Cylinders.AsNoTracking().Select(c => c.Barcode).ToListAsync()
                    : await db.Cylinders.AsNoTracking().Where(c => c.Barcode.ToUpper().StartsWith(prefix) || c.SerialNumber.ToUpper().StartsWith(prefix))
                        .Select(c => c.Barcode + "\n" + c.SerialNumber).ToListAsync();
                return Results.Ok(new { next = LabelCodes.NextNumber(prefix, used.SelectMany(x => x.Split('\n'))) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.LabelPrint);

        // Builds PREFIX+number codes and marks the ones that already belong to a cylinder (as barcode or serial number).
        api.MapPost("/etiket/olustur", async (LabelSequenceRequest request) =>
        {
            try
            {
                return Results.Ok(new { codes = await CodesAsync(request.Prefix, request.Start, request.Count, request.Digits) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.LabelPrint);

        api.MapGet("/etiket/urunler", async () => Results.Ok((await LabelTemplates.ListAsync()).Select(Json))).Requires(Perm.LabelPrint);

        api.MapPost("/etiket/urunler", async (LabelTemplateInput request, HttpContext context) =>
        {
            string user = (string)context.Items["panelUser"]!;
            try
            {
                LabelTemplate saved = await LabelTemplates.SaveAsync(request, user);
                WebPanelAudit.Write(user, request.Id is null ? "Barkod ürünü kaydedildi" : "Barkod ürünü güncellendi", saved.Name,
                    $"{saved.Prefix}{new string('0', saved.Digits)} · {saved.GasText}", WebPanelAuth.ClientIp(context));
                return Results.Ok(Json(saved));
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
            catch (DbUpdateException)
            {
                return Results.BadRequest(new { error = "Bu adda bir ürün az önce kaydedildi; listeyi yenileyip başka bir ad verin." });
            }
        }).Requires(Perm.LabelPrint);

        api.MapPost("/etiket/urunler/{id:int}/sil", async (int id, HttpContext context) =>
        {
            string? name = await LabelTemplates.DeleteAsync(id);
            if (name is null)
            {
                return Results.NotFound(new { error = "Ürün bulunamadı; listeyi yenileyin." });
            }

            WebPanelAudit.Write((string)context.Items["panelUser"]!, "Barkod ürünü silindi", name, null, WebPanelAuth.ClientIp(context));
            return Results.Ok(new { message = $"{name} silindi." });
        }).Requires(Perm.LabelPrint);

        // New label(s) from a saved product: only the serial number changes, prefix/digits come from the product.
        api.MapPost("/etiket/urunler/{id:int}/olustur", async (int id, LabelTemplateCodesRequest request) =>
        {
            LabelTemplate? template = await LabelTemplates.GetAsync(id);
            if (template is null)
            {
                return Results.NotFound(new { error = "Ürün bulunamadı; listeyi yenileyin." });
            }

            try
            {
                long start = LabelTemplates.ParseSerial(template, request.Serial);
                return Results.Ok(new { template = Json(template), codes = await CodesAsync(template.Prefix, start, request.Count ?? 1, template.Digits) });
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.LabelPrint);

        api.MapPost("/etiket/yazdirildi", (LabelPrintedRequest request, HttpContext context) =>
        {
            List<string> codes = request.Codes ?? [];
            string sample = string.Join(", ", codes.Take(20)) + (codes.Count > 20 ? $" … (+{codes.Count - 20})" : string.Empty);
            string mode = string.IsNullOrWhiteSpace(request.Mode) ? "tarayıcı" : request.Mode.Trim();
            WebPanelAudit.Write((string)context.Items["panelUser"]!, "Barkod etiketi yazdırmaya gönderildi", $"{Math.Clamp(request.Labels, 0, 100000)} etiket",
                mode + " · " + (string.IsNullOrWhiteSpace(request.Size) ? string.Empty : request.Size.Trim()[..Math.Min(request.Size.Trim().Length, 40)] + " · ") + sample,
                WebPanelAuth.ClientIp(context));
            return Results.Ok(new { ok = true });
        }).Requires(Perm.LabelPrint);

        // TSPL2 job: printer draws QRCODE natively (not a browser bitmap). Optional RAW send when API is on Windows.
        api.MapPost("/etiket/tspl", (LabelTsplRequest request, HttpContext context) =>
        {
            try
            {
                string tspl = BuildTspl(request);
                bool send = request.Send == true;
                if (send)
                {
                    string printer = string.IsNullOrWhiteSpace(request.Printer) ? "TSC TA310" : request.Printer.Trim();
                    (bool ok, string? error) = RawPrinter.SendUtf8(printer, tspl);
                    if (!ok)
                    {
                        return Results.BadRequest(new { error, tspl, windows = RawPrinter.IsWindows, printers = RawPrinter.ListPrinterNames() });
                    }

                    WebPanelAudit.Write((string)context.Items["panelUser"]!, "Barkod etiketi TSPL ile yazıcıya gönderildi",
                        printer, $"{CountLabels(request)} etiket", WebPanelAuth.ClientIp(context));
                    return Results.Ok(new { ok = true, printer, bytes = Encoding.UTF8.GetByteCount(tspl) });
                }

                byte[] utf8 = Encoding.UTF8.GetBytes(tspl);
                return Results.File(utf8, "application/octet-stream", $"arsan-etiket-{DateTime.Now:yyyyMMdd-HHmmss}.tspl");
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new { error = exception.Message });
            }
        }).Requires(Perm.LabelPrint);

        api.MapGet("/etiket/yazicilar", () => Results.Ok(new
        {
            windows = RawPrinter.IsWindows,
            printers = RawPrinter.ListPrinterNames(),
            agentHint = "http://127.0.0.1:39201/health",
            note = RawPrinter.IsWindows
                ? "API Windows üzerinde; /etiket/tspl { send:true } ile RAW basılabilir."
                : "API Linux'ta; tarayıcıdaki TsplPrintAgent (127.0.0.1:39201) veya .tspl indirme kullanın."
        })).Requires(Perm.LabelPrint);
    }

    private static string BuildTspl(LabelTsplRequest request)
    {
        List<TsplLabels.Item> items = (request.Items ?? [])
            .Select(i => new TsplLabels.Item(i.Code ?? "", i.Serial, i.Gas, i.Stt, i.Gtt, i.Owner, i.Extra))
            .ToList();
        TsplLabels.Settings settings = new(
            WidthMm: request.WidthMm ?? 50,
            HeightMm: request.HeightMm ?? 30,
            Copies: request.Copies ?? 1,
            Title: request.Title,
            ShowGas: request.ShowGas ?? true,
            ShowSerial: request.ShowSerial ?? true,
            ShowTest: request.ShowTest ?? true,
            ShowCodeText: request.ShowCodeText ?? true,
            GapMm: request.GapMm ?? 2);
        return TsplLabels.Build(items, settings);
    }

    private static int CountLabels(LabelTsplRequest request) =>
        Math.Max(0, (request.Items?.Count ?? 0) * Math.Clamp(request.Copies ?? 1, 1, 20));

    private static async Task<IEnumerable<object>> CodesAsync(string? prefix, long start, int count, int digits)
    {
        IReadOnlyList<string> codes = LabelCodes.Sequence(prefix, start, count, digits);
        List<string> upper = [.. codes.Select(c => c.ToUpperInvariant())];
        await using TupTakipDbContext db = new();
        var taken = await db.Cylinders.AsNoTracking()
            .Where(c => upper.Contains(c.Barcode.ToUpper()) || upper.Contains(c.SerialNumber.ToUpper()))
            .Select(c => new { c.Barcode, c.SerialNumber, c.GasType })
            .ToListAsync();
        Dictionary<string, string> owner = new(StringComparer.OrdinalIgnoreCase);
        foreach (var t in taken)
        {
            owner.TryAdd(t.Barcode, $"{t.SerialNumber} · {t.GasType}");
            owner.TryAdd(t.SerialNumber, $"{t.SerialNumber} · {t.GasType}");
        }

        return codes.Select(c => (object)new { code = c, usedBy = owner.GetValueOrDefault(c) });
    }

    private static object Json(LabelTemplate t) => new
    {
        id = t.Id, name = t.Name, gasText = t.GasText, prefix = t.Prefix, digits = t.Digits, sampleCode = t.SampleCode, testText = t.TestText,
        title = t.Title, widthMm = t.WidthMm, heightMm = t.HeightMm, copies = t.Copies,
        showGas = t.ShowGas, showSerial = t.ShowSerial, showTest = t.ShowTest, showText = t.ShowText,
        updated = t.UpdatedAt.ToString("dd.MM.yyyy HH:mm"), updatedBy = t.UpdatedBy
    };
}
