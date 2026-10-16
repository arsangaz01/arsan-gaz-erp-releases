using System.Text;

namespace ArsanTupTakip.Services;

/// <summary>
/// Builds TSPL2 jobs for TSC TA310 (300 dpi). The printer draws QRCODE/TEXT itself;
/// the host only sends coordinates and data — not a rasterized barcode image.
/// </summary>
public static class TsplLabels
{
    public const int Dpi = 300;
    public const double DotsPerMm = Dpi / 25.4; // 11.811… ≈ 12 for integer layout

    public sealed record Item(
        string Code,
        string? Serial = null,
        string? Gas = null,
        string? Stt = null,
        string? Gtt = null,
        string? Owner = null,
        string? Extra = null);

    public sealed record Settings(
        int WidthMm = 50,
        int HeightMm = 30,
        int Copies = 1,
        string? Title = "ARSAN GAZ",
        bool ShowGas = true,
        bool ShowSerial = true,
        bool ShowTest = true,
        bool ShowCodeText = true,
        int GapMm = 2,
        int Speed = 4,
        int Density = 8);

    public static string Build(IEnumerable<Item> items, Settings settings)
    {
        List<Item> list = items.Select(NormalizeItem).Where(i => i.Code.Length > 0).ToList();
        if (list.Count == 0)
        {
            throw new ArgumentException("Yazdırılacak barkod yok.");
        }

        Settings s = NormalizeSettings(settings);
        StringBuilder sb = new();
        sb.Append("SIZE ").Append(s.WidthMm).Append(" mm,").Append(s.HeightMm).Append(" mm\r\n");
        sb.Append("GAP ").Append(s.GapMm).Append(" mm,0\r\n");
        sb.Append("SPEED ").Append(s.Speed).Append("\r\n");
        sb.Append("DENSITY ").Append(s.Density).Append("\r\n");
        sb.Append("DIRECTION 0\r\n");
        sb.Append("CODEPAGE UTF-8\r\n");
        foreach (Item item in list)
        {
            AppendLabel(sb, item, s);
        }

        return sb.ToString();
    }

    private static void AppendLabel(StringBuilder sb, Item item, Settings s)
    {
        int w = Mm(s.WidthMm);
        int h = Mm(s.HeightMm);
        int pad = Mm(1.5);
        int gap = Mm(2);
        int codeBand = s.ShowCodeText ? Math.Clamp(Mm(s.HeightMm * 0.16), Mm(2.8), Mm(5.5)) : 0;
        int qrSide = Math.Max(Mm(8), Math.Min(h - 2 * pad - codeBand, (int)(w * 0.42)));
        int cell = QrCellWidth(item.Code, qrSide);
        int quiet = 2;
        int modules = EstimateModules(item.Code) + 2 * quiet;
        int qrDots = modules * cell;
        int qrX = pad;
        int qrY = pad + Math.Max(0, (h - 2 * pad - codeBand - qrDots) / 2);

        sb.Append("CLS\r\n");
        // M2 + S7: enhanced QR model recommended by TSC for smartphone scanners.
        sb.Append("QRCODE ").Append(qrX).Append(',').Append(qrY).Append(",M,").Append(cell)
            .Append(",A,0,M2,S7,\"").Append(Escape(item.Code)).Append("\"\r\n");

        if (s.ShowCodeText)
        {
            int codeY = qrY + qrDots + Mm(0.4);
            int pt = FitPoints(item.Code, qrDots, prefer: 7, min: 5);
            foreach (string line in Wrap(item.Code, qrDots, pt, 2))
            {
                int textW = EstimateTextWidthDots(line, pt);
                int tx = qrX + Math.Max(0, (qrDots - textW) / 2);
                sb.Append(TextCmd(tx, codeY, pt, line));
                codeY += PtToDots(pt) + 2;
            }
        }

        int infoX = pad + qrDots + gap;
        int infoW = Math.Max(Mm(12), w - pad - infoX);
        int y = pad + Mm(0.5);
        int brandPt = FitPoints(s.Title ?? "", infoW, prefer: 11, min: 7);
        int gasPt = FitPoints(s.ShowGas ? item.Gas : null, infoW, prefer: 9, min: 6);
        int metaPt = 7;

        if (!string.IsNullOrWhiteSpace(s.Title))
        {
            y = WriteBlock(sb, infoX, y, infoW, brandPt, s.Title!, 1);
        }

        if (s.ShowGas && !string.IsNullOrWhiteSpace(item.Gas))
        {
            y = WriteBlock(sb, infoX, y, infoW, gasPt, item.Gas!, 2);
        }

        if (s.ShowSerial && !string.IsNullOrWhiteSpace(item.Serial) &&
            !string.Equals(item.Serial, item.Code, StringComparison.OrdinalIgnoreCase))
        {
            y = WriteBlock(sb, infoX, y, infoW, metaPt, item.Serial!, 1);
        }

        if (s.ShowTest && !string.IsNullOrWhiteSpace(item.Stt))
        {
            y = WriteBlock(sb, infoX, y, infoW, metaPt, "STT: " + item.Stt, 1);
        }

        if (s.ShowTest && !string.IsNullOrWhiteSpace(item.Gtt))
        {
            y = WriteBlock(sb, infoX, y, infoW, metaPt, "GTT: " + item.Gtt, 1);
        }

        if (!string.IsNullOrWhiteSpace(item.Extra))
        {
            y = WriteBlock(sb, infoX, y, infoW, metaPt, item.Extra!, 1);
        }

        if (!string.IsNullOrWhiteSpace(item.Owner))
        {
            WriteBlock(sb, infoX, y, infoW, metaPt, item.Owner!, 2);
        }

        sb.Append("PRINT 1,").Append(s.Copies).Append("\r\n");
    }

    private static int WriteBlock(StringBuilder sb, int x, int y, int widthDots, int preferPt, string text, int maxLines)
    {
        int pt = FitPoints(text, widthDots, preferPt, min: 5);
        foreach (string line in Wrap(text, widthDots, pt, maxLines))
        {
            sb.Append(TextCmd(x, y, pt, line));
            y += PtToDots(pt) + 3;
        }

        return y + 2;
    }

    // Font "0" = TSPL2 internal TrueType (stretchable in points) — better Turkish coverage than bitmap fonts 1–8.
    private static string TextCmd(int x, int y, int pt, string text) =>
        $"TEXT {x},{y},\"0\",0,{pt},{pt},\"{Escape(text)}\"\r\n";

    private static int QrCellWidth(string code, int qrSideDots)
    {
        int modules = EstimateModules(code) + 4; // quiet zone 2 modules each side
        int cell = Math.Max(1, Math.Min(10, qrSideDots / modules));
        return cell;
    }

    // Rough QR version estimate for ECC-M byte mode (enough to size the cell; printer encodes the real matrix).
    private static int EstimateModules(string code)
    {
        int n = Encoding.UTF8.GetByteCount(code);
        // Capacities (bytes, ECC M): V1=14 V2=26 V3=42 V4=62 V5=84 V6=106 V10=213 …
        int version = n <= 14 ? 1 : n <= 26 ? 2 : n <= 42 ? 3 : n <= 62 ? 4 : n <= 84 ? 5 : n <= 106 ? 6 : n <= 122 ? 7
            : n <= 152 ? 8 : n <= 180 ? 9 : n <= 213 ? 10 : n <= 251 ? 11 : n <= 287 ? 12 : 16;
        return 21 + (version - 1) * 4;
    }

    private static int FitPoints(string? text, int widthDots, int prefer, int min)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0)
        {
            return prefer;
        }

        // ~0.55em average glyph width for font 0.
        int pt = prefer;
        while (pt > min && EstimateTextWidthDots(t, pt) > widthDots)
        {
            pt--;
        }

        return pt;
    }

    private static IEnumerable<string> Wrap(string text, int widthDots, int pt, int maxLines)
    {
        string t = text.Trim();
        if (t.Length == 0)
        {
            yield break;
        }

        int chars = Math.Max(4, (int)(widthDots / (PtToDots(pt) * 0.55)));
        if (t.Length <= chars || maxLines <= 1)
        {
            yield return t.Length <= chars + 2 ? t : t[..Math.Min(t.Length, chars)];
            yield break;
        }

        List<string> lines = [];
        string[] parts = System.Text.RegularExpressions.Regex.Split(t, @"(\s+|·|-)");
        string cur = "";
        foreach (string p in parts.Where(p => p.Length > 0))
        {
            if ((cur + p).Length <= chars || cur.Length == 0)
            {
                cur += p;
            }
            else
            {
                lines.Add(cur.Trim());
                cur = p.TrimStart();
                if (lines.Count >= maxLines - 1)
                {
                    break;
                }
            }
        }

        if (cur.Length > 0 && lines.Count < maxLines)
        {
            lines.Add(cur.Trim());
        }

        foreach (string line in lines.Take(maxLines))
        {
            if (line.Length <= chars + 2)
            {
                yield return line;
            }
            else
            {
                for (int i = 0; i < line.Length && i / chars < maxLines; i += chars)
                {
                    yield return line.Substring(i, Math.Min(chars, line.Length - i));
                }
            }
        }
    }

    private static int EstimateTextWidthDots(string text, int pt) =>
        (int)Math.Ceiling(text.Length * PtToDots(pt) * 0.55);

    private static int PtToDots(int pt) => Math.Max(1, (int)Math.Round(pt * Dpi / 72.0));

    private static int Mm(double mm) => Math.Max(0, (int)Math.Round(mm * DotsPerMm));

    private static string Escape(string value) =>
        (value ?? "").Replace("\"", "\\[\"]", StringComparison.Ordinal);

    private static Item NormalizeItem(Item item) => item with
    {
        Code = (item.Code ?? "").Trim(),
        Serial = TrimOrNull(item.Serial),
        Gas = TrimOrNull(item.Gas),
        Stt = TrimOrNull(item.Stt),
        Gtt = TrimOrNull(item.Gtt),
        Owner = TrimOrNull(item.Owner),
        Extra = TrimOrNull(item.Extra)
    };

    private static Settings NormalizeSettings(Settings s) => s with
    {
        WidthMm = Math.Clamp(s.WidthMm, 20, 104),
        HeightMm = Math.Clamp(s.HeightMm, 10, 200),
        Copies = Math.Clamp(s.Copies, 1, 20),
        Title = string.IsNullOrWhiteSpace(s.Title) ? null : s.Title.Trim(),
        GapMm = Math.Clamp(s.GapMm, 0, 20),
        Speed = Math.Clamp(s.Speed, 1, 6),
        Density = Math.Clamp(s.Density, 0, 15)
    };

    private static string? TrimOrNull(string? value)
    {
        string t = (value ?? "").Trim();
        return t.Length == 0 ? null : t;
    }
}
