using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace ArsanGazERP.Services;

// New barcode numbers for label printing: PREFIX + zero-padded running number (e.g. ATT000123). Codes stay within
// printable ASCII so every label prints as Code 128 and every scanner reads it back exactly.
public static class LabelCodes
{
    public const int MaxBatch = 500;
    public const int MaxPrefixLength = 10;
    public const int MaxDigits = 10;

    public static string NormalizePrefix(string? prefix)
    {
        string value = (prefix ?? string.Empty).Trim().ToUpperInvariant();
        if (value.Length > MaxPrefixLength)
        {
            throw new ArgumentException($"Önek en fazla {MaxPrefixLength} karakter olabilir.");
        }

        if (value.Any(c => c is < '!' or > '~'))
        {
            throw new ArgumentException("Önekte yalnızca Türkçe karakter içermeyen harf, rakam ve - / . gibi işaretler kullanılabilir.");
        }

        return value;
    }

    public static IReadOnlyList<string> Sequence(string? prefix, long start, int count, int digits)
    {
        string head = NormalizePrefix(prefix);
        if (count is < 1 or > MaxBatch)
        {
            throw new ArgumentException($"Tek seferde 1 ile {MaxBatch} arasında barkod oluşturulabilir.");
        }

        if (digits is < 1 or > MaxDigits)
        {
            throw new ArgumentException($"Basamak sayısı 1 ile {MaxDigits} arasında olmalı.");
        }

        if (start < 0 || start + count - 1 >= (long)Math.Pow(10, digits))
        {
            throw new ArgumentException($"{digits} basamağa sığmayan numara; başlangıcı küçültün veya basamak sayısını artırın.");
        }

        return Enumerable.Range(0, count).Select(i => head + (start + i).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0')).ToList();
    }

    // Next number after the highest existing PREFIX<digits> code (other codes are ignored); 1 when there is none.
    public static long NextNumber(string? prefix, IEnumerable<string?> existingCodes)
    {
        string head = NormalizePrefix(prefix);
        long max = 0;
        foreach (string? code in existingCodes)
        {
            string value = (code ?? string.Empty).Trim();
            if (value.Length > head.Length && value.StartsWith(head, StringComparison.OrdinalIgnoreCase)
                && value[head.Length..] is string tail && tail.Length <= 18 && tail.All(char.IsAsciiDigit)
                && long.TryParse(tail, NumberStyles.None, CultureInfo.InvariantCulture, out long number))
            {
                max = Math.Max(max, number);
            }
        }

        return max + 1;
    }
}
