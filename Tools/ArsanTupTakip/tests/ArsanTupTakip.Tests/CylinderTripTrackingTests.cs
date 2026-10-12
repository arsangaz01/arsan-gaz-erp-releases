using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class CylinderTripTrackingTests
{
    internal static void Run()
    {
        ReconcileFlagsMissingAndUnexpected();
    }

    private static void ReconcileFlagsMissingAndUnexpected()
    {
        List<TripCylinderSnapshot> loaded =
        [
            new(1, "A1", "Oksijen", "01 ABC 1", null, null),
            new(2, "A2", "Oksijen", "01 ABC 1", null, null),
            new(3, "A3", "Argon", "01 ABC 1", DeliveredAt: DateTime.Now, null)
        ];
        Dictionary<string, int> map = new(StringComparer.OrdinalIgnoreCase)
        {
            ["A1"] = 1,
            ["A2"] = 2,
            ["A9"] = 9
        };
        TripReconcileResult result = CylinderTripTracking.Reconcile(loaded, ["A1", "A9"], map, "01 ABC 1");
        Assert(result.NotDeliveredNotReturned.Contains("A2"), "Undelivered and not returned cylinder is flagged.");
        Assert(result.UnexpectedReturnCodes.Contains("A9"), "Return scan outside load list is unexpected.");
        Assert(result.ShortagesByGas.Any(x => x.GasType == "Oksijen" && x.Count == 1), "Gas type shortage is counted.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
