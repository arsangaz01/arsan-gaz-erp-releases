using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Text;

namespace ArsanTupTakip.Services;

// Depot count report as Excel (.xlsx) and PDF without third-party packages.
public static class StockCountExport
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    public static string FileName(StockCountReport r, string extension) => $"depo-sayimi-{r.Id}-{r.StartedAt:yyyyMMdd-HHmm}.{extension}";

    public static IReadOnlyList<(string Key, string Value)> Summary(StockCountReport r) =>
    [
        ("Depo", r.Location),
        ("Sayım no", r.Id.ToString(Turkish)),
        ("Başlangıç", Time(r.StartedAt)),
        ("Başlatan", r.StartedBy),
        ("Durum", r.Open ? "Devam ediyor (rapor anlık)" : "Bitti"),
        ("Bitiş", r.ClosedAt is DateTime closed ? Time(closed) : "-"),
        ("Bitiren", r.ClosedBy ?? "-"),
        ("Not", r.Note ?? "-"),
        (r.Open ? "Kayıtta depoda (şu an)" : "Kayıtta depoda (bitişte)", r.Expected.ToString(Turkish)),
        ("Okutulan tüp", r.Counted.ToString(Turkish)),
        ("Eşleşen", r.Comparison.Matched.Count.ToString(Turkish)),
        ("Eksik (okutulmadı)", r.Comparison.Missing.Count.ToString(Turkish)),
        ("Fazla (kayıtta başka yerde)", r.Comparison.Extra.Count.ToString(Turkish)),
        ("Tanınmayan barkod", r.Unknown.Count.ToString(Turkish))
    ];

    private static string Time(DateTime local) => local.ToString("dd.MM.yyyy HH:mm", Turkish);

    private static readonly string[] GroupHeader = ["Gaz türü", "Boyut", "Kayıtta depoda", "Sayılan", "Eşleşen", "Eksik", "Fazla", "Dolu", "Boş", "Fark"];
    private static readonly string[] CylinderHeader = ["Grup", "Seri no", "Barkod", "Dolu/Boş", "Yer", "Okutulma", "Okutan"];

    private static object[] GroupRow(StockCountGroup g) =>
        [g.GasType, g.Capacity, g.Expected, g.Counted, g.Matched, g.Missing, g.Extra, g.Full, g.Empty, g.Counted - g.Expected];

    private static object[] CylinderRow(CountCylinder c) =>
        [StockCounting.GroupTitle(c.GasType, c.Capacity), c.SerialNumber, c.Barcode, CylinderTracking.FullnessName(c.IsFull), c.Place,
            c.ScannedAt is DateTime at ? Time(at) : "", c.ScannedBy ?? ""];

    // ---- Excel ------------------------------------------------------------------------------------------------

    public static byte[] Xlsx(StockCountReport r)
    {
        List<(string Name, string[] Header, List<object[]> Rows)> sheets =
        [
            ("Özet", ["Bilgi", "Değer"], Summary(r).Select(x => new object[] { x.Key, x.Value }).ToList()),
            ("Gruplar", GroupHeader, r.Comparison.Groups.Select(GroupRow).ToList()),
            ("Eksik", CylinderHeader, r.Comparison.Missing.Select(CylinderRow).ToList()),
            ("Fazla", CylinderHeader, r.Comparison.Extra.Select(CylinderRow).ToList()),
            ("Eşleşen", CylinderHeader, r.Comparison.Matched.Select(CylinderRow).ToList()),
            ("Tanınmayan", ["Okutulan kod", "Zaman", "Okutan"], r.Unknown.Select(u => new object[] { u.Code, Time(u.ScannedAt), u.ScannedBy }).ToList())
        ];
        return Xlsx(sheets);
    }

    internal static byte[] Xlsx(IReadOnlyList<(string Name, string[] Header, List<object[]> Rows)> sheets)
    {
        using MemoryStream stream = new();
        using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using StreamWriter writer = new(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
                writer.Write(xml);
            }

            const string Head = "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>";
            Add("[Content_Types].xml", Head
                + "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">"
                + "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>"
                + "<Default Extension=\"xml\" ContentType=\"application/xml\"/>"
                + "<Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/>"
                + "<Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/>"
                + string.Concat(sheets.Select((_, i) => $"<Override PartName=\"/xl/worksheets/sheet{i + 1}.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/>"))
                + "</Types>");
            Add("_rels/.rels", Head
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/>"
                + "</Relationships>");
            Add("xl/workbook.xml", Head
                + "<workbook xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets>"
                + string.Concat(sheets.Select((s, i) => $"<sheet name=\"{Xml(s.Name)}\" sheetId=\"{i + 1}\" r:id=\"rId{i + 1}\"/>"))
                + "</sheets></workbook>");
            Add("xl/_rels/workbook.xml.rels", Head
                + "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">"
                + string.Concat(sheets.Select((_, i) => $"<Relationship Id=\"rId{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet{i + 1}.xml\"/>"))
                + $"<Relationship Id=\"rId{sheets.Count + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>"
                + "</Relationships>");
            Add("xl/styles.xml", Head
                + "<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + "<fonts count=\"2\"><font><sz val=\"11\"/><name val=\"Calibri\"/></font><font><b/><sz val=\"11\"/><name val=\"Calibri\"/></font></fonts>"
                + "<fills count=\"3\"><fill><patternFill patternType=\"none\"/></fill><fill><patternFill patternType=\"gray125\"/></fill>"
                + "<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FFCCFBF1\"/><bgColor indexed=\"64\"/></patternFill></fill></fills>"
                + "<borders count=\"1\"><border><left/><right/><top/><bottom/><diagonal/></border></borders>"
                + "<cellStyleXfs count=\"1\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\"/></cellStyleXfs>"
                + "<cellXfs count=\"2\"><xf numFmtId=\"0\" fontId=\"0\" fillId=\"0\" borderId=\"0\" xfId=\"0\"/>"
                + "<xf numFmtId=\"0\" fontId=\"1\" fillId=\"2\" borderId=\"0\" xfId=\"0\" applyFont=\"1\" applyFill=\"1\"/></cellXfs>"
                + "</styleSheet>");
            for (int i = 0; i < sheets.Count; i++)
            {
                Add($"xl/worksheets/sheet{i + 1}.xml", Sheet(sheets[i].Header, sheets[i].Rows));
            }
        }

        return stream.ToArray();
    }

    private static string Sheet(string[] header, List<object[]> rows)
    {
        StringBuilder xml = new("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>"
            + "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
            + "<sheetViews><sheetView workbookViewId=\"0\"><pane ySplit=\"1\" topLeftCell=\"A2\" activePane=\"bottomLeft\" state=\"frozen\"/></sheetView></sheetViews><cols>");
        for (int c = 0; c < header.Length; c++)
        {
            int width = Math.Clamp(rows.Select(r => c < r.Length ? Convert.ToString(r[c], Turkish)?.Length ?? 0 : 0).Append(header[c].Length).Max() + 2, 8, 60);
            xml.Append($"<col min=\"{c + 1}\" max=\"{c + 1}\" width=\"{width}\" customWidth=\"1\"/>");
        }

        xml.Append("</cols><sheetData>");
        AppendRow(xml, 1, header.Cast<object>().ToArray(), style: 1);
        for (int i = 0; i < rows.Count; i++)
        {
            AppendRow(xml, i + 2, rows[i], style: 0);
        }

        xml.Append("</sheetData>");
        if (rows.Count > 0)
        {
            xml.Append($"<autoFilter ref=\"A1:{Column(header.Length - 1)}{rows.Count + 1}\"/>");
        }

        return xml.Append("</worksheet>").ToString();
    }

    private static void AppendRow(StringBuilder xml, int row, object[] cells, int style)
    {
        xml.Append($"<row r=\"{row}\">");
        for (int c = 0; c < cells.Length; c++)
        {
            string reference = Column(c) + row;
            string s = style > 0 ? $" s=\"{style}\"" : string.Empty;
            if (cells[c] is int number)
            {
                xml.Append($"<c r=\"{reference}\"{s}><v>{number.ToString(CultureInfo.InvariantCulture)}</v></c>");
            }
            else
            {
                xml.Append($"<c r=\"{reference}\" t=\"inlineStr\"{s}><is><t xml:space=\"preserve\">{Xml(Convert.ToString(cells[c], Turkish) ?? string.Empty)}</t></is></c>");
            }
        }

        xml.Append("</row>");
    }

    private static string Column(int index)
    {
        string name = string.Empty;
        for (int n = index + 1; n > 0; n = (n - 1) / 26)
        {
            name = (char)('A' + (n - 1) % 26) + name;
        }

        return name;
    }

    private static string Xml(string text) =>
        SecurityElement.Escape(new string(text.Where(ch => ch is '\t' or '\n' or '\r' || ch >= ' ').ToArray())) ?? string.Empty;

    // ---- PDF --------------------------------------------------------------------------------------------------

    public static byte[] Pdf(StockCountReport r)
    {
        PdfWriter pdf = new();
        pdf.Title($"{r.Location} sayımı · {Time(r.StartedAt)}" + (r.Open ? " (devam ediyor)" : string.Empty));
        foreach ((string key, string value) in Summary(r))
        {
            pdf.KeyValue(key, value);
        }

        pdf.Space();
        pdf.Heading("Gaz türü ve boyuta göre karşılaştırma");
        float[] groupWidths = [150, 60, 50, 45, 45, 40, 40, 35, 35, 35];
        pdf.Table(["Gaz türü", "Boyut", "Kayıtta", "Sayılan", "Eşleşen", "Eksik", "Fazla", "Dolu", "Boş", "Fark"], groupWidths,
            r.Comparison.Groups.Select(g =>
            {
                int diff = g.Counted - g.Expected;
                return new[] { g.GasType, g.Capacity, $"{g.Expected}", $"{g.Counted}", $"{g.Matched}", $"{g.Missing}", $"{g.Extra}", $"{g.Full}", $"{g.Empty}", diff > 0 ? $"+{diff}" : $"{diff}" };
            }).ToList());
        float[] cylinderWidths = [120, 75, 80, 40, 140, 80];
        string[] cylinderHeader = ["Grup", "Seri no", "Barkod", "D/B", "Yer", "Okutulma"];
        string[] Short(CountCylinder c) => [StockCounting.GroupTitle(c.GasType, c.Capacity), c.SerialNumber, c.Barcode, CylinderTracking.FullnessName(c.IsFull), c.Place,
            c.ScannedAt is DateTime at ? Time(at) : ""];
        Section(pdf, $"Eksik tüpler – kayıtta depoda, okutulmadı ({r.Comparison.Missing.Count})", cylinderHeader, cylinderWidths, r.Comparison.Missing.Select(Short).ToList());
        Section(pdf, $"Fazla tüpler – okutuldu, kayıtta başka yerde ({r.Comparison.Extra.Count})", cylinderHeader, cylinderWidths, r.Comparison.Extra.Select(Short).ToList());
        Section(pdf, $"Tanınmayan barkodlar ({r.Unknown.Count})", ["Okutulan kod", "Zaman", "Okutan"], [180, 90, 265],
            r.Unknown.Select(u => new[] { u.Code, Time(u.ScannedAt), u.ScannedBy }).ToList());
        pdf.Space();
        pdf.Note($"Eşleşen {r.Comparison.Matched.Count} tüpün tam listesi Excel çıktısındadır. Oluşturma: {Time(DateTime.Now)} · ArsanTüpTakip");
        return pdf.Build();
    }

    private static void Section(PdfWriter pdf, string title, string[] header, float[] widths, List<string[]> rows)
    {
        pdf.Space();
        pdf.Heading(title);
        if (rows.Count == 0)
        {
            pdf.Note("Yok.");
            return;
        }

        pdf.Table(header, widths, rows);
    }
}

// Minimal A4 PDF with the standard Helvetica fonts; Turkish letters outside WinAnsi are mapped via /Differences.
internal sealed class PdfWriter
{
    private const float PageWidth = 595, PageHeight = 842, Margin = 30, Bottom = 40;
    private readonly List<StringBuilder> _pages = [];
    private StringBuilder _page = null!;
    private float _y;

    public PdfWriter() => NewPage();

    private void NewPage()
    {
        _page = new StringBuilder();
        _pages.Add(_page);
        _y = PageHeight - Margin;
    }

    private void Ensure(float height)
    {
        if (_y - height < Bottom)
        {
            NewPage();
        }
    }

    private void Text(float x, float y, string text, float size, bool bold) =>
        _page.Append($"BT /{(bold ? "F2" : "F1")} {N(size)} Tf {N(x)} {N(y)} Td ({Escape(text)}) Tj ET\n");

    private static string N(float value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public void Title(string text)
    {
        Ensure(24);
        _y -= 16;
        Text(Margin, _y, Fit(text, PageWidth - 2 * Margin, 15), 15, true);
        _y -= 10;
    }

    public void Heading(string text)
    {
        Ensure(40);
        _y -= 13;
        Text(Margin, _y, Fit(text, PageWidth - 2 * Margin, 11), 11, true);
        _y -= 5;
    }

    public void KeyValue(string key, string value)
    {
        Ensure(13);
        _y -= 12;
        Text(Margin, _y, key, 9, true);
        Text(Margin + 170, _y, Fit(value, PageWidth - 2 * Margin - 170, 9), 9, false);
    }

    public void Note(string text)
    {
        Ensure(13);
        _y -= 12;
        Text(Margin, _y, Fit(text, PageWidth - 2 * Margin, 8.5f), 8.5f, false);
    }

    public void Space() => _y -= 8;

    public void Table(string[] header, float[] widths, List<string[]> rows)
    {
        const float Size = 8, Line = 11;
        void Header()
        {
            Ensure(Line * 2);
            _y -= Line;
            _page.Append($"0.8 0.98 0.95 rg {N(Margin)} {N(_y - 3)} {N(widths.Sum())} {N(Line)} re f 0 g\n");
            float x = Margin;
            for (int i = 0; i < header.Length; i++)
            {
                Text(x + 2, _y, Fit(header[i], widths[i] - 4, Size), Size, true);
                x += widths[i];
            }
        }

        Header();
        foreach (string[] row in rows)
        {
            if (_y - Line < Bottom)
            {
                NewPage();
                Header();
            }

            _y -= Line;
            float x = Margin;
            for (int i = 0; i < header.Length && i < row.Length; i++)
            {
                Text(x + 2, _y, Fit(row[i], widths[i] - 4, Size), Size, false);
                x += widths[i];
            }
        }
    }

    // Helvetica averages about half the font size per character; long cells are cut with "..".
    private static string Fit(string? text, float width, float size)
    {
        string value = (text ?? string.Empty).Replace('\n', ' ').Replace('\r', ' ');
        int max = Math.Max(1, (int)(width / (size * 0.52f)));
        return value.Length <= max ? value : value[..Math.Max(1, max - 2)] + "..";
    }

    private static string Escape(string text)
    {
        StringBuilder result = new();
        foreach (char ch in text)
        {
            int code = ch switch
            {
                'Ğ' => 128, 'ğ' => 129, 'İ' => 130, 'ı' => 131, 'Ş' => 132, 'ş' => 133,
                '–' => 150, '—' => 151, '…' => '.',
                _ when ch is >= ' ' and <= '~' or >= '\u00A0' and <= '\u00FF' => ch,
                _ => '?'
            };
            if (code is '(' or ')' or '\\')
            {
                result.Append('\\').Append((char)code);
            }
            else if (code > 126)
            {
                result.Append('\\').Append(Convert.ToString(code, 8).PadLeft(3, '0'));
            }
            else
            {
                result.Append((char)code);
            }
        }

        return result.ToString();
    }

    public byte[] Build()
    {
        for (int i = 0; i < _pages.Count; i++)
        {
            _pages[i].Append($"BT /F1 7.5 Tf {N(PageWidth - Margin - 60)} 20 Td (Sayfa {i + 1} / {_pages.Count}) Tj ET\n");
        }

        List<string> objects =
        [
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [" + string.Join(" ", _pages.Select((_, i) => $"{5 + i * 2} 0 R")) + $"] /Count {_pages.Count} >>",
            Font("Helvetica"),
            Font("Helvetica-Bold")
        ];
        foreach (StringBuilder page in _pages)
        {
            int self = objects.Count + 1;
            objects.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 {N(PageWidth)} {N(PageHeight)}] /Resources << /Font << /F1 3 0 R /F2 4 0 R >> >> /Contents {self + 1} 0 R >>");
            string content = page.ToString();
            objects.Add($"<< /Length {Encoding.Latin1.GetByteCount(content)} >>\nstream\n{content}endstream");
        }

        using MemoryStream stream = new();
        void Write(string text)
        {
            byte[] bytes = Encoding.Latin1.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        Write("%PDF-1.4\n%\u00E2\u00E3\u00CF\u00D3\n");
        List<long> offsets = [];
        for (int i = 0; i < objects.Count; i++)
        {
            offsets.Add(stream.Position);
            Write($"{i + 1} 0 obj\n{objects[i]}\nendobj\n");
        }

        long xref = stream.Position;
        Write($"xref\n0 {objects.Count + 1}\n0000000000 65535 f \n");
        foreach (long offset in offsets)
        {
            Write($"{offset:D10} 00000 n \n");
        }

        Write($"trailer\n<< /Size {objects.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return stream.ToArray();
    }

    private static string Font(string name) =>
        $"<< /Type /Font /Subtype /Type1 /BaseFont /{name} /Encoding << /Type /Encoding /BaseEncoding /WinAnsiEncoding "
        + "/Differences [128 /Gbreve /gbreve /Idotaccent /dotlessi /Scedilla /scedilla] >> >>";
}
