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
    DateTime? ReturnedAt);

public sealed record TripGasShortage(string GasType, int Count);

public sealed record TripReconcileResult(
    IReadOnlyList<string> NotDeliveredNotReturned,
    IReadOnlyList<TripGasShortage> ShortagesByGas,
    IReadOnlyList<string> UnexpectedReturnCodes,
    IReadOnlyList<string> LoadedButNotAccountedFor)
{
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

        List<TripGasShortage> byGas = open
            .GroupBy(x => string.IsNullOrWhiteSpace(x.GasType) ? "Belirsiz" : x.GasType.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => new TripGasShortage(g.Key, g.Count()))
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.GasType, StringComparer.Create(System.Globalization.CultureInfo.GetCultureInfo("tr-TR"), true))
            .ToList();

        return new TripReconcileResult(missing, byGas, unexpected, missing);
    }
}
