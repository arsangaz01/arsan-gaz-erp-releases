using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record CustomerRequestView(
    int Id, int CustomerId, string CustomerName, string? City, string? District, string Kind, string KindLabel, int? Quantity, string? Note,
    DateTime CreatedAt, string CreatedBy, string Status, string StatusLabel, DateTime? HandledAt, string? HandledBy, string? HandledNote);

// Requests from the customer portal and their handling by the staff.
public static class CustomerRequests
{
    public const string Pickup = "bos-alim";
    public const string Order = "siparis";
    public const string Open = "acik";
    public const string Done = "tamamlandi";
    public const string Cancelled = "iptal";
    public const int MaxOpenPerCustomer = 5;
    public const int MaxQuantity = 500;

    public static string KindLabel(string kind) => kind switch
    {
        Pickup => "Boş tüp alımı",
        Order => "Dolu tüp siparişi",
        _ => kind
    };

    public static string StatusLabel(string status) => status switch
    {
        Open => "Açık",
        Done => "Tamamlandı",
        Cancelled => "İptal",
        _ => status
    };

    public static string? Problem(string? kind, int? quantity, string? note)
    {
        if (kind is not (Pickup or Order))
        {
            return "Talep türünü seçin (boş tüp alımı veya dolu tüp siparişi).";
        }

        if (quantity is int count && (count < 1 || count > MaxQuantity))
        {
            return $"Adet 1 ile {MaxQuantity} arasında olmalı (bilmiyorsanız boş bırakın).";
        }

        return (note ?? string.Empty).Trim().Length > 300 ? "Not en fazla 300 karakter olabilir." : null;
    }

    public static string? CleanNote(string? note) => string.IsNullOrWhiteSpace(note) ? null : note.Trim();

    public static async Task<CustomerRequest> CreateAsync(int customerId, string kind, int? quantity, string? note, string createdBy)
    {
        if (Problem(kind, quantity, note) is string problem)
        {
            throw new ArgumentException(problem);
        }

        await using ArsanGazDbContext db = new();
        int open = await db.CustomerRequests.CountAsync(r => r.CustomerId == customerId && r.Status == Open);
        if (open >= MaxOpenPerCustomer)
        {
            throw new InvalidOperationException($"Açık {open} talebiniz var. Yeni talep için önce bunların tamamlanmasını bekleyin veya birini iptal edin.");
        }

        CustomerRequest request = new()
        {
            CustomerId = customerId,
            Kind = kind,
            Quantity = quantity,
            Note = CleanNote(note),
            CreatedAt = DateTime.Now,
            CreatedBy = createdBy.Length > 80 ? createdBy[..80] : createdBy
        };
        db.CustomerRequests.Add(request);
        await db.SaveChangesAsync();
        return request;
    }

    public static async Task<List<CustomerRequestView>> ListAsync(int? customerId, bool openOnly, int days, int take = 500)
    {
        DateTime since = DateTime.Today.AddDays(-Math.Clamp(days, 1, 3650));
        await using ArsanGazDbContext db = new();
        IQueryable<CustomerRequest> query = db.CustomerRequests.AsNoTracking();
        if (customerId is int id)
        {
            query = query.Where(r => r.CustomerId == id);
        }

        query = openOnly ? query.Where(r => r.Status == Open) : query.Where(r => r.Status == Open || r.CreatedAt >= since);
        var rows = await query
            .Join(db.Customers.AsNoTracking(), r => r.CustomerId, c => c.Id, (r, c) => new { r, c.CompanyName, c.City, c.District })
            .OrderByDescending(x => x.r.Status == Open)
            .ThenByDescending(x => x.r.CreatedAt)
            .Take(take)
            .ToListAsync();
        return rows.Select(x => new CustomerRequestView(
                x.r.Id, x.r.CustomerId, x.CompanyName, x.City, x.District, x.r.Kind, KindLabel(x.r.Kind), x.r.Quantity, x.r.Note, x.r.CreatedAt, x.r.CreatedBy,
                x.r.Status, StatusLabel(x.r.Status), x.r.HandledAt, x.r.HandledBy, x.r.HandledNote))
            .ToList();
    }

    // Closes one open request. onlyCustomerId limits a customer to their own requests (and to cancelling).
    public static async Task<CustomerRequest> CloseAsync(int id, string status, string handledBy, string? note, int? onlyCustomerId = null)
    {
        if (status is not (Done or Cancelled))
        {
            throw new ArgumentException("Geçersiz talep durumu.");
        }

        await using ArsanGazDbContext db = new();
        CustomerRequest request = await db.CustomerRequests.FirstOrDefaultAsync(r => r.Id == id && (onlyCustomerId == null || r.CustomerId == onlyCustomerId))
            ?? throw new InvalidOperationException("Talep bulunamadı.");
        if (request.Status != Open)
        {
            throw new InvalidOperationException($"Talep zaten {StatusLabel(request.Status).ToLowerInvariant()}.");
        }

        request.Status = status;
        request.HandledAt = DateTime.Now;
        request.HandledBy = handledBy.Length > 80 ? handledBy[..80] : handledBy;
        string? cleaned = CleanNote(note);
        request.HandledNote = cleaned is { Length: > 300 } ? cleaned[..300] : cleaned;
        await db.SaveChangesAsync();
        return request;
    }

    // A pickup scanned on a trip completes the customer's open pickup requests; a delivery completes the open orders.
    public static async Task<int> CompleteForCustomerAsync(int customerId, string kind, string handledBy, string note)
    {
        await using ArsanGazDbContext db = new();
        List<CustomerRequest> open = await db.CustomerRequests.Where(r => r.CustomerId == customerId && r.Kind == kind && r.Status == Open).ToListAsync();
        foreach (CustomerRequest request in open)
        {
            request.Status = Done;
            request.HandledAt = DateTime.Now;
            request.HandledBy = handledBy.Length > 80 ? handledBy[..80] : handledBy;
            request.HandledNote = note;
        }

        await db.SaveChangesAsync();
        return open.Count;
    }

    public static async Task<Dictionary<int, List<CustomerRequest>>> OpenByCustomerAsync()
    {
        await using ArsanGazDbContext db = new();
        return (await db.CustomerRequests.AsNoTracking().Where(r => r.Status == Open).OrderBy(r => r.CreatedAt).ToListAsync())
            .GroupBy(r => r.CustomerId)
            .ToDictionary(g => g.Key, g => g.ToList());
    }

    public static string Summary(IEnumerable<CustomerRequest> requests) =>
        string.Join(", ", requests.Select(r => KindLabel(r.Kind) + (r.Quantity is int q ? $" ({q} tüp)" : string.Empty)));
}
