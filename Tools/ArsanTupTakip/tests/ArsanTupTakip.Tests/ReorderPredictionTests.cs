using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class ReorderPredictionTests
{
    internal static void Run()
    {
        UsesMedianIntervalAndFlagsOverdueCustomers();
        CountsSameDayDeliveriesOnceAndSkipsThinHistory();
        Console.WriteLine("PASS: reorder prediction median interval, overdue ordering, same-day grouping and minimum history (offline).");
    }

    private static void UsesMedianIntervalAndFlagsOverdueCustomers()
    {
        DateTime today = new(2026, 10, 10);
        List<CustomerDelivery> deliveries =
        [
            new(1, "Düzenli", new DateTime(2026, 9, 1)),
            new(1, "Düzenli", new DateTime(2026, 9, 15)),
            new(1, "Düzenli", new DateTime(2026, 9, 29)),
            new(1, "Düzenli", new DateTime(2026, 10, 1)),
            new(2, "Gecikmiş", new DateTime(2026, 8, 1)),
            new(2, "Gecikmiş", new DateTime(2026, 8, 11)),
            new(2, "Gecikmiş", new DateTime(2026, 8, 21))
        ];

        List<ReorderForecast> forecasts = ReorderPrediction.Compute(deliveries, today);

        Assert(forecasts.Count == 2, "Both customers with three delivery days must be forecast.");
        Assert(forecasts[0].CustomerName == "Gecikmiş" && forecasts[0].IsOverdue, "Overdue customers must be listed first.");
        Assert(forecasts[0].TypicalIntervalDays == 10 && forecasts[0].ExpectedNext == new DateTime(2026, 8, 31), "A regular 10-day rhythm must predict the next delivery 10 days after the last one.");
        Assert(forecasts[1].TypicalIntervalDays == 14, "The median of 14, 14 and 2 days must ignore the one-off short gap.");
        Assert(forecasts[1].ExpectedNext == new DateTime(2026, 10, 15) && forecasts[1].DaysUntilExpected == 5, "The expected date must be counted from the last delivery.");
    }

    private static void CountsSameDayDeliveriesOnceAndSkipsThinHistory()
    {
        DateTime today = new(2026, 10, 10);
        List<CustomerDelivery> deliveries =
        [
            new(3, "Toplu", new DateTime(2026, 9, 1, 9, 0, 0)),
            new(3, "Toplu", new DateTime(2026, 9, 1, 9, 5, 0)),
            new(3, "Toplu", new DateTime(2026, 9, 1, 15, 0, 0)),
            new(3, "Toplu", new DateTime(2026, 9, 8)),
            new(4, "Yeni", new DateTime(2026, 9, 20)),
            new(4, "Yeni", new DateTime(2026, 9, 27))
        ];

        List<ReorderForecast> forecasts = ReorderPrediction.Compute(deliveries, today);

        Assert(forecasts.Count == 0, "Several cylinders on one day are one order; two delivery days are not enough history.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
