using ArsanTupTakip.Services;

namespace ArsanTupTakip.Tests;

internal static class StockCountTests
{
    internal static void Run()
    {
        ComparesDepotCountByGasAndSize();
        NamesPlaces();
        ExportsExcelAndPdf();
    }

    private static void ExportsExcelAndPdf()
    {
        StockCountComparison comparison = StockCounting.Compare(
            [C(1, "ARGON", "50 Litre"), C(2, "OKSİJEN", "40 Litre")],
            [(C(1, "ARGON", "50 Litre"), true), (C(3, "AZOT", "10 Litre", false, "Müşteride · Şeker & <Ltd>"), false)]);
        StockCountReport report = new(7, "Ana depo", new DateTime(2026, 10, 14, 9, 30, 0), "Ali, telefon", new DateTime(2026, 10, 14, 11, 0, 0), "Ali", "Ay sonu ığüşöç",
            comparison, [new UnknownScan("ZZ-1", new DateTime(2026, 10, 14, 10, 0, 0), "Ali")], []);

        byte[] xlsx = StockCountExport.Xlsx(report);
        using (System.IO.Compression.ZipArchive zip = new(new MemoryStream(xlsx)))
        {
            string[] names = [.. zip.Entries.Select(e => e.FullName)];
            Assert(names.Contains("xl/workbook.xml") && names.Count(n => n.StartsWith("xl/worksheets/sheet", StringComparison.Ordinal)) == 6,
                "The workbook has summary, groups, missing, extra, matched and unknown sheets.");
            using StreamReader reader = new(zip.GetEntry("xl/worksheets/sheet4.xml")!.Open());
            string extraSheet = reader.ReadToEnd();
            Assert(extraSheet.Contains("Şeker &amp; &lt;Ltd&gt;") && System.Xml.Linq.XDocument.Parse(extraSheet) is not null, "Cell text is XML-escaped and keeps Turkish letters.");
        }

        byte[] pdf = StockCountExport.Pdf(report);
        string text = System.Text.Encoding.Latin1.GetString(pdf);
        Assert(text.StartsWith("%PDF-1.4", StringComparison.Ordinal) && text.TrimEnd().EndsWith("%%EOF", StringComparison.Ordinal), "The PDF has a header and trailer.");
        Assert(text.Contains("/Differences [128 /Gbreve /gbreve /Idotaccent /dotlessi /Scedilla /scedilla]"), "Turkish letters are mapped in the font encoding.");
        Assert(text.Contains("(Ay sonu \\203\\201\\374\\205\\366\\347)"), "ı ğ ü ş ö ç are written with the mapped codes.");
        int xref = int.Parse(text[(text.LastIndexOf("startxref", StringComparison.Ordinal) + 10)..].Split('\n')[0]);
        Assert(text[xref..].StartsWith("xref", StringComparison.Ordinal), "startxref points at the cross-reference table.");
        Assert(StockCountExport.FileName(report, "pdf") == "depo-sayimi-7-20261014-0930.pdf", "Export file name has the count number and date.");
    }

    private static CountCylinder C(int id, string gas, string capacity, bool? full = true, string place = "Depoda") =>
        new(id, "S-" + id, "B-" + id, gas, capacity, full, place);

    private static void ComparesDepotCountByGasAndSize()
    {
        List<CountCylinder> expected = [C(1, "ARGON", "50 Litre"), C(2, "argon", "50 Litre", false), C(3, "ARGON", "10 Litre"), C(4, "OKSİJEN", "40 Litre")];
        List<(CountCylinder, bool)> scanned =
        [
            (C(1, "ARGON", "50 Litre"), true),
            (C(1, "ARGON", "50 Litre"), true),
            (C(3, "ARGON", "10 Litre"), true),
            (C(9, "ARGON", "50 Litre", false, "Müşteride · X"), false)
        ];
        StockCountComparison result = StockCounting.Compare(expected, scanned);
        Assert(result.Matched.Select(x => x.Id).SequenceEqual([3, 1]), "Scans of warehouse cylinders match once each, sorted by gas and size.");
        Assert(result.Missing.Select(x => x.Id).Order().SequenceEqual([2, 4]), "Warehouse cylinders that were not scanned are missing.");
        Assert(result.Extra.Count == 1 && result.Extra[0].Place == "Müşteride · X", "A cylinder recorded elsewhere is reported with its place.");
        Assert(result.Groups.Select(x => x.Title).SequenceEqual(["ARGON 10 Litre", "ARGON 50 Litre", "OKSİJEN 40 Litre"]),
            "Groups are per gas and size, gas A→Z then size small to large.");
        StockCountGroup argon50 = result.Groups[1];
        Assert(argon50.Expected == 2 && argon50.Counted == 2 && argon50.Matched == 1 && argon50.Missing == 1 && argon50.Extra == 1,
            "Each group counts expected, counted, matched, missing and extra cylinders.");
        Assert(argon50.Full == 1 && argon50.Empty == 1, "Full and empty are counted among the scanned cylinders.");
        Assert(result.Groups[2].Counted == 0 && result.Groups[2].Missing == 1, "A group with nothing scanned still shows its missing cylinders.");
        Assert(StockCounting.Compare([], []).Groups.Count == 0, "An empty count has no groups.");
        Assert(StockCounting.GroupKey(" argon ", "50 Litre") == StockCounting.GroupKey("ARGON", "50 litre"), "Group keys ignore case and spaces.");

        StockCountGroupDetail detail = StockCounting.Group(result, argon50.GasType, argon50.Capacity);
        Assert(detail.Group == argon50, "Group detail finds the row the phone tapped.");
        Assert(detail.Matched.Select(x => x.Id).SequenceEqual([1]) && detail.Missing.Select(x => x.Id).SequenceEqual([2]) && detail.Extra.Select(x => x.Id).SequenceEqual([9]),
            "Group detail lists only that gas/size: matched, still unscanned and not-in-stock cylinders.");
        Assert(StockCounting.Group(result, "argon", "50 litre").Missing.Count == 1, "Group detail ignores case.");
        StockCountGroupDetail none = StockCounting.Group(result, "AZOT", "10 Litre");
        Assert(none.Group is null && none.Matched.Count + none.Missing.Count + none.Extra.Count == 0, "An unknown group is empty.");
    }

    private static void NamesPlaces()
    {
        Assert(StockCounting.Place(3, "ABC Ltd", null) == "Müşteride · ABC Ltd", "Customer place names the customer.");
        Assert(StockCounting.Place(2, null, "01 ABC 123") == "Araçta · 01 ABC 123", "Vehicle place names the plate.");
        Assert(StockCounting.Place(1, null, null) == "Depoda", "Warehouse place.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
