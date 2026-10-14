using System;
using System.Collections.Generic;
using System.Linq;

namespace ArsanTupTakip.Services;

public sealed record CustomerDelivery(int CustomerId, string CustomerName, DateTime DeliveredAt);

public sealed record ReorderForecast(
    int CustomerId,
    string CustomerName,
    int DeliveryDays,
    int TypicalIntervalDays,
    DateTime LastDelivery,
    DateTime ExpectedNext,
    int DaysUntilExpected)
{
    public bool IsOverdue => DaysUntilExpected < 0;

    public string Timing => DaysUntilExpected switch
    {
        0 => "Bugün bekleniyor",
        > 0 => $"{DaysUntilExpected} gün sonra",
        _ => $"{-DaysUntilExpected} gün gecikti"
    };
}

public static class ReorderPrediction
{
    // Fewer delivery days than this give an interval that is mostly noise.
    public const int MinimumDeliveryDays = 3;

    // Older deliveries say little about today's ordering rhythm and would make the query scan years of history.
    public const int HistoryDays = 730;

    public static List<ReorderForecast> Compute(IEnumerable<CustomerDelivery> deliveries, DateTime today)
    {
        List<ReorderForecast> forecasts = [];
        foreach (IGrouping<int, CustomerDelivery> customer in deliveries.GroupBy(delivery => delivery.CustomerId))
        {
            // Several cylinders delivered on the same day count as one order.
            List<DateTime> days = customer.Select(delivery => delivery.DeliveredAt.Date).Distinct().Order().ToList();
            if (days.Count < MinimumDeliveryDays)
            {
                continue;
            }

            List<int> intervals = days.Zip(days.Skip(1), (previous, next) => (next - previous).Days).Order().ToList();
            int median = intervals[intervals.Count / 2];
            if (intervals.Count % 2 == 0)
            {
                median = (int)Math.Round((intervals[intervals.Count / 2 - 1] + median) / 2.0, MidpointRounding.AwayFromZero);
            }

            DateTime last = days[^1];
            DateTime expected = last.AddDays(Math.Max(median, 1));
            string name = customer.Select(delivery => delivery.CustomerName).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))
                ?? $"Müşteri #{customer.Key}";
            forecasts.Add(new ReorderForecast(customer.Key, name, days.Count, Math.Max(median, 1), last, expected, (expected - today.Date).Days));
        }

        return forecasts.OrderBy(forecast => forecast.DaysUntilExpected).ThenBy(forecast => forecast.CustomerName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
