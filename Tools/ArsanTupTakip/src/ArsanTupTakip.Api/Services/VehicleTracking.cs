using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ArsanTupTakip.Services;

// One cylinder in a vehicle or trip load list, with the details shown when a gas type group is opened.
public sealed record LoadCylinderItem(
    int CylinderId,
    string SerialNumber,
    string Barcode,
    string GasType,
    string Capacity,
    bool? IsFull,
    string Fullness,
    string StatusName,
    string? Customer,
    string? Plate,
    DateTime? LastMovementAt,
    DateTime? TestDueDate,
    DateTime? LoadedAt,
    string State,
    bool Alarm);

public sealed record GasTypeGroup(string GasType, int Count, int Full, int Empty, int Alarm, IReadOnlyList<LoadCylinderItem> Cylinders);

public sealed record VehicleInput(string? Plate, string? Name, string? VehicleType, string? DriverName, int? CylinderCapacity, string? Note, bool? IsActive);

public static class VehicleTracking
{
    public const string UnknownGas = "Belirsiz";
    public const int MaxTextLength = 100;
    public const int MaxNoteLength = 500;
    public const int MaxCylinderCapacity = 5000;

    private static readonly StringComparer TurkishOrder = StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true);

    public static string GasKey(string? gasType) => string.IsNullOrWhiteSpace(gasType) ? UnknownGas : gasType.Trim();

    // Largest group first; inside a group the cylinders are listed by serial number.
    public static IReadOnlyList<GasTypeGroup> GroupByGas(IEnumerable<LoadCylinderItem> items) =>
        items
            .GroupBy(x => GasKey(x.GasType), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                List<LoadCylinderItem> cylinders = g.OrderBy(x => x.SerialNumber, TurkishOrder).ToList();
                return new GasTypeGroup(g.Key, cylinders.Count,
                    cylinders.Count(x => x.IsFull == true), cylinders.Count(x => x.IsFull == false), cylinders.Count(x => x.Alarm), cylinders);
            })
            .OrderByDescending(x => x.Count)
            .ThenBy(x => x.GasType, TurkishOrder)
            .ToList();

    public static string TripState(DateTime? deliveredAt, DateTime? returnedAt, bool missing) =>
        deliveredAt is not null ? "Müşteriye teslim edildi"
        : returnedAt is not null ? "Depoya döndü"
        : missing ? "Eksik: teslim/dönüş okutulmadı"
        : "Araçta";

    public static string GroupSummary(GasTypeGroup group)
    {
        List<string> parts = [$"{group.Count} tüp"];
        if (group.Full + group.Empty > 0)
        {
            parts.Add($"dolu {group.Full} · boş {group.Empty}");
        }

        if (group.Alarm > 0)
        {
            parts.Add($"⚠ {group.Alarm} eksik");
        }

        return string.Join(" · ", parts);
    }

    // Trims the free-text fields and returns the reason the input is rejected, or null when it can be saved.
    public static string? InputError(VehicleInput input)
    {
        if (new[] { input.Name, input.VehicleType, input.DriverName }.Any(x => (x ?? string.Empty).Trim().Length > MaxTextLength))
        {
            return $"Ad, tür ve şoför en fazla {MaxTextLength} karakter olabilir.";
        }

        if ((input.Note ?? string.Empty).Trim().Length > MaxNoteLength)
        {
            return $"Not en fazla {MaxNoteLength} karakter olabilir.";
        }

        return input.CylinderCapacity is int capacity && (capacity < 0 || capacity > MaxCylinderCapacity)
            ? $"Kapasite 0 ile {MaxCylinderCapacity} tüp arasında olmalıdır."
            : null;
    }

    public static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? CapacityWarning(int onBoard, int? capacity) =>
        capacity is int max && max > 0 && onBoard > max ? $"⚠ Araçta {onBoard} tüp var; kayıtlı kapasite {max} tüp." : null;
}
