using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;

namespace ArsanGazERP.Services;

internal enum PcCommandKind
{
    OpenApp,
    OpenSite,
    CreateFolder
}

internal sealed record PcCommand(PcCommandKind Kind, string Label, string Target);

internal static class PcCommandService
{
    private const int MaximumCommandLength = 140;
    private const int MaximumFolderNameLength = 80;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    private static readonly (string Label, string Executable, string[] Names)[] Apps =
    [
        ("Not Defteri", "notepad.exe", ["not defteri", "notepad"]),
        ("Hesap Makinesi", "calc.exe", ["hesap makinesi", "hesap makinasi"]),
        ("Dosya Gezgini", "explorer.exe", ["dosya gezgini", "gezgin", "explorer"]),
        ("Paint", "mspaint.exe", ["paint"]),
        ("Görev Yöneticisi", "taskmgr.exe", ["gorev yoneticisi"]),
        ("Excel", "excel.exe", ["excel"]),
        ("Word", "winword.exe", ["word"]),
        ("Outlook", "outlook.exe", ["outlook"]),
        ("Google Chrome", "chrome.exe", ["chrome", "krom"]),
        ("Microsoft Edge", "msedge.exe", ["edge"])
    ];

    private static readonly (string Label, string Url, string? SearchUrl, string[] Names)[] Sites =
    [
        ("Google", "https://www.google.com", "https://www.google.com/search?q=", ["google"]),
        ("YouTube", "https://www.youtube.com", "https://www.youtube.com/results?search_query=", ["youtube", "yutup"]),
        ("Gmail", "https://mail.google.com", null, ["gmail"]),
        ("WhatsApp Web", "https://web.whatsapp.com", null, ["whatsapp"]),
        ("Google Haritalar", "https://maps.google.com", "https://www.google.com/maps/search/", ["harita", "haritalar"]),
        ("Google Haberler", "https://news.google.com/?hl=tr&gl=TR&ceid=TR:tr", null, ["haber sitesi", "google haberler"]),
        ("e-Devlet", "https://www.turkiye.gov.tr", null, ["e-devlet", "edevlet", "e devlet"]),
        ("GİB", "https://www.gib.gov.tr", null, ["gib", "gelir idaresi"]),
        ("TCMB", "https://www.tcmb.gov.tr", null, ["tcmb", "merkez bankasi"]),
        ("Outlook Web", "https://outlook.office.com/mail", null, ["outlook web"]),
        ("OneDrive", "https://onedrive.live.com", null, ["onedrive"])
    ];

    private static readonly string[] OpenVerbs = ["ac", "acar misin", "acabilir misin", "baslat", "calistir", "git"];
    private static readonly string[] SearchVerbs = ["ara", "arat", "bul"];
    private static readonly string[] FolderVerbs = ["olustur", "yarat", "yap"];

    public static PcCommand? TryParse(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        string text = input.Trim();
        if (text.Length == 0 || text.Length > MaximumCommandLength || text.StartsWith('/'))
        {
            return null;
        }

        string folded = Fold(text);
        string[] words = folded.Split([' ', '\t', ',', '.', '!', '?'], StringSplitOptions.RemoveEmptyEntries);

        if (ContainsWord(folded, "klasor") && FolderVerbs.Any(verb => EndsWithVerb(folded, verb)))
        {
            return TryParseFolder(text);
        }

        foreach ((string label, string url, string? searchUrl, string[] names) in Sites)
        {
            string? name = names.FirstOrDefault(candidate => ContainsWord(folded, candidate));
            if (name is null)
            {
                continue;
            }

            if (searchUrl is not null && SearchVerbs.Any(verb => EndsWithVerb(folded, verb)))
            {
                string query = ExtractSearchQuery(text, name);
                if (query.Length > 0)
                {
                    return new PcCommand(PcCommandKind.OpenSite, $"{label} araması: {query}", searchUrl + Uri.EscapeDataString(query));
                }
            }

            if (OpenVerbs.Any(verb => EndsWithVerb(folded, verb)) && words.Length <= 6)
            {
                return new PcCommand(PcCommandKind.OpenSite, label, url);
            }
        }

        if (!OpenVerbs.Any(verb => EndsWithVerb(folded, verb)) || words.Length > 6)
        {
            return null;
        }

        foreach ((string label, string executable, string[] names) in Apps)
        {
            if (names.Any(name => ContainsWord(folded, name)))
            {
                return new PcCommand(PcCommandKind.OpenApp, label, executable);
            }
        }

        return null;
    }

    public static string Execute(PcCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        switch (command.Kind)
        {
            case PcCommandKind.OpenApp:
            case PcCommandKind.OpenSite:
                using (Process.Start(new ProcessStartInfo(command.Target) { UseShellExecute = true }))
                {
                }

                return $"{command.Label} açıldı.";
            case PcCommandKind.CreateFolder:
                if (Directory.Exists(command.Target))
                {
                    return $"Klasör zaten var: {command.Target}";
                }

                Directory.CreateDirectory(command.Target);
                return $"Klasör oluşturuldu: {command.Target}";
            default:
                throw new InvalidOperationException("Desteklenmeyen bilgisayar komutu.");
        }
    }

    public static string DescribeAllowList() =>
        "İzinli uygulamalar: " + string.Join(", ", Apps.Select(app => app.Label)) + Environment.NewLine +
        "İzinli siteler: " + string.Join(", ", Sites.Select(site => site.Label)) + Environment.NewLine +
        "Klasör: yalnızca Masaüstü veya Belgeler altında (ör. \"Masaüstünde Teklifler klasörü oluştur\").";

    internal static string? ValidateFolderName(string name)
    {
        string trimmed = name.Trim().Trim('"', '\'', '“', '”');
        if (trimmed.Length is 0 or > MaximumFolderNameLength ||
            trimmed is "." or ".." ||
            trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            trimmed.EndsWith('.') ||
            trimmed.Contains("..", StringComparison.Ordinal))
        {
            return null;
        }

        string reserved = trimmed.Split('.')[0].ToUpperInvariant();
        return reserved is "CON" or "PRN" or "AUX" or "NUL" ||
               (reserved.Length == 4 && (reserved.StartsWith("COM", StringComparison.Ordinal) ||
                                         reserved.StartsWith("LPT", StringComparison.Ordinal)) &&
                char.IsDigit(reserved[3]))
            ? null
            : trimmed;
    }

    private static PcCommand? TryParseFolder(string text)
    {
        string folded = Fold(text);
        bool documents = ContainsWord(folded, "belgeler") || ContainsWord(folded, "belgelerde") ||
                         ContainsWord(folded, "dokumanlar") || ContainsWord(folded, "belgelerim");
        string baseFolder = Environment.GetFolderPath(documents
            ? Environment.SpecialFolder.MyDocuments
            : Environment.SpecialFolder.DesktopDirectory);
        string baseLabel = documents ? "Belgeler" : "Masaüstü";

        string? name = ExtractQuoted(text) ?? ExtractFolderName(text);
        string? safeName = name is null ? null : ValidateFolderName(name);
        if (safeName is null)
        {
            return new PcCommand(PcCommandKind.CreateFolder, string.Empty, string.Empty);
        }

        string target = Path.GetFullPath(Path.Combine(baseFolder, safeName));
        string root = Path.GetFullPath(baseFolder).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return target.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? new PcCommand(PcCommandKind.CreateFolder, $"{baseLabel}\\{safeName}", target)
            : new PcCommand(PcCommandKind.CreateFolder, string.Empty, string.Empty);
    }

    private static string? ExtractQuoted(string text)
    {
        int start = text.IndexOfAny(['"', '“', '\'']);
        if (start < 0)
        {
            return null;
        }

        int end = text.IndexOfAny(['"', '”', '\''], start + 1);
        return end > start + 1 ? text[(start + 1)..end] : null;
    }

    private static string? ExtractFolderName(string text)
    {
        string[] tokens = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        HashSet<string> filler = new(StringComparer.Ordinal)
        {
            "masaustu", "masaustune", "masaustunde", "belgeler", "belgelere", "belgelerde", "belgelerim", "dokumanlar",
            "bir", "yeni", "adli", "adinda", "isimli", "olarak", "icin", "lutfen", "jarvis", "klasor", "klasoru",
            "olustur", "olusturur", "musun", "misin", "yarat", "yap", "bana", "da", "de", "diye"
        };
        List<string> nameTokens = tokens
            .Where(token => !filler.Contains(Fold(token).Trim(',', '.', '!', '?')))
            .Select(token => token.Trim(',', '.', '!', '?'))
            .Where(token => token.Length > 0)
            .ToList();
        return nameTokens.Count == 0 ? null : string.Join(' ', nameTokens);
    }

    private static string ExtractSearchQuery(string text, string siteName)
    {
        string folded = Fold(text);
        int siteIndex = folded.IndexOf(siteName, StringComparison.Ordinal);
        if (siteIndex < 0)
        {
            return string.Empty;
        }

        int afterSite = text.IndexOf(' ', siteIndex + siteName.Length);
        if (afterSite < 0)
        {
            return string.Empty;
        }

        string[] tokens = text[(afterSite + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int take = tokens.Length;
        while (take > 0 && SearchVerbs.Concat(["misin", "musun", "bana", "lutfen"])
                   .Contains(Fold(tokens[take - 1]).Trim('.', '!', '?')))
        {
            take--;
        }

        return string.Join(' ', tokens.Take(take)).Trim('"', '\'', ' ');
    }

    private static bool EndsWithVerb(string folded, string verb)
    {
        string trimmed = folded.TrimEnd('.', '!', '?', ' ');
        foreach (string suffix in new[] { "", " lutfen", " misin", " musun", " jarvis" })
        {
            if (trimmed.EndsWith(" " + verb + suffix, StringComparison.Ordinal) || trimmed == verb + suffix)
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsWord(string foldedText, string word)
    {
        int index = foldedText.IndexOf(word, StringComparison.Ordinal);
        while (index >= 0)
        {
            bool startOk = index == 0 || !char.IsLetter(foldedText[index - 1]);
            if (startOk)
            {
                return true;
            }

            index = foldedText.IndexOf(word, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string Fold(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text.ToLower(Turkish))
        {
            builder.Append(character switch
            {
                'ç' => 'c',
                'ğ' => 'g',
                'ı' => 'i',
                'ö' => 'o',
                'ş' => 's',
                'ü' => 'u',
                'â' => 'a',
                'î' => 'i',
                'û' => 'u',
                _ => character
            });
        }

        return builder.ToString();
    }
}
