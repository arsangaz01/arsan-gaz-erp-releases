using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ArsanGazERP.Services;

public sealed record CylinderStatusCount(int Status, string Name, int Total, int Full, int Empty);

public sealed record CylinderPlateCount(string Plate, int Count);

public sealed record CylinderOverview(IReadOnlyList<CylinderStatusCount> Statuses, IReadOnlyList<CylinderPlateCount> Vehicles, int LongAtCustomer, int LongAtCustomerDays);

public sealed record CylinderListItem(
    string SerialNumber,
    string Barcode,
    string GasType,
    string Capacity,
    int Status,
    string StatusName,
    string Fullness,
    string? Customer,
    string? Plate,
    int? DaysThere,
    string? TestDue);

public sealed record CylinderBulkResult(string Code, bool Ok, string Message);

public sealed record CylinderAuditComparison(IReadOnlyList<int> Found, IReadOnlyList<int> Missing, IReadOnlyList<int> Unexpected);

public sealed record CylinderAuditReport(int AuditId, string Customer, int Expected, int Found, IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected, IReadOnlyList<string> Unknown)
{
    public string Text
    {
        get
        {
            List<string> lines = [$"{Customer} sayımı: sistemde {Expected} tüp, sahada {Found} tanesi bulundu."];
            lines.Add(Missing.Count == 0 ? "Eksik yok." : $"Eksik ({Missing.Count}) – sistemde müşteride görünüyor ama okutulmadı: {string.Join(", ", Missing)}");
            if (Unexpected.Count > 0)
            {
                lines.Add($"Fazla ({Unexpected.Count}) – sahada var ama sistemde başka yerde: {string.Join(", ", Unexpected)}");
            }

            if (Unknown.Count > 0)
            {
                lines.Add($"Tanımsız kod ({Unknown.Count}): {string.Join(", ", Unknown)}");
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed record CylinderCustomerConflict(
    string SerialNumber,
    string CurrentCustomer,
    DateTime? Since,
    int? Days,
    bool? IsFull,
    string TargetCustomer,
    string? Code = null)
{
    public string Text
    {
        get
        {
            string since = Since is DateTime date ? $"{date:dd.MM.yyyy}" + (Days switch { 0 => " (bugün)", int days => $" ({days} gündür)", null => string.Empty }) : "tarih bilinmiyor";
            List<string> lines =
            [
                $"⚠ {SerialNumber} tüpü sistemde hâlâ {CurrentCustomer} müşterisinde görünüyor.",
                $"• {CurrentCustomer} müşterisinde: {since}" + (IsFull is null ? string.Empty : $", son bilinen: {CylinderTracking.FullnessName(IsFull)}"),
                $"• Şimdi {TargetCustomer} müşterisine teslim edilmek isteniyor.",
                $"Olası nedenler: tüp {CurrentCustomer} müşterisinden iade alındı ama iade kaydedilmedi, yanlış tüp okutuldu veya tüp müşteriler arasında el değiştirdi.",
                $"Devam ederseniz tüp {CurrentCustomer} kaydından düşülür ve {TargetCustomer} müşterisine yazılır; {CurrentCustomer} için ayrıca iade hareketi oluşmaz."
            ];
            return string.Join(Environment.NewLine, lines);
        }
    }
}

// Thrown when a movement would silently take a cylinder away from the customer it is registered to.
public sealed class CylinderConflictException(IReadOnlyList<CylinderCustomerConflict> conflicts)
    : InvalidOperationException(string.Join(Environment.NewLine + Environment.NewLine, conflicts.Select(c => c.Text)))
{
    public IReadOnlyList<CylinderCustomerConflict> Conflicts { get; } = conflicts;
}

// The record changed between reading and saving (another user or device saved first); nothing was written.
public sealed class StaleRecordException() : InvalidOperationException(DefaultMessage)
{
    public const string DefaultMessage = "Bu kayıt başka bir kullanıcı tarafından değiştirildi, yenileyip tekrar deneyin.";
}

public static class CylinderTracking
{
    public const int Warehouse = 1;
    public const int Vehicle = 2;
    public const int Customer = 3;
    public const int Filling = 4;
    public const int Service = 5;
    public const int Scrap = 6;
    public const int LongAtCustomerDays = 60;
    public const int MaxBatchSize = 200;

    private static readonly Regex PlatePattern = new(@"^(\d{2})\s*([A-ZÇĞİÖŞÜ]{1,3})\s*(\d{2,4})$", RegexOptions.CultureInvariant);

    public static IReadOnlyList<int> AllStatuses { get; } = [Warehouse, Vehicle, Customer, Filling, Service, Scrap];

    public static string StatusName(int status) => status switch
    {
        Warehouse => "Depoda",
        Vehicle => "Taşınıyor (araçta)",
        Customer => "Müşteride",
        Filling => "Dolumda",
        Service => "Serviste",
        Scrap => "Hurda",
        _ => $"Durum {status}"
    };

    public static string FullnessName(bool? isFull) => isFull switch
    {
        true => "Dolu",
        false => "Boş",
        null => "-"
    };

    // A delivered cylinder is full, one coming back from a customer is empty and one leaving the filling station is full,
    // unless the user states otherwise.
    public static bool? NextFullness(int from, int to, bool? current, bool? requested)
    {
        if (requested.HasValue)
        {
            return requested;
        }

        if (to == Customer)
        {
            return true;
        }

        if (from == Customer)
        {
            return false;
        }

        if (from == Filling && to != Filling)
        {
            return true;
        }

        return current;
    }

    public static string? NormalizePlate(string? value)
    {
        string text = (value ?? string.Empty).Trim().ToUpperInvariant();
        if (text.Length == 0)
        {
            return null;
        }

        Match match = PlatePattern.Match(text.Replace("-", " "));
        if (!match.Success)
        {
            throw new ArgumentException($"'{value}' geçerli bir plaka değil. Örnek: 01 ABC 123.");
        }

        return $"{match.Groups[1].Value} {match.Groups[2].Value} {match.Groups[3].Value}";
    }

    public static IReadOnlyList<string> CleanCodes(IEnumerable<string?>? codes) =>
        (codes ?? [])
            .Select(code => (code ?? string.Empty).Trim())
            .Where(code => code.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static CylinderAuditComparison CompareAudit(IEnumerable<int> expectedIds, IEnumerable<int> scannedIds)
    {
        HashSet<int> expected = [.. expectedIds];
        HashSet<int> scanned = [.. scannedIds];
        return new CylinderAuditComparison(
            expected.Where(scanned.Contains).Order().ToList(),
            expected.Where(id => !scanned.Contains(id)).Order().ToList(),
            scanned.Where(id => !expected.Contains(id)).Order().ToList());
    }

    public static int? DaysSince(DateTime? value, DateTime today) =>
        value is DateTime date ? Math.Max(0, (today.Date - date.Date).Days) : null;

    // A cylinder registered to one customer is about to be delivered to a different one.
    public static CylinderCustomerConflict? CustomerConflict(
        string serialNumber,
        int currentStatus,
        int? currentCustomerId,
        string? currentCustomerName,
        DateTime? lastMovementAt,
        bool? isFull,
        int targetStatus,
        int? targetCustomerId,
        string? targetCustomerName,
        DateTime today)
    {
        if (currentStatus != Customer || currentCustomerId is null || targetStatus != Customer || targetCustomerId == currentCustomerId)
        {
            return null;
        }

        return new CylinderCustomerConflict(
            serialNumber,
            string.IsNullOrWhiteSpace(currentCustomerName) ? $"#{currentCustomerId}" : currentCustomerName.Trim(),
            lastMovementAt,
            DaysSince(lastMovementAt, today),
            isFull,
            string.IsNullOrWhiteSpace(targetCustomerName) ? $"#{targetCustomerId}" : targetCustomerName.Trim());
    }

    public static IReadOnlyList<string> CommonGasTypes { get; } =
        ["Oksijen", "Medikal Oksijen", "Azot", "Argon", "Karbondioksit", "Argon/CO2 karışım", "Asetilen", "Helyum", "Hidrojen", "Kuru hava"];

    public const int MaxCodeLength = 100;

    // Returns the reason a new cylinder cannot be registered, or null when the input is acceptable.
    public static string? NewCylinderError(string? serialNumber, string? barcode, string? gasType, decimal capacity)
    {
        string serial = (serialNumber ?? string.Empty).Trim();
        if (serial.Length == 0)
        {
            return "Tüp seri numarası zorunludur.";
        }

        if (serial.Length > MaxCodeLength || (barcode ?? string.Empty).Trim().Length > MaxCodeLength)
        {
            return $"Seri numarası ve barkod en fazla {MaxCodeLength} karakter olabilir.";
        }

        if (string.IsNullOrWhiteSpace(gasType))
        {
            return "Gaz türü zorunludur.";
        }

        return capacity <= 0 ? "Tüp kapasitesi sıfırdan büyük olmalıdır." : null;
    }
}
