namespace ArsanGazERP.Services;

internal sealed record MailReplyDraft(string Language, string Reply, string TurkishTranslation);

internal static class MailReplyDraftParser
{
    internal const string LanguageMarker = "[DİL]";
    internal const string ReplyMarker = "[YANIT]";
    internal const string TranslationMarker = "[TÜRKÇE]";

    public static MailReplyDraft Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text = response.Trim();
        int replyIndex = text.IndexOf(ReplyMarker, StringComparison.OrdinalIgnoreCase);
        if (replyIndex < 0)
        {
            return new MailReplyDraft("Bilinmiyor", text, string.Empty);
        }

        int replyStart = replyIndex + ReplyMarker.Length;
        int translationIndex = text.IndexOf(TranslationMarker, replyStart, StringComparison.OrdinalIgnoreCase);
        int languageIndex = text.IndexOf(LanguageMarker, StringComparison.OrdinalIgnoreCase);
        if (languageIndex < 0) languageIndex = text.IndexOf("[DIL]", StringComparison.OrdinalIgnoreCase);
        string language = languageIndex >= 0 && languageIndex < replyIndex
            ? text[(languageIndex + LanguageMarker.Length)..replyIndex].Trim().TrimStart(':').Trim()
            : string.Empty;
        string reply = translationIndex >= 0 ? text[replyStart..translationIndex] : text[replyStart..];
        string translation = translationIndex >= 0 ? text[(translationIndex + TranslationMarker.Length)..] : string.Empty;
        return new MailReplyDraft(
            language.Length == 0 ? "Bilinmiyor" : language,
            reply.Trim().TrimStart(':').Trim(),
            translation.Trim().TrimStart(':').Trim());
    }
}
