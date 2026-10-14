using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class CustomerStockTests
{
    internal static void Run()
    {
        GroupsByGasAndSizeWithDays();
        Console.WriteLine("PASS: customer stock grouping by gas and size, full/empty counts, waiting days and long-wait counts (offline).");
    }

    private static void GroupsByGasAndSizeWithDays()
    {
        DateTime today = new(2026, 10, 14);
        List<CustomerStockCylinder> rows =
        [
            Cylinder(1, "SAF OKSİJEN", 10m, true, today.AddDays(-5)),
            Cylinder(2, "SAF OKSİJEN", 10m, false, today.AddDays(-100), owned: true),
            Cylinder(3, "SAF OKSİJEN", 10m, true, today.AddDays(-30)),
            Cylinder(4, "SAF OKSİJEN ", 40m, true, today.AddDays(-1)),
            Cylinder(5, "ARGON", 40m, null, null)
        ];

        IReadOnlyList<CustomerStockGroup> groups = CustomerStock.Groups(rows, today, 60);
        Assert(groups.Count == 3, "The same gas in two sizes must be two rows; trailing spaces must not split a gas type.");
        CustomerStockGroup small = groups[0];
        Assert(small.GasType == "SAF OKSİJEN" && small.Capacity == "10 Litre" && small.Count == 3, "The largest group must come first.");
        Assert(small.Full == 2 && small.Empty == 1, "Full and empty must be counted per group.");
        Assert(small.MinDays == 5 && small.MaxDays == 100 && small.AvgDays == 45 && small.Long == 1, "Waiting days must be measured from the delivery date.");
        Assert(groups.Single(g => g.GasType == "ARGON").MaxDays is null, "A cylinder without a delivery date has no waiting days.");

        CustomerStockTotals totals = CustomerStock.Totals(rows, today, 60);
        Assert(totals.Count == 5 && totals.Full == 3 && totals.Empty == 1 && totals.Unknown == 1 && totals.CustomerOwned == 1, "Totals must add up.");
        Assert(totals.MaxDays == 100 && totals.Long == 1 && totals.LastDelivery == today.AddDays(-1), "Totals must show the longest wait and the last delivery.");
        Assert(CustomerStock.GroupText(groups).StartsWith("SAF OKSİJEN 10 Litre ×3", StringComparison.Ordinal), "The one-line summary must list the largest group first.");
    }

    private static CustomerStockCylinder Cylinder(int id, string gas, decimal capacity, bool? full, DateTime? since, bool owned = false) =>
        new(id, 7, "S" + id, "B" + id, null, gas, capacity, "Litre", full, since, owned, null, false);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
