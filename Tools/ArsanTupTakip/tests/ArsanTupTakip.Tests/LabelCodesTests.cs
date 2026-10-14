using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

// New barcode numbers for the Barkod Yazdır tab.
internal static class LabelCodesTests
{
    internal static void Run()
    {
        Check(LabelCodes.Sequence(" ag ", 9, 3, 4).SequenceEqual(["AG0009", "AG0010", "AG0011"]), "Önek büyük harfe çevrilip numara sıfırla doldurulmalı.");
        Check(LabelCodes.Sequence(null, 1, 2, 3).SequenceEqual(["001", "002"]), "Öneksiz numara üretilebilmeli.");
        Throws(() => LabelCodes.Sequence("AG", 998, 3, 3), "Basamağa sığmayan numara reddedilmeli.");
        Throws(() => LabelCodes.Sequence("AG", 1, LabelCodes.MaxBatch + 1, 6), "Tek seferde en fazla 500 barkod üretilmeli.");
        Throws(() => LabelCodes.Sequence("AG", 1, 0, 6), "Sıfır adet reddedilmeli.");
        Throws(() => LabelCodes.Sequence("ÇG", 1, 1, 6), "Türkçe karakterli önek reddedilmeli (Code 128 basamaz).");
        Throws(() => LabelCodes.Sequence("A G", 1, 1, 6), "Boşluklu önek reddedilmeli.");
        Throws(() => LabelCodes.Sequence("ABCDEFGHIJK", 1, 1, 6), "Çok uzun önek reddedilmeli.");

        Check(LabelCodes.NextNumber("AG", ["AG000007", "ag000012", "AGX5", "AB000099", "AG", null, "12345"]) == 13, "Sıradaki numara aynı önekli en büyük sayıdan sonra gelmeli.");
        Check(LabelCodes.NextNumber("AG", []) == 1, "Kayıt yoksa 1'den başlamalı.");
        Check(LabelCodes.NextNumber("", ["0042", "ABC", "7"]) == 43, "Öneksiz yalnızca rakamlı kodlar sayılmalı.");
        Console.WriteLine("PASS: barkod etiketi numara üretimi (önek, basamak, sınırlar, sıradaki boş numara).");
    }

    private static void Throws(Action action, string message)
    {
        try
        {
            action();
        }
        catch (ArgumentException)
        {
            return;
        }

        throw new InvalidOperationException("FAIL: " + message);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException("FAIL: " + message);
        }
    }
}
