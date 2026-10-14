using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class StockCountTests
{
    internal static void Run()
    {
        ComparesDepotCountByGasAndSize();
        NamesPlaces();
    }

    private static CountCylinder C(int id, string gas, string capacity, bool? full = true, string place = "Depoda") =>
        new(id, "S-" + id, "B-" + id, gas, capacity, full, place);

    private static void ComparesDepotCountByGasAndSize()
    {
        List<CountCylinder> expected = [C(1, "ARGON", "50 Litre"), C(2, "argon", "50 Litre", false), C(3, "ARGON", "10 Litre"), C(4, "OKSİJEN", "40 Litre")];
        List<(CountCylinder, bool)> scanned =
        [
            (C(1, "ARGON", "50 Litre"), true),
            (C(1, "ARGON", "50 Litre"), true),
            (C(3, "ARGON", "10 Litre"), true),
            (C(9, "ARGON", "50 Litre", false, "Müşteride · X"), false)
        ];
        StockCountComparison result = StockCounting.Compare(expected, scanned);
        Assert(result.Matched.Select(x => x.Id).SequenceEqual([3, 1]), "Scans of warehouse cylinders match once each, sorted by gas and size.");
        Assert(result.Missing.Select(x => x.Id).Order().SequenceEqual([2, 4]), "Warehouse cylinders that were not scanned are missing.");
        Assert(result.Extra.Count == 1 && result.Extra[0].Place == "Müşteride · X", "A cylinder recorded elsewhere is reported with its place.");
        Assert(result.Groups.Select(x => x.Title).SequenceEqual(["ARGON 10 Litre", "ARGON 50 Litre", "OKSİJEN 40 Litre"]),
            "Groups are per gas and size, gas A→Z then size small to large.");
        StockCountGroup argon50 = result.Groups[1];
        Assert(argon50.Expected == 2 && argon50.Counted == 2 && argon50.Matched == 1 && argon50.Missing == 1 && argon50.Extra == 1,
            "Each group counts expected, counted, matched, missing and extra cylinders.");
        Assert(argon50.Full == 1 && argon50.Empty == 1, "Full and empty are counted among the scanned cylinders.");
        Assert(result.Groups[2].Counted == 0 && result.Groups[2].Missing == 1, "A group with nothing scanned still shows its missing cylinders.");
        Assert(StockCounting.Compare([], []).Groups.Count == 0, "An empty count has no groups.");
        Assert(StockCounting.GroupKey(" argon ", "50 Litre") == StockCounting.GroupKey("ARGON", "50 litre"), "Group keys ignore case and spaces.");
    }

    private static void NamesPlaces()
    {
        Assert(StockCounting.Place(3, "ABC Ltd", null) == "Müşteride · ABC Ltd", "Customer place names the customer.");
        Assert(StockCounting.Place(2, null, "01 ABC 123") == "Araçta · 01 ABC 123", "Vehicle place names the plate.");
        Assert(StockCounting.Place(1, null, null) == "Depoda", "Warehouse place.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
