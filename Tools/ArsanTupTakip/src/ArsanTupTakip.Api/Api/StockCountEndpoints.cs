using System.Globalization;
using ArsanTupTakip.Database;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record StockCountStartRequest(string? Note, string? Location = null);
internal sealed record StockCountScanRequest(string? Code, DateTime? At = null);

// Depot count: the phone scans, the panel shows the "Sayım Raporları" tab.
internal static class StockCountEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void MapMobile(WebApplication app)
    {
        app.MapGet("/api/depo-sayim/aktif", async (string? lokasyon) =>
        {
            StockCountService service = new();
            if (!string.IsNullOrWhiteSpace(lokasyon))
            {
                StockCountReport? one = await service.ActiveAsync(lokasyon);
                return Results.Ok(new
                {
                    count = one is null ? null : Json(one, lists: false),
                    counts = one is null ? Array.Empty<object>() : new[] { Json(one, lists: false) }
                });
            }

            List<StockCountReport> open = await service.ActiveAllAsync();
            return Results.Ok(new
            {
                count = open.Count == 0 ? null : Json(open[0], lists: false),
                counts = open.Select(r => Json(r, lists: false))
            });
        }).Requires(Perm.StockCount);

        app.MapPost("/api/depo-sayim/baslat", (StockCountStartRequest request, HttpContext context) => Run(async () =>
        {
            (StockCountReport report, bool existing) = await new StockCountService().StartAsync(
                MobileEndpoints.DeviceName(context), request.Note, request.Location);
            return new
            {
                message = existing
                    ? $"Açık sayıma devam ediliyor ({report.Location} · {report.StartedBy}, {Time(report.StartedAt)})."
                    : $"{report.Location} sayımı başlatıldı.",
                count = Json(report, lists: false)
            };
        })).Requires(Perm.StockCount);

        app.MapPost("/api/depo-sayim/{id:int}/okut", (int id, StockCountScanRequest request, HttpContext context) => Run(async () =>
        {
            StockCountScanResult result = await new StockCountService().ScanAsync(id, request.Code, MobileEndpoints.DeviceName(context), request.At);
            return new { outcome = result.Outcome, message = result.Message, group = result.Group, count = Json(result.Report, lists: false) };
        })).Requires(Perm.StockCount);

        app.MapGet("/api/depo-sayim/{id:int}/grup", async (int id, string? gaz, string? boyut) =>
        {
            StockCountGroupDetail? detail = await new StockCountService().GroupAsync(id, gaz, boyut);
            return detail is null
                ? Results.NotFound(new { error = "Sayım bulunamadı." })
                : Results.Ok(new
                {
                    title = detail.Group?.Title ?? StockCounting.GroupTitle(gaz, boyut),
                    matched = detail.Matched.Select(Cylinder),
                    missing = detail.Missing.Select(Cylinder),
                    extra = detail.Extra.Select(Cylinder)
                });
        }).Requires(Perm.StockCount);

        app.MapPost("/api/depo-sayim/{id:int}/geri-al", (int id, StockCountScanRequest request) => Run(async () =>
        {
            StockCountReport report = await new StockCountService().UndoAsync(id, request.Code);
            return new { message = $"{request.Code?.Trim()} sayımdan çıkarıldı.", count = Json(report, lists: false) };
        })).Requires(Perm.StockCount);

        app.MapPost("/api/depo-sayim/{id:int}/bitir", (int id, HttpContext context) => Run(async () =>
        {
            StockCountReport? report = await new StockCountService().CloseAsync(id, MobileEndpoints.DeviceName(context));
            return report is null
                ? new { message = "Hiç tüp okutulmadığı için sayım kaydedilmeden kapatıldı.", count = (object?)null }
                : new { message = report.Text + " Rapor panelde Sayım Raporları sekmesinde.", count = (object?)Json(report, lists: true) };
        })).Requires(Perm.StockCount);

        app.MapGet("/api/depo-sayim/liste", async () =>
            Results.Ok((await new StockCountService().ListAsync(30)).Select(ListRow))).Requires(Perm.StockCount, Perm.Reports);
        // Counting alone (e.g. the Şoför role) does not include downloading the reports.
        app.MapGet("/api/depo-sayim/{id:int}/excel", (int id) => ExportAsync(id, excel: true)).Requires(Perm.Reports, Perm.Export);
        app.MapGet("/api/depo-sayim/{id:int}/pdf", (int id) => ExportAsync(id, excel: false)).Requires(Perm.Reports, Perm.Export);
    }

    public static void MapPanel(RouteGroupBuilder api)
    {
        api.MapGet("/sayim/{id:int}/excel", (int id) => ExportAsync(id, excel: true)).Requires(Perm.StockCount, Perm.CylinderAudit, Perm.Reports);
        api.MapGet("/sayim/{id:int}/pdf", (int id) => ExportAsync(id, excel: false)).Requires(Perm.StockCount, Perm.CylinderAudit, Perm.Reports);

        api.MapGet("/sayimlar", async () =>
        {
            List<StockCountListItem> counts = await new StockCountService().ListAsync();
            await using TupTakipDbContext db = new();
            var audits = await db.CylinderAudits.AsNoTracking().OrderByDescending(x => x.AuditedAt).Take(100).ToListAsync();
            return Results.Ok(new
            {
                counts = counts.Select(ListRow),
                audits = audits.Select(a => new
                {
                    a.Id,
                    a.CustomerId,
                    a.CustomerName,
                    At = Time(a.AuditedAt),
                    Expected = a.ExpectedCount,
                    Found = a.FoundCount,
                    Missing = Split(a.MissingSerials),
                    Unexpected = Split(a.UnexpectedSerials),
                    Unknown = Split(a.UnknownCodes),
                    a.Note,
                    a.PerformedBy
                })
            });
        }).Requires(Perm.StockCount, Perm.CylinderAudit, Perm.Reports);

        api.MapGet("/sayim/{id:int}", async (int id) =>
        {
            StockCountReport? report = await new StockCountService().GetAsync(id);
            return report is null ? Results.NotFound(new { error = "Sayım bulunamadı." }) : Results.Ok(Json(report, lists: true));
        }).Requires(Perm.StockCount, Perm.CylinderAudit, Perm.Reports);

        api.MapPost("/sayim/{id:int}/bitir", (int id, HttpContext context) => Run(async () =>
        {
            string user = (string)context.Items["panelUser"]!;
            StockCountReport? report = await new StockCountService().CloseAsync(id, user + " (panel)");
            WebPanelAudit.Write(user, "Depo sayımı bitirildi", "#" + id, report?.Text ?? "Boş sayım silindi", WebPanelAuth.ClientIp(context));
            return report is null
                ? new { message = "Hiç tüp okutulmadığı için sayım kaydedilmeden kapatıldı.", count = (object?)null }
                : new { message = report.Text, count = (object?)Json(report, lists: true) };
        })).Requires(Perm.StockCount);
    }

    private static object ListRow(StockCountListItem c) => new
    {
        c.Id,
        c.Location,
        Started = Time(c.StartedAt),
        c.StartedBy,
        Closed = c.ClosedAt is DateTime closed ? Time(closed) : null,
        c.ClosedBy,
        Open = c.ClosedAt is null,
        c.Note,
        c.Expected,
        c.Counted,
        c.Matched,
        c.Missing,
        c.Extra,
        c.Unknown
    };

    private static async Task<IResult> ExportAsync(int id, bool excel)
    {
        StockCountReport? report = await new StockCountService().GetAsync(id);
        if (report is null)
        {
            return Results.NotFound(new { error = "Sayım bulunamadı." });
        }

        return excel
            ? Results.File(StockCountExport.Xlsx(report), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StockCountExport.FileName(report, "xlsx"))
            : Results.File(StockCountExport.Pdf(report), "application/pdf", StockCountExport.FileName(report, "pdf"));
    }

    internal static object Json(StockCountReport r, bool lists)
    {
        StockCountComparison c = r.Comparison;
        return new
        {
            r.Id,
            r.Location,
            Started = Time(r.StartedAt),
            r.StartedBy,
            Closed = r.ClosedAt is DateTime closed ? Time(closed) : null,
            r.ClosedBy,
            r.Open,
            r.Note,
            r.Expected,
            r.Counted,
            Matched = c.Matched.Count,
            Missing = c.Missing.Count,
            Extra = c.Extra.Count,
            Unknown = r.Unknown.Count,
            Text = r.Text,
            Groups = c.Groups.Select(g => new { g.Title, g.GasType, g.Capacity, g.Expected, g.Counted, g.Matched, g.Missing, g.Extra, g.Full, g.Empty }),
            Recent = r.Recent.Select(Cylinder),
            UnknownCodes = r.Unknown.Select(u => new { u.Code, At = Time(u.ScannedAt), u.ScannedBy }),
            MatchedList = lists ? c.Matched.Select(Cylinder) : null,
            MissingList = lists ? c.Missing.Select(Cylinder) : null,
            ExtraList = lists ? c.Extra.Select(Cylinder) : null
        };
    }

    private static object Cylinder(CountCylinder x) => new
    {
        x.Id,
        x.SerialNumber,
        x.Barcode,
        x.GasType,
        x.Capacity,
        Group = StockCounting.GroupTitle(x.GasType, x.Capacity),
        Fullness = CylinderTracking.FullnessName(x.IsFull),
        x.Place,
        At = x.ScannedAt is DateTime at ? Time(at) : null,
        x.ScannedBy
    };

    private static string[] Split(string? text) =>
        (text ?? string.Empty).Split(", ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static string Time(DateTime local) => local.ToString("dd.MM.yyyy HH:mm", Turkish);

    private static async Task<IResult> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return Results.Ok(await action());
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return Results.BadRequest(new { error = exception.Message });
        }
    }
}
