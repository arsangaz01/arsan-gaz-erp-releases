using ArsanTupTakip.Models;
using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class FieldWorkTests
{
    internal static void Run()
    {
        ExchangeBalanceText();
        FillRules();
        RouteOrderAndMapLink();
        Console.WriteLine("PASS: customer exchange balance, filling rules (customer / scrap / overdue test) and trip route order and map link (offline).");
    }

    private static void ExchangeBalanceText()
    {
        CustomerTripBalance even = new(7, "ACME", 3, 3, 10, 6, 4);
        Assert(even.Net == 0 && even.Text.Contains("(denk)") && even.Text.Contains("müşteride şimdi 10 tüp (6 dolu, 4 boş)"), "An even exchange must say so and show the holdings.");
        CustomerTripBalance more = new(7, "ACME", 5, 2, 12, 9, 2);
        Assert(more.Net == 3 && more.Text.Contains("müşteride +3") && more.Text.Contains("1 belirsiz"), "More delivered than collected must show the difference and unknown fullness.");
        CustomerTripBalance less = new(7, "ACME", 1, 4, 3, 1, 2);
        Assert(less.Text.Contains("müşteriden 3 fazla alındı"), "More collected than delivered must be explained.");
    }

    private static void FillRules()
    {
        DateTime today = new(2026, 10, 14);
        Assert(CylinderFills.Check(CylinderStatus.Warehouse, true, today.AddDays(30), today, false).Problem is null, "An empty cylinder in the depot with a valid test can be filled.");
        Assert(CylinderFills.Check(CylinderStatus.Customer, true, null, today, true).Problem is not null, "A cylinder registered at a customer must not be filled.");
        Assert(CylinderFills.Check(CylinderStatus.Scrap, true, null, today, true).Problem is not null, "A scrapped cylinder must not be filled.");
        Assert(CylinderFills.Check(CylinderStatus.Warehouse, false, null, today, true).Problem is not null, "An inactive cylinder must not be filled.");
        (string? problem, bool overdue) = CylinderFills.Check(CylinderStatus.Warehouse, true, today.AddDays(-1), today, false);
        Assert(problem is not null && overdue, "An overdue test must stop the fill and ask for confirmation.");
        Assert(CylinderFills.Check(CylinderStatus.Warehouse, true, today.AddDays(-1), today, true).Problem is null, "A confirmed overdue test may be filled.");
        Assert(CylinderFills.Check(CylinderStatus.Warehouse, true, today, today, false).Problem is null, "A test due today is not overdue yet.");
        Assert(CylinderFills.CleanLot("  L-1 ") == "L-1" && CylinderFills.CleanLot(" ") is null, "Lot numbers are trimmed; blank means none.");
        Assert(Throws(() => CylinderFills.CleanLot(new string('x', CylinderFills.MaxLotLength + 1))), "Too long lot numbers are refused.");
    }

    private static void RouteOrderAndMapLink()
    {
        Assert(TripRoutes.Move([1, 2, 3], 3, -1).SequenceEqual([1, 3, 2]), "Moving up swaps with the previous stop.");
        Assert(TripRoutes.Move([1, 2, 3], 1, 1).SequenceEqual([2, 1, 3]), "Moving down swaps with the next stop.");
        Assert(TripRoutes.Move([1, 2, 3], 1, -1).SequenceEqual([1, 2, 3]) && TripRoutes.Move([1, 2, 3], 3, 1).SequenceEqual([1, 2, 3]), "The ends stay put.");
        Assert(TripRoutes.Move([1, 2, 3], 9, 1).SequenceEqual([1, 2, 3]), "An unknown stop changes nothing.");
        string? url = TripRoutes.MapUrl("Atatürk Cad. No:5", "Seyhan", "Adana");
        Assert(url is not null && url.StartsWith("https://www.google.com/maps/dir/?api=1&destination=", StringComparison.Ordinal)
            && Uri.UnescapeDataString(url).EndsWith("Atatürk Cad. No:5, Seyhan, Adana, Türkiye", StringComparison.Ordinal), "The map link must carry the full address.");
        Assert(TripRoutes.MapUrl(null, " ", null) is null, "No address means no map link.");
        Assert(Uri.UnescapeDataString(TripRoutes.MapUrl(null, null, "Adana")!).EndsWith("Adana, Türkiye", StringComparison.Ordinal), "A city alone still gives a link.");

        DateTime today = new(2026, 10, 14);
        ReorderForecast Due(int days, int interval) => new(1, "A", 5, interval, today.AddDays(days - interval), today.AddDays(days), days);
        Assert(TripRoutes.IsSuggested(Due(1, 10)) && TripRoutes.IsSuggested(Due(-10, 10)), "Due soon or a little late is suggested.");
        Assert(!TripRoutes.IsSuggested(Due(5, 10)), "Not due yet is not suggested.");
        Assert(!TripRoutes.IsSuggested(Due(-725, 30)) && TripRoutes.IsSuggested(Due(-30, 30)) && !TripRoutes.IsSuggested(Due(-31, 30)), "Late by more than one cycle is dropped.");
    }

    private static bool Throws(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ArgumentException)
        {
            return true;
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
