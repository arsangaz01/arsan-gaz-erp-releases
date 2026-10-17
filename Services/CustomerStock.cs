using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record CustomerStockCylinder(
    int Id,
    int CustomerId,
    string SerialNumber,
    string Barcode,
    string? ManufacturerSerial,
    string GasType,
    decimal Capacity,
    string CapacityUnit,
    bool? IsFull,
    DateTime? Since,
    bool CustomerOwned,
    DateTime? TestDueDate,
    bool NeedsReview);

public sealed record CustomerStockGroup(string GasType, string Capacity, int Count, int Full, int Empty, int? MinDays, int? MaxDays, int? AvgDays, int Long);

public sealed record CustomerStockTotals(int Count, int Full, int Empty, int Unknown, int CustomerOwned, int? MaxDays, int? AvgDays, int Long, DateTime? LastDelivery);

// Stock held at customers: what they hold, how many of each gas/size and for how long.
public static class CustomerStock
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string CapacityText(decimal capacity, string unit) => $"{capacity.ToString("0.##", Turkish)} {unit}".Trim();

    public static CustomerStockTotals Totals(IReadOnlyCollection<CustomerStockCylinder> rows, DateTime today, int longDays)
    {
        List<int> days = DaysOf(rows, today);
        return new CustomerStockTotals(
            rows.Count,
            rows.Count(c => c.IsFull == true),
            rows.Count(c => c.IsFull == false),
            rows.Count(c => c.IsFull is null),
            rows.Count(c => c.CustomerOwned),
            days.Count == 0 ? null : days.Max(),
            days.Count == 0 ? null : (int)Math.Round(days.Average()),
            days.Count(d => d >= longDays),
            rows.Max(c => c.Since));
    }

    public static IReadOnlyList<CustomerStockGroup> Groups(IEnumerable<CustomerStockCylinder> rows, DateTime today, int longDays) =>
        rows.GroupBy(c => (Gas: c.GasType.Trim(), Capacity: CapacityText(c.Capacity, c.CapacityUnit)))
            .Select(g =>
            {
                List<int> days = DaysOf(g, today);
                return new CustomerStockGroup(
                    g.Key.Gas,
                    g.Key.Capacity,
                    g.Count(),
                    g.Count(c => c.IsFull == true),
                    g.Count(c => c.IsFull == false),
                    days.Count == 0 ? null : days.Min(),
                    days.Count == 0 ? null : days.Max(),
                    days.Count == 0 ? null : (int)Math.Round(days.Average()),
                    days.Count(d => d >= longDays));
            })
            .OrderByDescending(g => g.Count)
            .ThenBy(g => g.GasType, StringComparer.Create(Turkish, true))
            .ThenBy(g => g.Capacity, StringComparer.Ordinal)
            .ToList();

    public static string GroupText(IEnumerable<CustomerStockGroup> groups) =>
        string.Join(", ", groups.Select(g => $"{g.GasType} {g.Capacity} ×{g.Count}"));

    public static async Task<List<CustomerStockCylinder>> LoadAsync(int? customerId = null)
    {
        await using ArsanGazDbContext db = new();
        IQueryable<Cylinder> query = db.Cylinders.AsNoTracking()
            .Where(c => c.IsActive && c.Status == CylinderStatus.Customer && c.CurrentCustomerId != null);
        if (customerId is int id)
        {
            query = query.Where(c => c.CurrentCustomerId == id);
        }

        return await query
            .OrderBy(c => c.LastMovementAt)
            .Select(c => new CustomerStockCylinder(
                c.Id, c.CurrentCustomerId!.Value, c.SerialNumber, c.Barcode, null, c.GasType, c.Capacity, c.CapacityUnit,
                c.IsFull, c.LastMovementAt, c.OwnerCustomerId != null, c.TestDueDate, false))
            .ToListAsync();
    }

    private static List<int> DaysOf(IEnumerable<CustomerStockCylinder> rows, DateTime today) =>
        rows.Select(c => CylinderTracking.DaysSince(c.Since, today)).OfType<int>().ToList();
}
