using System.Globalization;
using System.Text;
using ArsanTupTakip.Database;
using ArsanTupTakip.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanTupTakip.Services;

// Plain-text reports shown by the mobile app (Özet, tüp ara, müşterideki tüpler, test tarihleri, hareketler, tahmin).
public static class TupReports
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static async Task<string> SummaryAsync()
    {
        await using TupTakipDbContext db = new();
        StringBuilder text = new();
        text.AppendLine($"Aktif müşteri: {await db.Customers.CountAsync(x => x.IsActive)}");
        text.AppendLine("Tüpler (aktif):");
        var groups = await db.Cylinders.AsNoTracking().Where(x => x.IsActive)
            .GroupBy(x => x.Status)
            .Select(g => new { Status = g.Key, Total = g.Count(), Full = g.Count(x => x.IsFull == true), Empty = g.Count(x => x.IsFull == false) })
            .ToListAsync();
        if (groups.Count == 0)
        {
            text.AppendLine("- Henüz tüp kaydı yok.");
        }

        foreach (var group in groups.OrderBy(x => x.Status))
        {
            string split = group.Full + group.Empty > 0 ? $" (dolu {group.Full} · boş {group.Empty})" : string.Empty;
            text.AppendLine($"- {CylinderTracking.StatusName((int)group.Status)}: {group.Total}{split}");
        }

        DateTime longLimit = DateTime.Today.AddDays(-CylinderTracking.LongAtCustomerDays);
        DateTime testLimit = DateTime.Today.AddDays(30);
        DateTime since = DateTime.Today.AddDays(-30);
        text.AppendLine($"{CylinderTracking.LongAtCustomerDays} günden uzun süredir müşteride: {await db.Cylinders.CountAsync(x => x.IsActive && x.Status == CylinderStatus.Customer && x.LastMovementAt != null && x.LastMovementAt < longLimit)}");
        DateTime today = DateTime.Today;
        text.AppendLine($"Test tarihi geçmiş tüp: {await db.Cylinders.CountAsync(x => x.IsActive && x.TestDueDate != null && x.TestDueDate < today)}");
        text.AppendLine($"Testi 30 gün içinde dolacak tüp: {await db.Cylinders.CountAsync(x => x.IsActive && x.TestDueDate != null && x.TestDueDate >= today && x.TestDueDate <= testLimit)}");
        text.AppendLine($"Son 30 günde tüp hareketi: {await db.CylinderMovements.CountAsync(x => x.MovementDate >= since)}");
        return text.ToString();
    }

    public static async Task<string> FindCylinderAsync(string code)
    {
        Cylinder? cylinder = await new CrudService().FindCylinderByCodeAsync(code);
        if (cylinder is null)
        {
            return $"'{code.Trim()}' barkodlu veya seri numaralı tüp bulunamadı.";
        }

        StringBuilder text = new();
        text.AppendLine($"Seri no: {cylinder.SerialNumber} · barkod: {cylinder.Barcode}");
        if (!string.IsNullOrEmpty(cylinder.ManufacturerSerial))
        {
            text.AppendLine($"Üretici seri no: {cylinder.ManufacturerSerial}{(string.IsNullOrEmpty(cylinder.ProductName) ? "" : " · " + cylinder.ProductName)}");
        }

        if (cylinder.OwnerCustomerId is int ownerId)
        {
            await using TupTakipDbContext owners = new();
            string? owner = await owners.Customers.AsNoTracking().Where(x => x.Id == ownerId).Select(x => x.CompanyName).FirstOrDefaultAsync();
            text.AppendLine($"Tüpün sahibi: müşteri malı · {owner ?? "#" + ownerId}");
        }

        if (cylinder.NeedsReview)
        {
            text.AppendLine($"⚠ İnceleme bekliyor: {cylinder.ReviewNote}");
        }

        text.AppendLine($"Gaz: {cylinder.GasType} · kapasite: {cylinder.Capacity.ToString("0.##", Turkish)} {cylinder.CapacityUnit}");
        text.AppendLine($"Durum: {CylinderTracking.StatusName((int)cylinder.Status, cylinder.VehiclePlate)}{(cylinder.CurrentCustomer is null ? "" : " · müşteri: " + cylinder.CurrentCustomer.CompanyName)}");
        if (cylinder.Status != CylinderStatus.Customer)
        {
            text.AppendLine($"Dolu/boş: {CylinderTracking.FullnessName(cylinder.IsFull)}");
        }

        text.AppendLine($"Test tarihi: {Date(cylinder.TestDueDate)} · son hareket: {Date(cylinder.LastMovementAt)}");

        await using TupTakipDbContext db = new();
        var movements = await db.CylinderMovements.AsNoTracking()
            .Where(x => x.CylinderId == cylinder.Id)
            .OrderByDescending(x => x.MovementDate).Take(10)
            .Select(x => new { x.MovementDate, x.MovementType, x.FromStatus, x.ToStatus, Customer = x.Customer != null ? x.Customer.CompanyName : null, x.Description, x.VehiclePlate })
            .ToListAsync();
        text.AppendLine("Son hareketler:");
        if (movements.Count == 0)
        {
            text.AppendLine("- Hareket kaydı yok.");
        }

        foreach (var m in movements)
        {
            text.AppendLine($"- {m.MovementDate:dd.MM.yyyy HH:mm} · {m.MovementType} · {CylinderTracking.MovementPlaces((int)m.FromStatus, (int)m.ToStatus, m.VehiclePlate)}"
                + $"{(m.Customer is null ? "" : " · " + m.Customer)}{(string.IsNullOrEmpty(m.Description) ? "" : " · " + m.Description)}");
        }

        return text.ToString();
    }

    public static async Task<string> CylindersAtCustomersAsync(string? customer)
    {
        DateTime today = DateTime.Today;
        int longDays = CylinderTracking.LongAtCustomerDays;
        List<CustomerStockCylinder> stock = await CustomerStock.LoadAsync();
        await using TupTakipDbContext db = new();
        Dictionary<int, string> names = await db.Customers.AsNoTracking().ToDictionaryAsync(c => c.Id, c => c.CompanyName);
        string term = (customer ?? string.Empty).Trim();
        var customers = stock.GroupBy(c => c.CustomerId)
            .Select(g => (Name: names.GetValueOrDefault(g.Key, $"#{g.Key}"), Rows: g.ToList()))
            .Where(x => term.Length == 0 || Contains(x.Name, term))
            .OrderByDescending(x => x.Rows.Count)
            .ToList();
        if (customers.Count == 0)
        {
            return term.Length == 0 ? "Müşteride duran tüp kaydı yok." : $"'{term}' ile eşleşen müşteride tüp yok.";
        }

        List<string> lines = [$"{customers.Count} müşteride {customers.Sum(x => x.Rows.Count)} tüp (⚠ = {longDays}+ gün):"];
        bool detailed = customers.Count <= 3;
        foreach (var (name, rows) in customers.Take(100))
        {
            CustomerStockTotals totals = CustomerStock.Totals(rows, today, longDays);
            IReadOnlyList<CustomerStockGroup> groups = CustomerStock.Groups(rows, today, longDays);
            lines.Add(string.Empty);
            lines.Add($"■ {name}: {totals.Count} tüp · dolu {totals.Full} · boş {totals.Empty} · en uzun {DaysText(totals.MaxDays)}{(totals.Long > 0 ? $" ⚠{totals.Long}" : string.Empty)}");
            lines.AddRange(groups.Select(g => $"  {g.GasType} {g.Capacity}: {g.Count} adet (dolu {g.Full}, boş {g.Empty}) · {DaysText(g.MinDays)}–{DaysText(g.MaxDays)}"));
            if (detailed)
            {
                lines.AddRange(rows.OrderByDescending(c => CylinderTracking.DaysSince(c.Since, today) ?? -1).Select(c =>
                {
                    int? days = CylinderTracking.DaysSince(c.Since, today);
                    string code = c.ManufacturerSerial is { Length: > 0 } serial ? $"{c.SerialNumber} / {serial}" : c.SerialNumber;
                    return $"  - {code} · {c.GasType} {CustomerStock.CapacityText(c.Capacity, c.CapacityUnit)} · {CylinderTracking.FullnessName(c.IsFull)} · "
                        + $"{c.Since?.ToString("dd.MM.yyyy") ?? "tarih yok"} ({DaysText(days)}){(days >= longDays ? " ⚠" : string.Empty)}";
                }));
            }
        }

        if (customers.Count > 100)
        {
            lines.Add($"… ilk 100 müşteri gösterildi; müşteri adıyla arayın.");
        }
        else if (!detailed)
        {
            lines.Add(string.Empty);
            lines.Add("Barkod listesi için müşteri adıyla arayın (en fazla 3 müşteri eşleşirse tüpler tek tek listelenir).");
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static string DaysText(int? days) => days is int value ? $"{value} gün" : "tarih yok";

    public static async Task<string> LongAtCustomerAsync(int days)
    {
        days = Math.Clamp(days, 7, 365);
        DateTime limit = DateTime.Today.AddDays(-days);
        await using TupTakipDbContext db = new();
        var rows = await db.Cylinders.AsNoTracking()
            .Where(x => x.Status == CylinderStatus.Customer && x.IsActive && x.CurrentCustomerId != null && x.LastMovementAt != null && x.LastMovementAt < limit)
            .OrderBy(x => x.LastMovementAt).Take(200)
            .Select(x => new { Customer = x.CurrentCustomer!.CompanyName, x.SerialNumber, x.GasType, x.LastMovementAt })
            .ToListAsync();
        List<string> lines = rows.Select(x => $"- {x.Customer} · {x.SerialNumber} ({x.GasType}) · {(DateTime.Today - x.LastMovementAt!.Value.Date).Days} gündür müşteride").ToList();
        return lines.Count == 0
            ? $"{days} günden uzun süredir müşteride duran tüp yok."
            : $"{lines.Count} tüp {days} günden uzun süredir müşteride:{Environment.NewLine}{string.Join(Environment.NewLine, lines)}";
    }

    public static async Task<string> TestDueAsync(int days)
    {
        days = Math.Clamp(days, 1, 365);
        DateTime today = DateTime.Today;
        DateTime limit = today.AddDays(days);
        await using TupTakipDbContext db = new();
        IQueryable<Cylinder> active = db.Cylinders.AsNoTracking().Where(x => x.IsActive && x.TestDueDate != null);
        // Upcoming tests first; then the most recently expired ones, because very old dates are usually stale records.
        var upcoming = await TestRows(active.Where(x => x.TestDueDate >= today && x.TestDueDate <= limit).OrderBy(x => x.TestDueDate).Take(150)).ToListAsync();
        int overdueCount = await active.CountAsync(x => x.TestDueDate < today);
        var overdue = await TestRows(active.Where(x => x.TestDueDate < today).OrderByDescending(x => x.TestDueDate).Take(100)).ToListAsync();

        string Line(DateTime? value, string serial, string gas, CylinderStatus status, string? customer)
        {
            DateTime due = value!.Value.Date;
            int left = (due - today).Days;
            string timing = left < 0 ? $"{-left} gün geçti" : left == 0 ? "bugün" : $"{left} gün kaldı";
            return $"- {serial} ({gas}) · test: {due:dd.MM.yyyy} ({timing}) · {CylinderTracking.StatusName((int)status)}{(customer is null ? "" : " · " + customer)}";
        }

        List<string> lines = [upcoming.Count == 0
            ? $"Önümüzdeki {days} gün içinde testi dolacak tüp yok (test tarihi girilmemiş tüpler dahil edilmez)."
            : $"Önümüzdeki {days} gün içinde testi dolacak: {upcoming.Count} tüp"];
        lines.AddRange(upcoming.Select(x => Line(x.TestDueDate, x.SerialNumber, x.GasType, x.Status, x.Customer)));
        if (overdueCount > 0)
        {
            lines.Add(string.Empty);
            lines.Add($"Test tarihi geçmiş: {overdueCount} tüp" + (overdueCount > overdue.Count ? $" (en son geçen {overdue.Count} tanesi; tamamı web panelde Tüpler › Test tarihi: Geçmiş)" : string.Empty));
            lines.AddRange(overdue.Select(x => Line(x.TestDueDate, x.SerialNumber, x.GasType, x.Status, x.Customer)));
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static IQueryable<TestDueRow> TestRows(IQueryable<Cylinder> query) =>
        query.Select(x => new TestDueRow(x.SerialNumber, x.GasType, x.TestDueDate, x.Status, x.CurrentCustomer != null ? x.CurrentCustomer.CompanyName : null));

    private sealed record TestDueRow(string SerialNumber, string GasType, DateTime? TestDueDate, CylinderStatus Status, string? Customer);

    public static async Task<string> ReorderForecastsAsync()
    {
        List<ReorderForecast> forecasts = await new CrudService().ReorderForecastsAsync();
        return forecasts.Count == 0
            ? $"Tahmin için en az {ReorderPrediction.MinimumDeliveryDays} farklı günde teslim kaydı olan müşteri yok."
            : string.Join(Environment.NewLine, forecasts.Take(50).Select(forecast =>
                $"- {forecast.CustomerName} · beklenen: {forecast.ExpectedNext:dd.MM.yyyy} ({forecast.Timing}) · genelde {forecast.TypicalIntervalDays} günde bir · son teslim {forecast.LastDelivery:dd.MM.yyyy}"));
    }

    public static async Task<string> RecentMovementsAsync(int days)
    {
        days = Math.Clamp(days, 1, 90);
        DateTime since = DateTime.Today.AddDays(-days);
        await using TupTakipDbContext db = new();
        var rows = await db.CylinderMovements.AsNoTracking()
            .Where(x => x.MovementDate >= since)
            .OrderByDescending(x => x.MovementDate).Take(200)
            .Select(x => new { x.MovementDate, x.Cylinder!.SerialNumber, x.MovementType, x.FromStatus, x.ToStatus, x.VehiclePlate, Customer = x.Customer != null ? x.Customer.CompanyName : null })
            .ToListAsync();
        List<string> lines = rows.Select(x =>
            $"- {x.MovementDate:dd.MM.yyyy HH:mm} · {x.SerialNumber} · {x.MovementType} · {CylinderTracking.MovementPlaces((int)x.FromStatus, (int)x.ToStatus, x.VehiclePlate)}{(x.Customer is null ? "" : " · " + x.Customer)}").ToList();
        return lines.Count == 0 ? $"Son {days} günde tüp hareketi yok." : string.Join(Environment.NewLine, lines);
    }

    private static string Date(DateTime? value) => value?.ToString("dd.MM.yyyy", Turkish) ?? "-";

    private static bool Contains(string? value, string term) =>
        value is not null && Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;
}
