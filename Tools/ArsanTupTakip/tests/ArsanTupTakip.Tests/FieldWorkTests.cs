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
        LocationParsing();
        Console.WriteLine("PASS: customer exchange balance, filling rules (customer / scrap / overdue test), trip route order and map link, customer location parsing (offline).");
    }

    private static void LocationParsing()
    {
        static bool Near((double Latitude, double Longitude)? found, double lat, double lng) =>
            found is { } f && Math.Abs(f.Latitude - lat) < 0.00001 && Math.Abs(f.Longitude - lng) < 0.00001;

        Assert(Near(TripPlans.Parse("37.00123, 35.32111"), 37.00123, 35.32111), "Plain coordinates with a dot.");
        Assert(Near(TripPlans.Parse("37,00123 35,32111"), 37.00123, 35.32111), "Turkish decimal commas separated by a space.");
        Assert(Near(TripPlans.Parse("37,00123,35,32111"), 37.00123, 35.32111), "Turkish decimal commas separated by a comma.");
        Assert(Near(TripPlans.Parse("https://www.google.com/maps/place/Arsan+Gaz/@36.9912,35.3301,17z/data=!3m1!4b1!4m6!3m5!1s0x0:0x0!8m2!3d36.99123!4d35.33456"),
            36.99123, 35.33456), "The place pin (!3d!4d) wins over the map centre (@).");
        Assert(Near(TripPlans.Parse("https://www.google.com/maps/@36.9912,35.3301,17z"), 36.9912, 35.3301), "Map centre link.");
        Assert(Near(TripPlans.Parse("https://maps.google.com/?q=36.98765,35.12345"), 36.98765, 35.12345), "q= link.");
        Assert(Near(TripPlans.Parse("https://www.google.com/maps/search/?api=1&query=36.98765%2C35.12345"), 36.98765, 35.12345), "Encoded query= link.");
        Assert(Near(TripPlans.Parse("https://www.google.com/maps/search/36.98765,+35.12345?entry=tts"), 36.98765, 35.12345), "/search/lat,+lng link.");
        Assert(Near(TripPlans.Parse("37°00'04.4\"N 35°19'16.0\"E"), 37.001222, 35.321111), "Degrees, minutes, seconds.");
        Assert(TripPlans.Parse("Atatürk Cad. No:5 Seyhan") is null && TripPlans.Parse("") is null, "Text without coordinates gives nothing.");
        Assert(TripPlans.Parse("https://maps.app.goo.gl/AbCdEf123") is null, "A short link needs to be opened first.");
        Assert(TripPlans.IsShortLink(new Uri("https://maps.app.goo.gl/AbCdEf123")) && !TripPlans.IsShortLink(new Uri("https://example.com/x")), "Only Google short links are followed.");
        Assert(TripPlans.InTurkey(37.0, 35.3) && !TripPlans.InTurkey(35.3, 37.0 + 20) && !TripPlans.InTurkey(51.5, -0.1), "Turkey bounds.");
        double d = TripPlans.DistanceMeters(37.0, 35.0, 37.0027, 35.0);
        Assert(d > 290 && d < 310, $"About 300 m between points 0.0027° of latitude apart (was {d:0}).");
        Assert(TripRoutes.CoordinateMapUrl(37.001234, 35.321) == "https://www.google.com/maps/dir/?api=1&destination=37.001234,35.321", "Coordinate map link.");
        Assert(CustomerGeocoder.Mentions("Kurttepe, 01370 Çukurova/Adana, Türkiye", "ÇUKUROVA") && CustomerGeocoder.Mentions("Seyhan/Adana", "adana"),
            "Google's address matches the customer's district and city regardless of case and Turkish letters.");
        Assert(CustomerGeocoder.Mentions("Sarıçam/Adana", "SARIÇAM") && CustomerGeocoder.Mentions("İstanbul", "istanbul") && CustomerGeocoder.Mentions("Istanbul", "İSTANBUL"),
            "Dotted and dotless I fold the same way.");
        Assert(!CustomerGeocoder.Mentions("Akdeniz/Mersin", "Adana") && !CustomerGeocoder.Mentions("Seyhan/Adana", ""), "Another city or an empty part does not match.");
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
