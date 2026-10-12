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
        text.AppendLine($"Testi 30 gün içinde dolacak veya geçmiş tüp: {await db.Cylinders.CountAsync(x => x.IsActive && x.TestDueDate != null && x.TestDueDate <= testLimit)}");
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
        text.AppendLine($"Gaz: {cylinder.GasType} · kapasite: {cylinder.Capacity.ToString("0.##", Turkish)} {cylinder.CapacityUnit}");
        text.AppendLine($"Durum: {CylinderTracking.StatusName((int)cylinder.Status)}{(cylinder.VehiclePlate is null ? "" : " · plaka: " + cylinder.VehiclePlate)}{(cylinder.CurrentCustomer is null ? "" : " · müşteri: " + cylinder.CurrentCustomer.CompanyName)}");
        text.AppendLine($"Dolu/boş: {CylinderTracking.FullnessName(cylinder.IsFull)}");
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
            text.AppendLine($"- {m.MovementDate:dd.MM.yyyy HH:mm} · {m.MovementType} · {CylinderTracking.StatusName((int)m.FromStatus)} → {CylinderTracking.StatusName((int)m.ToStatus)}"
                + $"{(m.VehiclePlate is null ? "" : " · " + m.VehiclePlate)}{(m.Customer is null ? "" : " · " + m.Customer)}{(string.IsNullOrEmpty(m.Description) ? "" : " · " + m.Description)}");
        }

        return text.ToString();
    }

    public static async Task<string> CylindersAtCustomersAsync(string? customer)
    {
        await using TupTakipDbContext db = new();
        var rows = await db.Cylinders.AsNoTracking()
            .Where(x => x.Status == CylinderStatus.Customer && x.IsActive && x.CurrentCustomerId != null)
            .OrderBy(x => x.LastMovementAt)
            .Select(x => new { Customer = x.CurrentCustomer!.CompanyName, x.SerialNumber, x.GasType, x.LastMovementAt })
            .ToListAsync();
        string term = (customer ?? string.Empty).Trim();
        List<string> lines = rows
            .Where(x => term.Length == 0 || Contains(x.Customer, term))
            .Take(100)
            .Select(x =>
            {
                int? elapsed = CylinderTracking.DaysSince(x.LastMovementAt, DateTime.Today);
                string days = elapsed is int value
                    ? $"{value} gündür müşteride{(value > CylinderTracking.LongAtCustomerDays ? " ⚠" : "")}"
                    : "teslim tarihi yok";
                return $"- {x.Customer} · {x.SerialNumber} ({x.GasType}) · {days}";
            })
            .ToList();
        return lines.Count == 0 ? "Müşteride duran tüp kaydı yok." : string.Join(Environment.NewLine, lines);
    }

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
        DateTime limit = DateTime.Today.AddDays(days);
        await using TupTakipDbContext db = new();
        var rows = await db.Cylinders.AsNoTracking()
            .Where(x => x.IsActive && x.TestDueDate != null && x.TestDueDate <= limit)
            .OrderBy(x => x.TestDueDate).Take(200)
            .Select(x => new { x.SerialNumber, x.GasType, x.TestDueDate, x.Status, Customer = x.CurrentCustomer != null ? x.CurrentCustomer.CompanyName : null })
            .ToListAsync();
        List<string> lines = rows.Select(x =>
        {
            DateTime due = x.TestDueDate!.Value.Date;
            int left = (due - DateTime.Today).Days;
            string timing = left < 0 ? $"{-left} gün geçti" : left == 0 ? "bugün" : $"{left} gün kaldı";
            return $"- {x.SerialNumber} ({x.GasType}) · test: {due:dd.MM.yyyy} ({timing}) · {CylinderTracking.StatusName((int)x.Status)}{(x.Customer is null ? "" : " · " + x.Customer)}";
        }).ToList();
        return lines.Count == 0
            ? $"Önümüzdeki {days} gün içinde testi dolacak tüp yok (test tarihi girilmemiş tüpler dahil edilmez)."
            : string.Join(Environment.NewLine, lines);
    }

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
            .Select(x => new { x.MovementDate, x.Cylinder!.SerialNumber, x.MovementType, x.FromStatus, x.ToStatus, Customer = x.Customer != null ? x.Customer.CompanyName : null })
            .ToListAsync();
        List<string> lines = rows.Select(x =>
            $"- {x.MovementDate:dd.MM.yyyy HH:mm} · {x.SerialNumber} · {x.MovementType} · {CylinderTracking.StatusName((int)x.FromStatus)} → {CylinderTracking.StatusName((int)x.ToStatus)}{(x.Customer is null ? "" : " · " + x.Customer)}").ToList();
        return lines.Count == 0 ? $"Son {days} günde tüp hareketi yok." : string.Join(Environment.NewLine, lines);
    }

    private static string Date(DateTime? value) => value?.ToString("dd.MM.yyyy", Turkish) ?? "-";

    private static bool Contains(string? value, string term) =>
        value is not null && Turkish.CompareInfo.IndexOf(value, term, CompareOptions.IgnoreCase) >= 0;
}
