using System.Globalization;
using System.Text;

namespace ArsanGazERP.Services;

internal static class WakeWordDetector
{
    public static readonly TimeSpan ArmedWindow = TimeSpan.FromSeconds(10);
    private const int MaximumLeadingTokens = 3;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");

    // Whisper's Turkish model spells the English name in several phonetic ways.
    private static readonly string[] Variants =
    [
        "jarvis", "jarviz", "carvis", "carviz", "cervis", "jervis", "javis", "jarves", "yarvis", "sarvis", "harvis",
        "jarbis", "carbis"
    ];

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', ',', '.', '!', '?', ';', ':', '-', '…'];

    public static bool TryMatch(string transcript, out string command)
    {
        ArgumentNullException.ThrowIfNull(transcript);
        command = string.Empty;
        string[] tokens = transcript.Split(Separators, StringSplitOptions.RemoveEmptyEntries);
        for (int index = 0; index < tokens.Length && index < MaximumLeadingTokens + 1; index++)
        {
            string folded = Fold(tokens[index]);
            string stem = folded.Split('\'', '’')[0];
            if (Variants.Any(variant => stem == variant || (stem.StartsWith(variant, StringComparison.Ordinal) && stem.Length - variant.Length <= 3)))
            {
                command = string.Join(' ', tokens.Skip(index + 1)).Trim();
                return true;
            }

            if (folded is not ("hey" or "hei" or "hay" or "ey" or "selam" or "merhaba" or "tamam"))
            {
                return false;
            }
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
                _ => character
            });
        }

        return builder.ToString();
    }
}
