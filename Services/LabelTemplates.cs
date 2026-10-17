using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using ArsanGazERP.Data;
using ArsanGazERP.Models;
using Microsoft.EntityFrameworkCore;

namespace ArsanGazERP.Services;

public sealed record LabelTemplateInput(
    int? Id, string? Name, string? GasText, string? Prefix, int? Digits, string? SampleCode, string? TestText, string? Title,
    int? WidthMm, int? HeightMm, int? Copies, bool? ShowGas, bool? ShowSerial, bool? ShowTest, bool? ShowText);

public sealed record LabelCodeParts(string Prefix, int Digits, long Number);

// Saved label products (Kayıtlı ürünler): a working barcode such as AG000123 is stored as prefix AG + 6 digits, so
// a new label is made by typing only the next serial number.
public static class LabelTemplates
{
    public const int MaxTemplates = 200;

    // "AG000123" -> AG, 6 digits, 123. The number is the trailing digit run; a code without one cannot be a template.
    public static LabelCodeParts SplitCode(string? code)
    {
        string value = (code ?? string.Empty).Trim().ToUpperInvariant();
        int split = value.Length;
        while (split > 0 && char.IsAsciiDigit(value[split - 1]))
        {
            split--;
        }

        string digits = value[split..];
        if (digits.Length == 0)
        {
            throw new ArgumentException("Örnek barkodun sonunda numara yok (ör. AG000123 olmalı).");
        }

        if (digits.Length > LabelCodes.MaxDigits)
        {
            throw new ArgumentException($"Örnek barkodun numara kısmı en fazla {LabelCodes.MaxDigits} basamak olabilir.");
        }

        string prefix = LabelCodes.NormalizePrefix(value[..split]);
        return new LabelCodeParts(prefix, digits.Length, long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture));
    }

    // Serial number typed by the user: either the number alone ("124") or the full code ("AG000124").
    public static long ParseSerial(LabelTemplate template, string? serial)
    {
        string value = (serial ?? string.Empty).Trim().ToUpperInvariant();
        if (template.Prefix.Length > 0 && value.StartsWith(template.Prefix, StringComparison.Ordinal))
        {
            value = value[template.Prefix.Length..];
        }

        if (value.Length == 0 || value.Length > 18 || !value.All(char.IsAsciiDigit))
        {
            throw new ArgumentException("Seri no yalnızca rakam olmalı (ör. 124) veya ürünün önekiyle başlamalı.");
        }

        return long.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
    }

    public static LabelTemplate Normalize(LabelTemplateInput input, LabelTemplate? into = null)
    {
        string name = Clean(input.Name, 60);
        if (name.Length == 0)
        {
            throw new ArgumentException("Ürün adı boş olamaz (ör. Oksijen 40 lt).");
        }

        string? sample = Clean(input.SampleCode, 40) is { Length: > 0 } s ? s.ToUpperInvariant() : null;
        string prefix;
        int digits;
        if (sample is not null)
        {
            LabelCodeParts parts = SplitCode(sample);
            prefix = parts.Prefix;
            digits = parts.Digits;
        }
        else
        {
            prefix = LabelCodes.NormalizePrefix(input.Prefix);
            digits = input.Digits ?? 6;
            if (digits is < 1 or > LabelCodes.MaxDigits)
            {
                throw new ArgumentException($"Basamak sayısı 1 ile {LabelCodes.MaxDigits} arasında olmalı.");
            }
        }

        LabelTemplate t = into ?? new LabelTemplate();
        t.Name = name;
        t.GasText = Clean(input.GasText, 60);
        t.Prefix = prefix;
        t.Digits = digits;
        t.SampleCode = sample;
        t.TestText = Clean(input.TestText, 30) is { Length: > 0 } test ? test : null;
        t.Title = Clean(input.Title, 40);
        t.WidthMm = Range(input.WidthMm, 20, 104, 50);
        t.HeightMm = Range(input.HeightMm, 10, 200, 30);
        t.Copies = Range(input.Copies, 1, 20, 1);
        t.ShowGas = input.ShowGas ?? true;
        t.ShowSerial = input.ShowSerial ?? true;
        t.ShowTest = input.ShowTest ?? true;
        t.ShowText = input.ShowText ?? true;
        return t;
    }

    public static async Task<List<LabelTemplate>> ListAsync()
    {
        await using ArsanGazDbContext db = new();
        List<LabelTemplate> all = await db.LabelTemplates.AsNoTracking().ToListAsync();
        return [.. all.OrderBy(t => t.Name, StringComparer.Create(CultureInfo.GetCultureInfo("tr-TR"), true))];
    }

    public static async Task<LabelTemplate?> GetAsync(int id)
    {
        await using ArsanGazDbContext db = new();
        return await db.LabelTemplates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == id);
    }

    public static async Task<LabelTemplate> SaveAsync(LabelTemplateInput input, string user)
    {
        await using ArsanGazDbContext db = new();
        LabelTemplate? existing = input.Id is int id ? await db.LabelTemplates.FirstOrDefaultAsync(t => t.Id == id) : null;
        if (input.Id is not null && existing is null)
        {
            throw new InvalidOperationException("Ürün bulunamadı; başka biri silmiş olabilir. Listeyi yenileyin.");
        }

        LabelTemplate template = Normalize(input, existing);
        string key = template.Name.ToLower(CultureInfo.GetCultureInfo("tr-TR"));
        List<LabelTemplate> others = await db.LabelTemplates.AsNoTracking().Where(t => t.Id != template.Id).ToListAsync();
        if (others.Any(t => t.Name.ToLower(CultureInfo.GetCultureInfo("tr-TR")) == key))
        {
            throw new InvalidOperationException($"\"{template.Name}\" adında bir ürün zaten var; başka bir ad verin veya onu düzenleyin.");
        }

        if (existing is null && others.Count >= MaxTemplates)
        {
            throw new InvalidOperationException($"En fazla {MaxTemplates} ürün kaydedilebilir; kullanılmayanları silin.");
        }

        template.UpdatedAt = DateTime.Now;
        template.UpdatedBy = user;
        if (existing is null)
        {
            template.CreatedAt = template.UpdatedAt;
            template.CreatedBy = user;
            db.LabelTemplates.Add(template);
        }

        await db.SaveChangesAsync();
        return template;
    }

    public static async Task<string?> DeleteAsync(int id)
    {
        await using ArsanGazDbContext db = new();
        LabelTemplate? template = await db.LabelTemplates.FirstOrDefaultAsync(t => t.Id == id);
        if (template is null)
        {
            return null;
        }

        db.LabelTemplates.Remove(template);
        await db.SaveChangesAsync();
        return template.Name;
    }

    private static string Clean(string? text, int max)
    {
        string value = string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return value.Length > max ? value[..max] : value;
    }

    private static int Range(int? value, int min, int max, int fallback) => value is int v && v >= min && v <= max ? v : fallback;
}
