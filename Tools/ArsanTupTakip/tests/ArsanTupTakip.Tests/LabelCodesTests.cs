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

        Check(LabelTemplates.SplitCode(" ag000123 ") == new LabelCodeParts("AG", 6, 123), "Örnek barkod önek + basamak + numaraya ayrılmalı.");
        Check(LabelTemplates.SplitCode("OX-2024-0042") == new LabelCodeParts("OX-2024-", 4, 42), "Önekteki rakamlar korunmalı; yalnız sondaki numara değişmeli.");
        Check(LabelTemplates.SplitCode("000077") == new LabelCodeParts("", 6, 77), "Öneksiz rakamlı barkod ürün olabilmeli.");
        Throws(() => LabelTemplates.SplitCode("AGX"), "Sonunda numara olmayan barkod ürün olamamalı.");
        Throws(() => LabelTemplates.SplitCode("ÇG0001"), "Türkçe karakterli örnek barkod reddedilmeli.");
        Throws(() => LabelTemplates.SplitCode("AG12345678901"), "11 basamaklı numara reddedilmeli.");

        Models.LabelTemplate t = LabelTemplates.Normalize(new LabelTemplateInput(null, "  Oksijen   40 lt ", "Oksijen 40 lt", "zz", 3, "ag000123", null, "ARSAN GAZ",
            500, 5, 0, null, false, null, null));
        Check(t.Name == "Oksijen 40 lt" && t.Prefix == "AG" && t.Digits == 6 && t.SampleCode == "AG000123", "Örnek barkod varsa önek/basamak ondan alınmalı.");
        Check(t.WidthMm == 50 && t.HeightMm == 30 && t.Copies == 1 && t.ShowGas && !t.ShowSerial, "Yazıcı sınırı dışındaki ölçüler varsayılana dönmeli.");
        Check(LabelTemplates.Normalize(new LabelTemplateInput(null, "CO2", "", " co ", 4, " ", null, "", 104, 10, 2, true, true, true, true)) is { Prefix: "CO", Digits: 4, SampleCode: null, WidthMm: 104 },
            "Örnek barkod yoksa önek ve basamak elle verilebilmeli.");
        Throws(() => LabelTemplates.Normalize(new LabelTemplateInput(null, "  ", "", "AG", 6, null, null, "", null, null, null, null, null, null, null)), "Ürün adı boş olamamalı.");
        Throws(() => LabelTemplates.Normalize(new LabelTemplateInput(null, "X", "", "AG", 11, null, null, "", null, null, null, null, null, null, null)), "Basamak 1-10 dışında reddedilmeli.");

        Check(LabelTemplates.ParseSerial(t, "124") == 124 && LabelTemplates.ParseSerial(t, " ag000125 ") == 125 && LabelTemplates.ParseSerial(t, "000126") == 126,
            "Seri no yalnız numara ya da önekli tam barkod olarak yazılabilmeli.");
        Throws(() => LabelTemplates.ParseSerial(t, "AB000124"), "Başka önekli barkod reddedilmeli.");
        Throws(() => LabelTemplates.ParseSerial(t, ""), "Boş seri no reddedilmeli.");
        Check(LabelCodes.Sequence(t.Prefix, LabelTemplates.ParseSerial(t, "124"), 2, t.Digits).SequenceEqual(["AG000124", "AG000125"]), "Üründen yeni barkod aynı biçimde üretilmeli.");
        Console.WriteLine("PASS: kayıtlı barkod ürünleri (örnek barkoddan biçim, ölçü sınırları, seri numarasından yeni barkod).");

        string tspl = TsplLabels.Build(
            [new TsplLabels.Item("AG000001", Serial: "AG000001", Gas: "Oksijen 40 lt", Stt: "01.01.2024", Gtt: "01.01.2029", Owner: "Arsan")],
            new TsplLabels.Settings(50, 30, 2, "ARSAN GAZ", true, true, true, true));
        Check(tspl.Contains("SIZE 50 mm,30 mm", StringComparison.Ordinal), "TSPL etiket ölçüsü mm cinsinden olmalı.");
        Check(tspl.Contains("QRCODE ", StringComparison.Ordinal) && tspl.Contains("M2,S7,\"AG000001\"", StringComparison.Ordinal),
            "TA310 karekodu TSPL QRCODE (M2) ile kendi üretmeli.");
        Check(tspl.Contains("TEXT ", StringComparison.Ordinal) && tspl.Contains("\"0\"", StringComparison.Ordinal), "Yazılar TSPL2 font 0 (TrueType) ile basılmalı.");
        Check(tspl.Contains("PRINT 1,2", StringComparison.Ordinal), "Kopya sayısı PRINT komutunda olmalı.");
        Check(tspl.Contains("CODEPAGE UTF-8", StringComparison.Ordinal), "Türkçe metin için UTF-8 kod sayfası istenmeli.");
        Throws(() => TsplLabels.Build([], new TsplLabels.Settings()), "Boş liste TSPL üretmemeli.");
        Console.WriteLine("PASS: TSPL2 etiket üretimi (QRCODE native, SIZE/PRINT, UTF-8).");
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
