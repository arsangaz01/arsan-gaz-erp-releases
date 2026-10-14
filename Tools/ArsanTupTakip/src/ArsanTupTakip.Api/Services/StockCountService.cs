using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

public sealed record CountCylinder(int Id, string SerialNumber, string Barcode, string GasType, string Capacity, bool? IsFull, string Place,
    DateTime? ScannedAt = null, string? ScannedBy = null);

public sealed record StockCountGroup(string Title, string GasType, string Capacity, int Expected, int Counted, int Matched, int Missing, int Extra, int Full, int Empty);

public sealed record StockCountComparison(
    IReadOnlyList<StockCountGroup> Groups,
    IReadOnlyList<CountCylinder> Matched,
    IReadOnlyList<CountCylinder> Missing,
    IReadOnlyList<CountCylinder> Extra);

public sealed record UnknownScan(string Code, DateTime ScannedAt, string ScannedBy);

// The cylinders behind one gas/size row of a count: tapped on the phone to see which barcodes are still unscanned.
public sealed record StockCountGroupDetail(StockCountGroup? Group, IReadOnlyList<CountCylinder> Matched, IReadOnlyList<CountCylinder> Missing, IReadOnlyList<CountCylinder> Extra);

public sealed record StockCountReport(
    int Id,
    string Location,
    DateTime StartedAt,
    string StartedBy,
    DateTime? ClosedAt,
    string? ClosedBy,
    string? Note,
    StockCountComparison Comparison,
    IReadOnlyList<UnknownScan> Unknown,
    IReadOnlyList<CountCylinder> Recent)
{
    public bool Open => ClosedAt is null;
    public int Expected => Comparison.Matched.Count + Comparison.Missing.Count;
    public int Counted => Comparison.Matched.Count + Comparison.Extra.Count;

    public string Text => $"{Location} sayımı: {Counted} tüp okutuldu, {Comparison.Matched.Count} kayıtla eşleşti, "
        + $"{Comparison.Missing.Count} eksik, {Comparison.Extra.Count} fazla/yer uyuşmazlığı, {Unknown.Count} tanınmayan barkod.";
}

public sealed record StockCountListItem(int Id, string Location, DateTime StartedAt, string StartedBy, DateTime? ClosedAt, string? ClosedBy, string? Note,
    int Expected, int Counted, int Matched, int Missing, int Extra, int Unknown);

public sealed record StockCountScanResult(string Outcome, string Message, string? Group, StockCountReport Report);

// Depot count: matched = scanned while the records said "Depoda"; extra = scanned but the records put it elsewhere;
// missing = in the warehouse by the records but not scanned.
public static class StockCounting
{
    public const string MainDepot = "Ana depo";
    public const int MaxItems = 20000;
    public const int MaxCodeLength = 200;
    public const int MaxNoteLength = 500;

    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly StringComparer TurkishOrder = StringComparer.Create(Turkish, true);

    public static StockCountComparison Compare(IEnumerable<CountCylinder> expected, IEnumerable<(CountCylinder Cylinder, bool InWarehouse)> scanned)
    {
        List<(CountCylinder Cylinder, bool InWarehouse)> scans = scanned.GroupBy(x => x.Cylinder.Id).Select(g => g.First()).ToList();
        HashSet<int> scannedIds = [.. scans.Select(x => x.Cylinder.Id)];
        List<CountCylinder> matched = scans.Where(x => x.InWarehouse).Select(x => x.Cylinder).ToList();
        List<CountCylinder> extra = scans.Where(x => !x.InWarehouse).Select(x => x.Cylinder).ToList();
        List<CountCylinder> missing = expected.GroupBy(x => x.Id).Select(g => g.First()).Where(x => !scannedIds.Contains(x.Id)).ToList();

        var tagged = matched.Select(c => (c, Kind: 'm')).Concat(missing.Select(c => (c, Kind: 'x'))).Concat(extra.Select(c => (c, Kind: 'e')));
        List<StockCountGroup> groups = tagged
            .GroupBy(x => GroupKey(x.c.GasType, x.c.Capacity))
            .Select(g =>
            {
                CountCylinder first = g.First().c;
                string gas = VehicleTracking.GasKey(first.GasType);
                string capacity = VehicleTracking.SizeKey(first.Capacity) ?? string.Empty;
                int m = g.Count(x => x.Kind == 'm');
                int miss = g.Count(x => x.Kind == 'x');
                int e = g.Count(x => x.Kind == 'e');
                List<CountCylinder> counted = g.Where(x => x.Kind != 'x').Select(x => x.c).ToList();
                return new StockCountGroup(capacity.Length == 0 ? gas : $"{gas} {capacity}", gas, capacity, m + miss, m + e, m, miss, e,
                    counted.Count(x => x.IsFull == true), counted.Count(x => x.IsFull == false));
            })
            .OrderBy(x => x.GasType, TurkishOrder)
            .ThenBy(x => VehicleTracking.SizeValue(x.Capacity))
            .ToList();

        return new StockCountComparison(groups, Sorted(matched), Sorted(missing), Sorted(extra));
    }

    public static StockCountGroupDetail Group(StockCountComparison comparison, string? gasType, string? capacity)
    {
        string key = GroupKey(gasType, capacity);
        List<CountCylinder> Of(IReadOnlyList<CountCylinder> list) => list.Where(x => GroupKey(x.GasType, x.Capacity) == key).ToList();
        return new StockCountGroupDetail(comparison.Groups.FirstOrDefault(g => GroupKey(g.GasType, g.Capacity) == key),
            Of(comparison.Matched), Of(comparison.Missing), Of(comparison.Extra));
    }

    public static string GroupKey(string? gasType, string? capacity) =>
        VehicleTracking.GasKey(gasType).ToUpper(Turkish) + "|" + (VehicleTracking.SizeKey(capacity) ?? string.Empty).ToUpper(Turkish);

    public static string GroupTitle(string? gasType, string? capacity) =>
        VehicleTracking.SizeKey(capacity) is string size ? $"{VehicleTracking.GasKey(gasType)} {size}" : VehicleTracking.GasKey(gasType);

    public static string Place(int status, string? customer, string? plate) => status switch
    {
        CylinderTracking.Customer when !string.IsNullOrWhiteSpace(customer) => $"Müşteride · {customer}",
        CylinderTracking.Vehicle when !string.IsNullOrWhiteSpace(plate) => $"Araçta · {plate}",
        _ => CylinderTracking.StatusName(status)
    };

    private static List<CountCylinder> Sorted(IEnumerable<CountCylinder> list) =>
        list.OrderBy(x => x.GasType, TurkishOrder).ThenBy(x => VehicleTracking.SizeValue(x.Capacity)).ThenBy(x => x.SerialNumber, TurkishOrder).ToList();
}

public sealed class StockCountService
{
    private const int RecentCount = 15;

    public async Task<StockCountReport?> ActiveAsync(string location = StockCounting.MainDepot)
    {
        await using TupTakipDbContext db = new();
        int? id = await db.StockCounts.AsNoTracking().Where(x => x.Location == location && x.ClosedAt == null)
            .OrderByDescending(x => x.Id).Select(x => (int?)x.Id).FirstOrDefaultAsync();
        return id is int open ? await ReportAsync(db, open) : null;
    }

    public async Task<(StockCountReport Report, bool Existing)> StartAsync(string startedBy, string? note, string location = StockCounting.MainDepot)
    {
        if ((note ?? string.Empty).Trim().Length > StockCounting.MaxNoteLength)
        {
            throw new ArgumentException($"Not en fazla {StockCounting.MaxNoteLength} karakter olabilir.");
        }

        await using TupTakipDbContext db = new();
        int? open = await db.StockCounts.AsNoTracking().Where(x => x.Location == location && x.ClosedAt == null)
            .Select(x => (int?)x.Id).FirstOrDefaultAsync();
        if (open is int existing)
        {
            return (await ReportAsync(db, existing), true);
        }

        StockCount count = new() { Location = location, StartedAt = DateTime.Now, StartedBy = startedBy, Note = VehicleTracking.Clean(note) };
        db.StockCounts.Add(count);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // Another phone opened the count at the same moment (one open count per location).
            await using TupTakipDbContext again = new();
            int other = await again.StockCounts.AsNoTracking().Where(x => x.Location == location && x.ClosedAt == null).Select(x => x.Id).FirstAsync();
            return (await ReportAsync(again, other), true);
        }

        return (await ReportAsync(db, count.Id), false);
    }

    // scannedAt: time on the phone when the code was read offline and sent later by "Senkronize et".
    public async Task<StockCountScanResult> ScanAsync(int countId, string? code, string scannedBy, DateTime? scannedAt = null)
    {
        DateTime now = DateTime.Now;
        DateTime at = scannedAt is DateTime sent && sent > now.AddDays(-7) && sent < now.AddMinutes(5) ? sent : now;
        string value = (code ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            throw new ArgumentException("Barkod boş.");
        }

        if (value.Length > StockCounting.MaxCodeLength)
        {
            throw new ArgumentException("Barkod çok uzun.");
        }

        await using TupTakipDbContext db = new();
        StockCount count = await OpenCountAsync(db, countId);
        if (await db.StockCountItems.CountAsync(x => x.CountId == count.Id) >= StockCounting.MaxItems)
        {
            throw new InvalidOperationException($"Bir sayımda en fazla {StockCounting.MaxItems} okutma olabilir.");
        }

        Cylinder? cylinder = await new CrudService().FindCylinderByCodeAsync(value);
        string lower = value.ToLower();
        bool duplicate = cylinder is null
            ? await db.StockCountItems.AnyAsync(x => x.CountId == count.Id && x.CylinderId == null && x.Code.ToLower() == lower)
            : await db.StockCountItems.AnyAsync(x => x.CountId == count.Id && x.CylinderId == cylinder.Id);
        string? group = cylinder is null ? null : StockCounting.GroupTitle(cylinder.GasType, CustomerStock.CapacityText(cylinder.Capacity, cylinder.CapacityUnit));
        if (!duplicate)
        {
            db.StockCountItems.Add(new StockCountItem
            {
                CountId = count.Id,
                CylinderId = cylinder?.Id,
                Code = value,
                ScannedAt = at,
                ScannedBy = scannedBy,
                StatusAtScan = cylinder is null ? null : (int)cylinder.Status,
                PlaceAtScan = cylinder is null ? null : StockCounting.Place((int)cylinder.Status, cylinder.CurrentCustomer?.CompanyName, cylinder.VehiclePlate)
            });
            try
            {
                await db.SaveChangesAsync();
            }
            catch (DbUpdateException)
            {
                duplicate = true;
                db.ChangeTracker.Clear();
            }
        }

        StockCountReport report = await ReportAsync(db, count.Id);
        StockCountGroup? row = group is null ? null : report.Comparison.Groups.FirstOrDefault(x => x.Title == group);
        string tally = row is null ? string.Empty : $" · {row.Title}: {row.Matched} eşleşti / kayıtta {row.Expected}";
        (string outcome, string message) = (cylinder, duplicate) switch
        {
            (null, true) => ("duplicate", $"{value}: bu tanınmayan kod zaten okutuldu."),
            (null, false) => ("unknown", $"❌ HATA: {value} sistemde kayıtlı değil (tanınmayan barkod olarak not edildi)."),
            (_, true) => ("duplicate", $"{cylinder.SerialNumber} zaten okutuldu, tekrar sayılmadı{tally}"),
            _ when cylinder.Status == CylinderStatus.Warehouse => ("matched", $"✅ EŞLEŞTİ: {cylinder.SerialNumber}{tally}"),
            _ => ("extra", $"❌ HATA: {cylinder.SerialNumber} depo stokunda yok, kayıtta {StockCounting.Place((int)cylinder.Status, cylinder.CurrentCustomer?.CompanyName, cylinder.VehiclePlate)}. "
                + $"Stoka sayılmadı; raporda \"stokta olmayan\" listesine yazıldı. Tüp buradaysa hareketini düzeltin.{(row is null ? string.Empty : $" · {row.Title}")}")
        };
        return new StockCountScanResult(outcome, message, group, report);
    }

    public async Task<StockCountReport> UndoAsync(int countId, string? code)
    {
        string value = (code ?? string.Empty).Trim();
        await using TupTakipDbContext db = new();
        StockCount count = await OpenCountAsync(db, countId);
        Cylinder? cylinder = value.Length == 0 ? null : await new CrudService().FindCylinderByCodeAsync(value);
        string lower = value.ToLower();
        StockCountItem item = await db.StockCountItems.FirstOrDefaultAsync(x => x.CountId == count.Id
                && (cylinder != null ? x.CylinderId == cylinder.Id : x.CylinderId == null && x.Code.ToLower() == lower))
            ?? throw new InvalidOperationException($"{value} bu sayımda okutulmamış.");
        db.StockCountItems.Remove(item);
        await db.SaveChangesAsync();
        return await ReportAsync(db, count.Id);
    }

    // Freezes the expected list; an empty count is dropped instead of being kept as a report.
    public async Task<StockCountReport?> CloseAsync(int countId, string closedBy)
    {
        await using TupTakipDbContext db = new();
        StockCount count = await OpenCountAsync(db, countId);
        if (!await db.StockCountItems.AnyAsync(x => x.CountId == count.Id))
        {
            db.StockCounts.Remove(count);
            await db.SaveChangesAsync();
            return null;
        }

        StockCountReport live = await ReportAsync(db, count.Id);
        count.ClosedAt = DateTime.Now;
        count.ClosedBy = closedBy;
        count.ExpectedSnapshot = string.Join(",", live.Comparison.Matched.Concat(live.Comparison.Missing).Select(x => x.Id).Order());
        count.ExpectedCount = live.Expected;
        count.MatchedCount = live.Comparison.Matched.Count;
        count.MissingCount = live.Comparison.Missing.Count;
        count.ExtraCount = live.Comparison.Extra.Count;
        count.UnknownCount = live.Unknown.Count;
        await db.SaveChangesAsync();
        return await ReportAsync(db, count.Id);
    }

    public async Task<StockCountGroupDetail?> GroupAsync(int countId, string? gasType, string? capacity)
    {
        StockCountReport? report = await GetAsync(countId);
        return report is null ? null : StockCounting.Group(report.Comparison, gasType, capacity);
    }

    public async Task<StockCountReport?> GetAsync(int countId)
    {
        await using TupTakipDbContext db = new();
        return await db.StockCounts.AnyAsync(x => x.Id == countId) ? await ReportAsync(db, countId) : null;
    }

    public async Task<List<StockCountListItem>> ListAsync(int take = 100)
    {
        await using TupTakipDbContext db = new();
        List<StockCount> counts = await db.StockCounts.AsNoTracking().OrderByDescending(x => x.StartedAt).Take(Math.Clamp(take, 1, 500)).ToListAsync();
        List<StockCountListItem> result = [];
        foreach (StockCount c in counts)
        {
            if (c.ClosedAt is null)
            {
                StockCountReport live = await ReportAsync(db, c.Id);
                result.Add(new(c.Id, c.Location, c.StartedAt, c.StartedBy, null, null, c.Note, live.Expected, live.Counted,
                    live.Comparison.Matched.Count, live.Comparison.Missing.Count, live.Comparison.Extra.Count, live.Unknown.Count));
            }
            else
            {
                result.Add(new(c.Id, c.Location, c.StartedAt, c.StartedBy, c.ClosedAt, c.ClosedBy, c.Note, c.ExpectedCount,
                    c.MatchedCount + c.ExtraCount, c.MatchedCount, c.MissingCount, c.ExtraCount, c.UnknownCount));
            }
        }

        return result;
    }

    private static async Task<StockCount> OpenCountAsync(TupTakipDbContext db, int countId)
    {
        StockCount count = await db.StockCounts.FirstOrDefaultAsync(x => x.Id == countId)
            ?? throw new InvalidOperationException("Sayım bulunamadı.");
        return count.ClosedAt is null ? count : throw new InvalidOperationException("Bu sayım bitirilmiş; yeni okutma için yeni sayım başlatın.");
    }

    private static async Task<StockCountReport> ReportAsync(TupTakipDbContext db, int countId)
    {
        StockCount count = await db.StockCounts.AsNoTracking().FirstAsync(x => x.Id == countId);
        var items = await db.StockCountItems.AsNoTracking().Where(x => x.CountId == countId).OrderBy(x => x.ScannedAt).ThenBy(x => x.Id).ToListAsync();

        List<int> scannedIds = items.Where(x => x.CylinderId != null).Select(x => x.CylinderId!.Value).ToList();
        IQueryable<Cylinder> expectedQuery;
        if (count.ClosedAt is null)
        {
            expectedQuery = db.Cylinders.Where(x => x.IsActive && x.Status == CylinderStatus.Warehouse);
        }
        else
        {
            List<int> snapshot = (count.ExpectedSnapshot ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(x => int.TryParse(x, out int id) ? id : 0).Where(x => x > 0).ToList();
            expectedQuery = db.Cylinders.Where(x => snapshot.Contains(x.Id));
        }

        var expectedRows = await expectedQuery.AsNoTracking()
            .Select(x => new { x.Id, x.SerialNumber, x.Barcode, x.GasType, x.Capacity, x.CapacityUnit, x.IsFull }).ToListAsync();
        var scannedRows = await db.Cylinders.AsNoTracking().Where(x => scannedIds.Contains(x.Id))
            .Select(x => new { x.Id, x.SerialNumber, x.Barcode, x.GasType, x.Capacity, x.CapacityUnit, x.IsFull }).ToListAsync();
        var byId = scannedRows.ToDictionary(x => x.Id);

        List<CountCylinder> expected = expectedRows
            .Select(x => new CountCylinder(x.Id, x.SerialNumber, x.Barcode, x.GasType, CustomerStock.CapacityText(x.Capacity, x.CapacityUnit), x.IsFull,
                CylinderTracking.StatusName(CylinderTracking.Warehouse)))
            .ToList();
        List<(CountCylinder, bool)> scanned = [];
        List<CountCylinder> recent = [];
        foreach (StockCountItem item in items)
        {
            if (item.CylinderId is not int id || !byId.TryGetValue(id, out var c))
            {
                continue;
            }

            CountCylinder cylinder = new(c.Id, c.SerialNumber, c.Barcode, c.GasType, CustomerStock.CapacityText(c.Capacity, c.CapacityUnit), c.IsFull,
                item.PlaceAtScan ?? CylinderTracking.StatusName(item.StatusAtScan ?? 0), item.ScannedAt, item.ScannedBy);
            scanned.Add((cylinder, item.StatusAtScan == CylinderTracking.Warehouse));
            recent.Add(cylinder);
        }

        List<UnknownScan> unknown = items.Where(x => x.CylinderId is null).Select(x => new UnknownScan(x.Code, x.ScannedAt, x.ScannedBy)).ToList();
        recent.Reverse();
        return new StockCountReport(count.Id, count.Location, count.StartedAt, count.StartedBy, count.ClosedAt, count.ClosedBy, count.Note,
            StockCounting.Compare(expected, scanned), unknown, recent.Take(RecentCount).ToList());
    }
}
