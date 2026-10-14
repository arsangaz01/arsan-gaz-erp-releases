using System.Globalization;
using ArsanTupTakip.Database;
using ArsanTupTakip.Services;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Api;

internal sealed record StockCountStartRequest(string? Note);
internal sealed record StockCountScanRequest(string? Code);

// Depot count: the phone scans, the panel shows the "Sayım Raporları" tab.
internal static class StockCountEndpoints
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static void MapMobile(WebApplication app)
    {
        app.MapGet("/api/depo-sayim/aktif", async () =>
        {
            StockCountReport? report = await new StockCountService().ActiveAsync();
            return Results.Ok(new { count = report is null ? null : Json(report, lists: false) });
        }).Requires(Perm.StockCount);

        app.MapPost("/api/depo-sayim/baslat", (StockCountStartRequest request, HttpContext context) => Run(async () =>
        {
            (StockCountReport report, bool existing) = await new StockCountService().StartAsync(MobileEndpoints.DeviceName(context), request.Note);
            return new
            {
                message = existing ? $"Açık sayıma devam ediliyor ({report.StartedBy}, {Time(report.StartedAt)})." : "Depo sayımı başlatıldı.",
                count = Json(report, lists: false)
            };
        })).Requires(Perm.StockCount);

        app.MapPost("/api/depo-sayim/{id:int}/okut", (int id, StockCountScanRequest request, HttpContext context) => Run(async () =>
        {
            StockCountScanResult result = await new StockCountService().ScanAsync(id, request.Code, MobileEndpoints.DeviceName(context));
            return new { outcome = result.Outcome, message = result.Message, group = result.Group, count = Json(result.Report, lists: false) };
        })).Requires(Perm.StockCount);

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
    }

    public static void MapPanel(RouteGroupBuilder api)
    {
        api.MapGet("/sayimlar", async () =>
        {
            List<StockCountListItem> counts = await new StockCountService().ListAsync();
            await using TupTakipDbContext db = new();
            var audits = await db.CylinderAudits.AsNoTracking().OrderByDescending(x => x.AuditedAt).Take(100).ToListAsync();
            return Results.Ok(new
            {
                counts = counts.Select(c => new
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
                }),
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
