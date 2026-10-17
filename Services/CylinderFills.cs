using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record FillLine(string Code, bool Ok, string Message, bool TestOverdue = false);

public sealed record FillResult(int Filled, IReadOnlyList<FillLine> Lines)
{
    public IReadOnlyList<string> NeedsTestConfirmation => Lines.Where(x => !x.Ok && x.TestOverdue).Select(x => x.Code).ToList();
}

public sealed record FillListItem(int Id, int CylinderId, string SerialNumber, string GasType, DateTime FilledAt, string FilledBy, string? LotNumber, string? Note, bool TestOverdue);

// Filling: the cylinder becomes full in the depot and the fill (who, when, which lot) is kept for traceability.
public static class CylinderFills
{
    public const int MaxLotLength = 60;
    public const int MaxNoteLength = 300;

    public static (string? Problem, bool TestOverdue) Check(CylinderStatus status, bool isActive, DateTime? testDue, DateTime today, bool allowOverdueTest)
    {
        if (!isActive || status == CylinderStatus.Scrap)
        {
            return ("pasif veya hurda tüp doldurulamaz", false);
        }

        if (status == CylinderStatus.Customer)
        {
            return ("sistemde müşteride görünüyor; önce boş alım veya depo dönüşü okutun", false);
        }

        if (testDue is DateTime due && due.Date < today.Date && !allowOverdueTest)
        {
            return ($"periyodik test tarihi geçmiş ({due:dd.MM.yyyy}); doldurmak için onay gerekir", true);
        }

        return (null, false);
    }

    public static string? CleanLot(string? lot)
    {
        string? clean = string.IsNullOrWhiteSpace(lot) ? null : lot.Trim();
        return clean is { Length: > MaxLotLength } ? throw new ArgumentException($"Parti/lot numarası en fazla {MaxLotLength} karakter olabilir.") : clean;
    }

    public static async Task<FillResult> FillAsync(IEnumerable<string?> codes, string? lot, string? note, string performedBy, bool allowOverdueTest)
    {
        IReadOnlyList<string> clean = CylinderTracking.CleanCodes(codes);
        if (clean.Count == 0)
        {
            throw new ArgumentException("Okutulmuş tüp yok.");
        }

        string? lotNumber = CleanLot(lot);
        string? cleanNote = string.IsNullOrWhiteSpace(note) ? null : note.Trim();
        if (cleanNote is { Length: > MaxNoteLength })
        {
            throw new ArgumentException($"Not en fazla {MaxNoteLength} karakter olabilir.");
        }

        CrudService crud = new();
        List<FillLine> lines = [];
        List<(string Code, Cylinder Cylinder, bool Overdue)> ready = [];
        DateTime today = DateTime.Today;
        foreach (string code in clean)
        {
            Cylinder? cylinder = await crud.FindCylinderByCodeAsync(code);
            if (cylinder is null)
            {
                lines.Add(new FillLine(code, false, $"{code}: tüp bulunamadı."));
                continue;
            }

            (string? problem, bool overdue) = Check(cylinder.Status, cylinder.IsActive, cylinder.TestDueDate, today, allowOverdueTest);
            if (problem is not null)
            {
                lines.Add(new FillLine(code, false, $"{cylinder.SerialNumber}: {problem}.", overdue));
                continue;
            }

            ready.Add((code, cylinder, cylinder.TestDueDate is DateTime due && due.Date < today));
        }

        if (ready.Count == 0)
        {
            return new FillResult(0, lines);
        }

        string movementNote = "Dolum" + (lotNumber is null ? string.Empty : $" · lot {lotNumber}") + (cleanNote is null ? string.Empty : " · " + cleanNote) + $" · {performedBy}";
        List<CylinderBulkResult> moved = await crud.MoveCylindersAsync(ready.Select(x => x.Code), CylinderStatus.Warehouse, null, movementNote, "Dolum", isFull: true);
        Dictionary<string, CylinderBulkResult> byCode = moved.ToDictionary(x => x.Code, StringComparer.OrdinalIgnoreCase);
        await using ArsanGazDbContext db = new();
        DateTime now = DateTime.Now;
        int filled = 0;
        foreach ((string code, Cylinder cylinder, bool overdue) in ready)
        {
            if (byCode.GetValueOrDefault(code) is not { Ok: true })
            {
                lines.Add(new FillLine(code, false, $"{cylinder.SerialNumber}: kaydedilemedi ({byCode.GetValueOrDefault(code)?.Message ?? "bilinmeyen hata"})."));
                continue;
            }

            db.CylinderFills.Add(new CylinderFill
            {
                CylinderId = cylinder.Id,
                SerialNumber = cylinder.SerialNumber,
                GasType = cylinder.GasType,
                FilledAt = now,
                FilledBy = performedBy,
                LotNumber = lotNumber,
                Note = cleanNote,
                TestOverdue = overdue
            });
            filled++;
            lines.Add(new FillLine(code, true, $"{cylinder.SerialNumber} ({cylinder.GasType}): dolu, depoda" + (overdue ? " · ⚠ testi geçmiş, onayla dolduruldu" : string.Empty)));
        }

        await db.SaveChangesAsync();
        return new FillResult(filled, lines);
    }

    public static async Task<List<FillListItem>> ListAsync(int days, string? search, int take = 500)
    {
        DateTime since = DateTime.Today.AddDays(-Math.Clamp(days, 1, 3650) + 1);
        await using ArsanGazDbContext db = new();
        IQueryable<CylinderFill> query = db.CylinderFills.AsNoTracking().Where(x => x.FilledAt >= since);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string q = search.Trim().ToLower();
            query = query.Where(x => x.SerialNumber.ToLower().Contains(q) || (x.LotNumber != null && x.LotNumber.ToLower().Contains(q))
                || x.GasType.ToLower().Contains(q) || x.FilledBy.ToLower().Contains(q));
        }

        return await query.OrderByDescending(x => x.FilledAt).ThenByDescending(x => x.Id).Take(Math.Clamp(take, 1, 2000))
            .Select(x => new FillListItem(x.Id, x.CylinderId, x.SerialNumber, x.GasType, x.FilledAt, x.FilledBy, x.LotNumber, x.Note, x.TestOverdue))
            .ToListAsync();
    }
}
