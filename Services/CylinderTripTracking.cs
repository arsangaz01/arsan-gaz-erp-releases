using System;
using System.Collections.Generic;
using System.Linq;

namespace ArsanGazERP.Services;

public sealed record TripCylinderSnapshot(
    int CylinderId,
    string SerialNumber,
    string GasType,
    string VehiclePlate,
    DateTime? DeliveredAt,
    DateTime? ReturnedAt,
    string? MissingReason = null,
    bool IsPickup = false);

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
        new("musteride-kaldi", "Müşteride bırakıldı (boş alınmadı / yanlış okutuldu)", true, true),
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
    IReadOnlyList<string>? Unexplained = null,
    IReadOnlyList<string>? MissingPickups = null)
{
    // Missing cylinders that nobody has written a reason for yet.
    public IReadOnlyList<string> UnexplainedMissing => Unexplained ?? NotDeliveredNotReturned;

    // Empties taken from customers that were not scanned back at the depot (also part of NotDeliveredNotReturned).
    public IReadOnlyList<string> MissingEmpties => MissingPickups ?? [];

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
            int fullCount = NotDeliveredNotReturned.Count - MissingEmpties.Count;
            if (fullCount > 0)
            {
                lines.Add($"⚠ Dolu yüklenip ne teslim ne de depoya geri okutulan {fullCount} tüp: "
                    + string.Join(", ", NotDeliveredNotReturned.Except(MissingEmpties)));
            }

            if (MissingEmpties.Count > 0)
            {
                lines.Add($"⚠ Müşteriden boş alınıp depoya okutulmayan {MissingEmpties.Count} tüp: " + string.Join(", ", MissingEmpties));
            }

            if (ShortagesByGas.Count > 0)
            {
                lines.Add("Gaz türüne göre: " + string.Join(", ", ShortagesByGas.Select(s => $"{s.GasType} ×{s.Count}")));
                int explained = NotDeliveredNotReturned.Count - UnexplainedMissing.Count;
                lines.Add(UnexplainedMissing.Count > 0
                    ? $"⚠ Gerekçe seçilmemiş {UnexplainedMissing.Count} tüp: {string.Join(", ", UnexplainedMissing)}"
                        + (explained > 0 ? $" ({explained} tüpün gerekçesi var)" : string.Empty)
                    : $"Okutulmayan {NotDeliveredNotReturned.Count} tüpün tamamına gerekçe seçilmiş.");
            }

            if (UnexpectedReturnCodes.Count > 0)
            {
                lines.Add("⚠ Dönüşte okutulan ama sefere yüklenmemiş / müşteriden alınmamış: " + string.Join(", ", UnexpectedReturnCodes));
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
    // Only full cylinders go out on a trip. A cylinder registered at a customer is refused too: it is at least partly
    // used, so it has to be scanned into the depot (and filled) first. Unknown fullness is accepted and recorded as full,
    // because older records have no fullness and the cylinder is physically in the depot being loaded.
    public static string? LoadRefusal(int status, bool? isFull) => status switch
    {
        CylinderTracking.Customer => "sistemde müşteride görünüyor; önce depoya giriş okutun, dolu değilse yüklenemez.",
        CylinderTracking.Scrap => "hurdaya ayrılmış tüp sefere yüklenemez.",
        CylinderTracking.Filling => null,
        _ when isFull == false => "BOŞ tüp sefere yüklenemez; yalnızca dolu tüpler yüklenir.",
        _ => null
    };

    // Fullness written when a cylinder is scanned back at the depot: a loaded cylinder that was not delivered comes back
    // full, an empty picked up from a customer comes back empty; anything else keeps the default rule.
    public static bool? ReturnFullness(bool onTrip, bool isPickup, bool delivered) =>
        !onTrip || delivered ? null : !isPickup;

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
        List<string> pickups = open.Where(x => x.IsPickup).Select(x => x.SerialNumber).ToList();

        List<TripGasShortage> byGas = open
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GasType) ? "Belirsiz" : x.GasType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TripGasShortage(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.GasType, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .ToList();

        return new TripReconcileResult(missing, byGas, unexpected, missing, unexplained, pickups);
    }
}

// One cylinder that came back unscanned when its trip was closed, as reported to the managers.
public sealed record TripMissingNotice(
    string SerialNumber, string GasType, string Plate, string? Driver, bool IsPickup, string? PickupCustomer,
    string? Reason, string? ReasonLabel, string? Note, string? ReasonBy);

public sealed record TripCloseResult(
    int TripId, TripReconcileResult Reconcile, string Message, string Title = "", string? ClosedBy = null,
    IReadOnlyList<TripMissingNotice>? Missing = null)
{
    public IReadOnlyList<TripMissingNotice> MissingCylinders => Missing ?? [];

    // Telegram/manager text; never contains customer phone numbers (only the customer name the cylinder was taken from).
    public string? ManagerNotice
    {
        get
        {
            if (MissingCylinders.Count == 0)
            {
                return null;
            }

            const int shown = 40;
            int empties = MissingCylinders.Count(x => x.IsPickup);
            List<string> lines =
            [
                $"🚚 Sefer kapandı, okutulmayan tüp var: {Title} (#{TripId})",
                $"Kapatan: {(string.IsNullOrWhiteSpace(ClosedBy) ? "-" : ClosedBy)}",
                $"{MissingCylinders.Count} tüp depoya geri okutulmadı ve teslim de edilmedi"
                    + (empties > 0 ? $" ({MissingCylinders.Count - empties} dolu yüklenen, {empties} müşteriden boş alınan)" : string.Empty) + ":"
            ];
            foreach (TripMissingNotice item in MissingCylinders.Take(shown))
            {
                string kind = item.IsPickup
                    ? "müşteriden boş alındı" + (string.IsNullOrWhiteSpace(item.PickupCustomer) ? string.Empty : $" ({item.PickupCustomer})")
                    : "dolu yüklendi";
                lines.Add($"• {item.SerialNumber} ({item.GasType}, {kind}) · araç {item.Plate} · sürücü {(string.IsNullOrWhiteSpace(item.Driver) ? "-" : item.Driver)}");
                lines.Add(item.Reason is null
                    ? "   Gerekçe: YOK"
                    : $"   Gerekçe: {item.ReasonLabel}" + (string.IsNullOrWhiteSpace(item.Note) ? string.Empty : $" · Açıklama: {item.Note}")
                        + (string.IsNullOrWhiteSpace(item.ReasonBy) ? string.Empty : $" · Yazan: {item.ReasonBy}"));
            }

            if (MissingCylinders.Count > shown)
            {
                lines.Add($"… ve {MissingCylinders.Count - shown} tüp daha.");
            }

            lines.Add("Kontrol: Tüp takip paneli > Seferler > Kayıp kontrolü (\"kontrol edildi\" işaretleyin).");
            return string.Join(Environment.NewLine, lines);
        }
    }
}

public sealed class TripReasonsRequiredException(IReadOnlyList<string> serials) : InvalidOperationException(
    $"Sefer kapatılamadı: okutulmayan {serials.Count} tüpe gerekçe seçilmeli (nerede / neden okutulmadı): {string.Join(", ", serials)}")
{
    public IReadOnlyList<string> Serials { get; } = serials;
}
