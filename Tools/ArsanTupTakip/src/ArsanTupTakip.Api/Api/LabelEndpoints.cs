using ArsanTupTakip.Database;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record LabelSequenceRequest(string? Prefix, long Start, int Count, int Digits);
internal sealed record LabelPrintedRequest(int Labels, List<string>? Codes, string? Size);

// Barkod Yazdır (panel only): the labels are drawn in the browser and printed through the TSC driver of the computer;
// the server only hands out numbers, flags codes that are already used and writes the audit entry.
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
                IReadOnlyList<string> codes = LabelCodes.Sequence(request.Prefix, request.Start, request.Count, request.Digits);
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

                return Results.Ok(new { codes = codes.Select(c => new { code = c, usedBy = owner.GetValueOrDefault(c) }) });
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
            WebPanelAudit.Write((string)context.Items["panelUser"]!, "Barkod etiketi yazdırmaya gönderildi", $"{Math.Clamp(request.Labels, 0, 100000)} etiket",
                (string.IsNullOrWhiteSpace(request.Size) ? string.Empty : request.Size.Trim()[..Math.Min(request.Size.Trim().Length, 40)] + " · ") + sample,
                WebPanelAuth.ClientIp(context));
            return Results.Ok(new { ok = true });
        }).Requires(Perm.LabelPrint);
    }
}
