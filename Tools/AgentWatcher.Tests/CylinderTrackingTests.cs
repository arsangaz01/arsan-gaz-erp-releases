using ArsanGazERP.Services;

namespace ArsanGazERP.AgentWatcher.Tests;

internal static class CylinderTrackingTests
{
    internal static void Run()
    {
        FullnessFollowsTheCylinderCycle();
        PlatesAreNormalized();
        AuditSplitsFoundMissingAndUnexpected();
        CodesAreCleaned();
        NewCylindersAreValidated();
    }

    private static void NewCylindersAreValidated()
    {
        Assert(CylinderTracking.NewCylinderError("AG-001", null, "Oksijen", 40m) is null, "A complete new cylinder is accepted.");
        Assert(CylinderTracking.NewCylinderError("  ", null, "Oksijen", 40m) is not null, "A serial number is required.");
        Assert(CylinderTracking.NewCylinderError("AG-001", null, " ", 40m) is not null, "A gas type is required.");
        Assert(CylinderTracking.NewCylinderError("AG-001", null, "Oksijen", 0m) is not null, "Capacity must be positive.");
        Assert(CylinderTracking.NewCylinderError("AG-001", new string('x', 101), "Oksijen", 40m) is not null, "Overlong barcodes are rejected.");
        Assert(CylinderTracking.CommonGasTypes.Contains("Oksijen"), "Common gas types are offered.");
    }

    private static void FullnessFollowsTheCylinderCycle()
    {
        Assert(CylinderTracking.NextFullness(CylinderTracking.Vehicle, CylinderTracking.Customer, false, null) == true, "A delivery leaves a full cylinder at the customer.");
        Assert(CylinderTracking.NextFullness(CylinderTracking.Customer, CylinderTracking.Warehouse, true, null) == false, "A cylinder returning from a customer is empty.");
        Assert(CylinderTracking.NextFullness(CylinderTracking.Customer, CylinderTracking.Vehicle, true, null) == false, "A cylinder collected onto a vehicle from a customer is empty.");
        Assert(CylinderTracking.NextFullness(CylinderTracking.Filling, CylinderTracking.Warehouse, false, null) == true, "A cylinder leaving the filling station is full.");
        Assert(CylinderTracking.NextFullness(CylinderTracking.Warehouse, CylinderTracking.Vehicle, null, null) is null, "Unknown fullness stays unknown on a plain transfer.");
        Assert(CylinderTracking.NextFullness(CylinderTracking.Warehouse, CylinderTracking.Vehicle, true, null) == true, "A plain transfer keeps the current fullness.");
        Assert(CylinderTracking.NextFullness(CylinderTracking.Customer, CylinderTracking.Warehouse, true, true) == true, "An explicit choice overrides the automatic rule.");
        Assert(CylinderTracking.StatusName(CylinderTracking.Vehicle) == "Taşınıyor (araçta)", "Vehicle status is shown as in transit.");
    }

    private static void PlatesAreNormalized()
    {
        Assert(CylinderTracking.NormalizePlate("01abc123") == "01 ABC 123", "Compact lowercase plates are formatted.");
        Assert(CylinderTracking.NormalizePlate(" 34 ab 1234 ") == "34 AB 1234", "Spaced plates are formatted.");
        Assert(CylinderTracking.NormalizePlate("06-çk-55") == "06 ÇK 55", "Turkish letters and dashes are accepted.");
        Assert(CylinderTracking.NormalizePlate("34 abi 12") == "34 ABI 12", "Plate letters are Latin; a lowercase i becomes I, not İ.");
        Assert(CylinderTracking.NormalizePlate("  ") is null, "An empty plate is allowed.");
        bool rejected = false;
        try
        {
            CylinderTracking.NormalizePlate("ABC");
        }
        catch (ArgumentException)
        {
            rejected = true;
        }

        Assert(rejected, "Text that is not a plate must be rejected.");
    }

    private static void AuditSplitsFoundMissingAndUnexpected()
    {
        CylinderAuditComparison result = CylinderTracking.CompareAudit([1, 2, 3], [3, 2, 9, 2]);
        Assert(result.Found.SequenceEqual([2, 3]), "Cylinders both expected and scanned are found.");
        Assert(result.Missing.SequenceEqual([1]), "Expected cylinders that were not scanned are missing.");
        Assert(result.Unexpected.SequenceEqual([9]), "Scanned cylinders recorded elsewhere are unexpected.");
    }

    private static void CodesAreCleaned()
    {
        IReadOnlyList<string> codes = CylinderTracking.CleanCodes([" A1 ", "a1", null, "", "B2"]);
        Assert(codes.SequenceEqual(["A1", "B2"]), "Codes are trimmed, blanks dropped and duplicates removed case-insensitively.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
