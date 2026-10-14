using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class VehicleTrackingTests
{
    internal static void Run()
    {
        GroupsByGasTypeWithCounts();
        TripStateNames();
        RejectsInvalidInput();
        WarnsWhenOverCapacity();
    }

    private static LoadCylinderItem Item(int id, string serial, string gas, bool? full, bool alarm = false, string capacity = "40 Litre") =>
        new(id, serial, serial, gas, capacity, full, CylinderTracking.FullnessName(full), "Taşınıyor (araçta)", null, "01 ABC 123",
            null, null, null, alarm ? "Eksik" : "Araçta", alarm);

    private static void GroupsByGasTypeWithCounts()
    {
        IReadOnlyList<GasTypeGroup> groups = VehicleTracking.GroupByGas(
        [
            Item(1, "OX-2", "Oksijen", true),
            Item(2, "OX-1", " oksijen ", false),
            Item(3, "OX-3", "Oksijen", true, alarm: true),
            Item(4, "AR-1", "Argon", true),
            Item(5, "X-1", "", null)
        ]);
        Assert(groups.Count == 3, "Gas types are grouped case and space insensitively.");
        GasTypeGroup oxygen = groups[0];
        Assert(oxygen.GasType == "Oksijen 40 Litre" && oxygen.Count == 3, "The largest group comes first with its gas and size.");
        Assert(oxygen.Full == 2 && oxygen.Empty == 1 && oxygen.Alarm == 1, "Full, empty and missing cylinders are counted per group.");
        Assert(oxygen.Cylinders.Select(x => x.SerialNumber).SequenceEqual(["OX-1", "OX-2", "OX-3"]), "Cylinders in a group are sorted by serial number.");
        Assert(groups.Any(x => x.GasType == VehicleTracking.UnknownGas + " 40 Litre" && x.Count == 1), "A cylinder without gas type is listed as unknown.");
        Assert(VehicleTracking.GroupSummary(oxygen).Contains("⚠ 1 eksik"), "The group summary mentions missing cylinders.");
        Assert(VehicleTracking.GroupByGas([]).Count == 0, "An empty load has no groups.");

        IReadOnlyList<GasTypeGroup> sized = VehicleTracking.GroupByGas(
        [
            Item(1, "AR-50a", "ARGON", true, capacity: "50 Litre"),
            Item(2, "AR-10a", "ARGON", true, capacity: "10 Litre"),
            Item(3, "AR-10b", "argon", false, capacity: "10 Litre"),
            Item(4, "AR-50b", "ARGON", true, capacity: "50 Litre"),
            Item(5, "AR-50c", "ARGON", true, capacity: "50 Litre"),
            Item(6, "OX-1", "OKSİJEN", true, capacity: "40 Litre"),
            Item(7, "EO-1", "ETİLENOKSİT", true, capacity: "0 Litre"),
            Item(8, "X-1", "ARGON", true, capacity: "-")
        ]);
        Assert(sized.Select(x => x.GasType).SequenceEqual(["ARGON", "ARGON 10 Litre", "ARGON 50 Litre", "ETİLENOKSİT", "OKSİJEN 40 Litre"]),
            "Each gas is split by size, sizes of a gas stay together from small to large and a missing size is left out of the name.");
        Assert(sized[1].Count == 2 && sized[2].Count == 3, "Each size group counts only its own cylinders.");
        Assert(sized[2].Cylinders.Select(x => x.SerialNumber).SequenceEqual(["AR-50a", "AR-50b", "AR-50c"]), "Every cylinder of a size group is listed.");
    }

    private static void TripStateNames()
    {
        Assert(VehicleTracking.TripState(DateTime.Now, null, false) == "Müşteriye teslim edildi", "Delivered state.");
        Assert(VehicleTracking.TripState(null, DateTime.Now, false) == "Depoya döndü", "Returned state.");
        Assert(VehicleTracking.TripState(null, null, true).StartsWith("Eksik", StringComparison.Ordinal), "Missing state.");
        Assert(VehicleTracking.TripState(null, null, false) == "Araçta", "Still on the vehicle.");
    }

    private static void RejectsInvalidInput()
    {
        Assert(VehicleTracking.InputError(new VehicleInput("01 ABC 123", "Kamyonet", "Panelvan", "Ali", 40, null, true)) is null, "Valid input is accepted.");
        Assert(VehicleTracking.InputError(new VehicleInput("01 ABC 123", null, null, null, -1, null, null)) is not null, "Negative capacity is rejected.");
        Assert(VehicleTracking.InputError(new VehicleInput("01 ABC 123", new string('a', 101), null, null, null, null, null)) is not null, "Too long name is rejected.");
        Assert(VehicleTracking.Clean("  ") is null && VehicleTracking.Clean(" Ali ") == "Ali", "Free text is trimmed and blank becomes null.");
    }

    private static void WarnsWhenOverCapacity()
    {
        Assert(VehicleTracking.CapacityWarning(41, 40) is not null, "Loading beyond capacity warns.");
        Assert(VehicleTracking.CapacityWarning(40, 40) is null, "Loading up to capacity is fine.");
        Assert(VehicleTracking.CapacityWarning(10, null) is null, "Without capacity there is no warning.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
