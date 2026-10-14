using System;
using System.Collections.Generic;
using System.Linq;

namespace ArsanTupTakip.Services;

public sealed record TripCylinderSnapshot(
    int CylinderId,
    string SerialNumber,
    string GasType,
    string VehiclePlate,
    DateTime? DeliveredAt,
    DateTime? ReturnedAt,
    string? MissingReason = null);

public sealed record TripGasShortage(string GasType, int Count);

// AsksCustomer: the driver picks the customer (or writes it) instead of a free note.
public sealed record TripMissingReason(string Key, string Label, bool NoteRequired, bool AsksCustomer = false);

// Where a cylinder is (and why it was not scanned) when it was neither delivered nor scanned back at the depot. A reason
// only explains the gap; the cylinder itself is not moved, so the stock stays where the scans left it until someone
// scans or moves it.
public static class TripMissingReasons
{
    public const int MaxNoteLength = 500;

    public static readonly IReadOnlyList<TripMissingReason> All =
    [
        new("musteride-okutulmadi", "Müşteriye teslim edildi, okutulmadı", true, true),
        new("musteride-kaldi", "Müşteride bırakıldı (yanlış müşteri / yanlış okutuldu)", true, true),
        new("arac-icinde", "Araçta kaldı, depoya indirilmedi", false),
        new("depoda-okutulmadi", "Depoya indirildi ama okutulmadı", false),
        new("barkod-okunmadi", "Barkod/etiket okunamadı (hasarlı)", false),
        new("baska-araca", "Başka araca aktarıldı", true),
        new("hasarli-servis", "Hasarlı / servise veya dolumcuya bırakıldı", true),
        new("kayip", "Kayıp / bulunamadı", true),
        new("diger", "Diğer", true)
    ];

    public static TripMissingReason? Find(string? key) =>
        All.FirstOrDefault(x => string.Equals(x.Key, key?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string Label(string? key) => Find(key)?.Label ?? (string.IsNullOrWhiteSpace(key) ? string.Empty : key.Trim());

    // Returns the validated reason and the note to store ("Müşteri: X · note" when a customer was picked), or throws
    // with a message for the user.
    public static (TripMissingReason Reason, string? Note) Validate(string? key, string? note, string? customerName = null)
    {
        TripMissingReason reason = Find(key) ?? throw new ArgumentException("Geçerli bir gerekçe seçin.");
        string? clean = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        string? customer = reason.AsksCustomer && !string.IsNullOrWhiteSpace(customerName) ? customerName.Trim() : null;
        if (customer is not null)
        {
            clean = "Müşteri: " + customer + (clean is null ? string.Empty : " · " + clean);
        }

        if (reason.NoteRequired && clean is null)
        {
            throw new ArgumentException(reason.AsksCustomer
                ? $"\"{reason.Label}\" için müşteriyi seçin veya açıklamaya yazın."
                : $"\"{reason.Label}\" için açıklama yazın (araç, yer veya ayrıntı).");
        }

        if (clean is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"Açıklama en fazla {MaxNoteLength} karakter olabilir.");
        }

        return (reason, clean);
    }
}

public sealed record TripReconcileResult(
    IReadOnlyList<string> NotDeliveredNotReturned,
    IReadOnlyList<TripGasShortage> ShortagesByGas,
    IReadOnlyList<string> UnexpectedReturnCodes,
    IReadOnlyList<string> LoadedButNotAccountedFor,
    IReadOnlyList<string>? Unexplained = null)
{
    // Missing cylinders that nobody has written a reason for yet.
    public IReadOnlyList<string> UnexplainedMissing => Unexplained ?? NotDeliveredNotReturned;

    public bool HasAlerts => NotDeliveredNotReturned.Count > 0 || UnexpectedReturnCodes.Count > 0 || LoadedButNotAccountedFor.Count > 0;

    public string AlertText
    {
        get
        {
            if (!HasAlerts)
            {
                return "Sefer tüpleri tutarlı görünüyor.";
            }

            List<string> lines = [];
            if (ShortagesByGas.Count > 0)
            {
                lines.Add("⚠ Okutulmamış (müşteriye teslim ve depo dönüşü yok): "
                    + string.Join(", ", ShortagesByGas.Select(s => $"{s.GasType} ×{s.Count}")));
                int explained = NotDeliveredNotReturned.Count - UnexplainedMissing.Count;
                lines.Add(UnexplainedMissing.Count > 0
                    ? $"⚠ Gerekçe seçilmemiş {UnexplainedMissing.Count} tüp: {string.Join(", ", UnexplainedMissing)}"
                        + (explained > 0 ? $" ({explained} tüpün gerekçesi var)" : string.Empty)
                    : $"Okutulmayan {NotDeliveredNotReturned.Count} tüpün tamamına gerekçe seçilmiş.");
            }

            if (UnexpectedReturnCodes.Count > 0)
            {
                lines.Add("⚠ Dönüşte okutulan ama sefere yüklenmemiş: " + string.Join(", ", UnexpectedReturnCodes));
            }

            if (LoadedButNotAccountedFor.Count > 0 && ShortagesByGas.Count == 0)
            {
                lines.Add("⚠ Takip dışı yüklenen tüpler: " + string.Join(", ", LoadedButNotAccountedFor));
            }

            return string.Join(Environment.NewLine, lines);
        }
    }
}

public static class CylinderTripTracking
{
    public static TripReconcileResult Reconcile(
        IEnumerable<TripCylinderSnapshot> loaded,
        IEnumerable<string> returnScanCodes,
        IReadOnlyDictionary<string, int> codeToCylinderId,
        string? vehiclePlateFilter = null)
    {
        string? plate = string.IsNullOrWhiteSpace(vehiclePlateFilter) ? null : vehiclePlateFilter.Trim();
        List<TripCylinderSnapshot> rows = loaded
            .Where(x => plate is null || string.Equals(x.VehiclePlate, plate, StringComparison.OrdinalIgnoreCase))
            .ToList();

        HashSet<int> loadedIds = rows.Select(x => x.CylinderId).ToHashSet();
        List<string> unexpected = [];
        HashSet<int> returnIds = [];
        foreach (string code in CylinderTracking.CleanCodes(returnScanCodes))
        {
            if (!codeToCylinderId.TryGetValue(code, out int id))
            {
                unexpected.Add(code + " (kayıtsız)");
                continue;
            }

            if (!loadedIds.Contains(id))
            {
                unexpected.Add(code);
                continue;
            }

            returnIds.Add(id);
        }

        // A cylinder scanned in this return counts as returned even if the snapshot was taken before the scan was saved.
        List<TripCylinderSnapshot> open = rows
            .Where(x => x.DeliveredAt is null && x.ReturnedAt is null && !returnIds.Contains(x.CylinderId))
            .ToList();
        List<string> missing = open.Select(x => x.SerialNumber).ToList();
        List<string> unexplained = open.Where(x => string.IsNullOrWhiteSpace(x.MissingReason)).Select(x => x.SerialNumber).ToList();

        List<TripGasShortage> byGas = open
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GasType) ? "Belirsiz" : x.GasType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TripGasShortage(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.GasType, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .ToList();

        return new TripReconcileResult(missing, byGas, unexpected, missing, unexplained);
    }

    // Serial numbers that block closing: every cylinder that was neither delivered nor scanned back needs a "where / why"
    // reason, but only on trips started after reasons became mandatory (older open trips close as before).
    public static IReadOnlyList<string> UnexplainedForClose(bool requireReasons, IEnumerable<TripCylinderSnapshot> rows) =>
        !requireReasons
            ? []
            : rows.Where(x => x.DeliveredAt is null && x.ReturnedAt is null && string.IsNullOrWhiteSpace(x.MissingReason))
                .OrderBy(x => x.VehiclePlate, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.SerialNumber, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.SerialNumber)
                .ToList();

    public static void EnsureCanClose(bool requireReasons, IEnumerable<TripCylinderSnapshot> rows)
    {
        IReadOnlyList<string> unexplained = UnexplainedForClose(requireReasons, rows);
        if (unexplained.Count > 0)
        {
            throw new TripReasonsRequiredException(unexplained);
        }
    }
}

// One cylinder that came back unscanned when its trip was closed, as reported to the managers.
public sealed record TripMissingNotice(
    string SerialNumber, string GasType, string Plate, string? Driver, string? Reason, string? ReasonLabel, string? Note, string? ReasonBy);

public sealed record TripCloseResult(
    int TripId, TripReconcileResult Reconcile, string Message, string Title = "", string? ClosedBy = null,
    IReadOnlyList<TripMissingNotice>? Missing = null)
{
    public IReadOnlyList<TripMissingNotice> MissingCylinders => Missing ?? [];

    // Manager notice (Telegram via the bot's notice box and the panel). Only trip, vehicle, driver, cylinder and the
    // written reason: never customer phone numbers, and it is never sent to customers.
    public string? ManagerNotice
    {
        get
        {
            if (MissingCylinders.Count == 0)
            {
                return null;
            }

            const int shown = 40;
            int unexplained = MissingCylinders.Count(x => x.Reason is null);
            List<string> lines =
            [
                $"🚚 ArsanTüpTakip: sefer kapandı, okutulmayan tüp var: {Title} (#{TripId})",
                $"Kapatan: {(string.IsNullOrWhiteSpace(ClosedBy) ? "-" : ClosedBy)}",
                $"{MissingCylinders.Count} tüp ne müşteriye teslim ne de depoya geri okutuldu"
                    + (unexplained > 0 ? $" ({unexplained} tüpün gerekçesi yok)" : string.Empty) + ":"
            ];
            foreach (TripMissingNotice item in MissingCylinders.Take(shown))
            {
                lines.Add($"• {item.SerialNumber} ({(string.IsNullOrWhiteSpace(item.GasType) ? "gaz belirsiz" : item.GasType)}) · araç {item.Plate} · sürücü {(string.IsNullOrWhiteSpace(item.Driver) ? "-" : item.Driver)}");
                lines.Add(item.Reason is null
                    ? "   Gerekçe: YOK"
                    : $"   Gerekçe: {item.ReasonLabel}" + (string.IsNullOrWhiteSpace(item.Note) ? string.Empty : $" · Açıklama: {HidePhones(item.Note)}")
                        + (string.IsNullOrWhiteSpace(item.ReasonBy) ? string.Empty : $" · Yazan: {item.ReasonBy}"));
            }

            if (MissingCylinders.Count > shown)
            {
                lines.Add($"… ve {MissingCylinders.Count - shown} tüp daha.");
            }

            lines.Add("Kontrol: ArsanTüpTakip paneli > Seferler > Kayıp kontrolü (\"kontrol edildi\" işaretleyin).");
            return string.Join(Environment.NewLine, lines);
        }
    }

    // A driver may type a phone number into the free note; the manager notice leaves it out.
    internal static string HidePhones(string text) => PhonePattern.Replace(text, "[telefon gizlendi]");

    private static readonly System.Text.RegularExpressions.Regex PhonePattern = new(@"\+?\(?\d(?:[ \-()]*\d){9,}");
}

public sealed class TripReasonsRequiredException(IReadOnlyList<string> serials) : InvalidOperationException(
    $"Sefer kapatılamadı: okutulmayan {serials.Count} tüpe gerekçe seçilmeli (nerede / neden okutulmadı): {string.Join(", ", serials)}")
{
    public IReadOnlyList<string> Serials { get; } = serials;
}
