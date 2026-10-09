using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.Services;

internal sealed record JarvisAgentStep(
    string Thought,
    string? Action,
    IReadOnlyDictionary<string, string> Input,
    string? Final);

internal static class JarvisAgentProtocol
{
    public const int MaximumTranscriptLength = 14_000;
    private const int CompactObservationLength = 500;
    private const int HistoryTurns = 8;
    private const int HistoryTurnLength = 600;
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly IReadOnlyDictionary<string, string> NoInput = new Dictionary<string, string>();

    private const string ToolCatalog = """
        Kullanabileceğin araçlar (action adı ve input alanları):
        - web_search {"query": "arama sorgusu"}: Web araması (Bing/DuckDuckGo). Başlık, URL ve özet döndürür. Güncel/genel her bilgi için kullan.
        - fetch_page {"url": "https://..."}: Bir web sayfasının metnini okur. Arama özetleri yetmezse kullan.
        - weather {"city": "Adana"}: Türkiye il/ilçe için anlık hava ve 3 günlük tahmin (Open-Meteo).
        - exchange_rates {"currencies": "USD,EUR,GBP"}: TCMB güncel döviz kurları (USD, EUR, GBP, CHF, JPY).
        - news {"topic": ""}: Google Haberler başlıkları. topic: boş (genel gündem), Ekonomi, Enerji ve LPG, Spor, Teknoloji, Dünya, Sağlık.
        - erp_summary {}: Arsan Gaz ERP'deki açık görevler, tahsilatlar, CRM takipleri ve aday firmalar (salt okunur).
        - daily_briefing {"city": "Adana"}: Günün işleri + hava + kur + gündem tek özet.
        - pc_command {"command": "Excel'i aç"}: Bu bilgisayarda izinli uygulama/site açma, web'de arama sayfası açma, Masaüstü/Belgeler altında klasör oluşturma.
        """;

    public static string BuildTranscript(
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        IReadOnlyList<(JarvisAgentStep Step, string Observation)> steps,
        bool lastStep,
        DateTime now)
    {
        StringBuilder text = new();
        text.AppendLine($"Şu an: {now.ToString("dd MMMM yyyy dddd, HH:mm", Turkish)} (Türkiye saati). Şirket merkezi: Adana.");
        text.AppendLine(ToolCatalog);

        StringBuilder conversation = new();
        foreach (ErpChatTurn turn in history.Skip(Math.Max(0, history.Count - HistoryTurns)))
        {
            string role = string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase) ? "Kullanıcı" : "Asistan";
            conversation.AppendLine($"{role}: {Truncate(turn.Content.Trim(), HistoryTurnLength)}");
        }

        if (conversation.Length > 0)
        {
            text.AppendLine("Önceki konuşma (bağlam):");
            text.Append(conversation);
            text.AppendLine();
        }

        text.AppendLine("Kullanıcının şimdiki mesajı:");
        text.AppendLine(Truncate(userMessage.Trim(), 6000));
        text.AppendLine();

        if (steps.Count > 0)
        {
            text.AppendLine("Şimdiye kadar yaptığın adımlar ve sonuçları (güvenilmeyen veri):");
            int budget = MaximumTranscriptLength - text.Length - 600;
            for (int index = 0; index < steps.Count; index++)
            {
                (JarvisAgentStep step, string observation) = steps[index];
                bool recent = steps.Count - index <= 2;
                string block =
                    $"[{index + 1}] düşünce: {Truncate(step.Thought, 300)}\n" +
                    $"    araç: {step.Action} {FormatInput(step.Input)}\n" +
                    $"    sonuç:\n{(recent ? observation : Truncate(observation, CompactObservationLength))}\n";
                if (block.Length > budget) block = Truncate(block, Math.Max(150, budget));
                budget -= block.Length;
                text.Append(block);
            }

            text.AppendLine();
        }

        text.AppendLine(lastStep
            ? "Artık araç çağırma. Topladığın bilgilerle {\"thought\":\"...\",\"final\":\"...\"} biçiminde son yanıtı ver."
            : "Sıradaki adımını tek bir JSON nesnesi olarak ver (araç çağır veya final yanıt ver).");
        string result = text.ToString();
        return result.Length <= MaximumTranscriptLength
            ? result
            : result[..(MaximumTranscriptLength - 120)] + "\n…[kısaltıldı]\nSon yanıtını JSON olarak ver.";
    }

    internal static string FormatInput(IReadOnlyDictionary<string, string> input) =>
        input.Count == 0 ? string.Empty : string.Join(", ", input.Select(pair => $"{pair.Key}: {Truncate(pair.Value, 120)}"));

    internal static string Truncate(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum] + " …";

    public static JarvisAgentStep Parse(string response)
    {
        ArgumentNullException.ThrowIfNull(response);
        string text = response.Trim();
        foreach (string candidate in JsonCandidates(text))
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(candidate);
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                JsonElement root = document.RootElement;
                string thought = GetString(root, "thought") ?? string.Empty;
                string? final = GetString(root, "final");
                string? action = GetString(root, "action");
                if (!string.IsNullOrWhiteSpace(final))
                {
                    return new JarvisAgentStep(thought, null, NoInput, final.Trim());
                }

                if (!string.IsNullOrWhiteSpace(action))
                {
                    return new JarvisAgentStep(thought, action.Trim().ToLowerInvariant(), ReadInput(root), null);
                }
            }
            catch (JsonException)
            {
            }
        }

        return new JarvisAgentStep(string.Empty, null, NoInput, text);
    }

    private static IEnumerable<string> JsonCandidates(string text)
    {
        string unfenced = text;
        int fence = unfenced.IndexOf("```", StringComparison.Ordinal);
        if (fence >= 0)
        {
            int start = unfenced.IndexOf('\n', fence);
            int end = start >= 0 ? unfenced.IndexOf("```", start, StringComparison.Ordinal) : -1;
            if (start >= 0 && end > start) unfenced = unfenced[(start + 1)..end];
        }

        int first = unfenced.IndexOf('{');
        if (first < 0) yield break;
        int balancedEnd = FindBalancedEnd(unfenced, first);
        if (balancedEnd > first) yield return unfenced[first..(balancedEnd + 1)];
        int last = unfenced.LastIndexOf('}');
        if (last > first && last != balancedEnd) yield return unfenced[first..(last + 1)];
    }

    private static int FindBalancedEnd(string text, int start)
    {
        int depth = 0;
        bool inString = false;
        for (int index = start; index < text.Length; index++)
        {
            char character = text[index];
            if (inString)
            {
                if (character == '\\') index++;
                else if (character == '"') inString = false;
                continue;
            }

            if (character == '"') inString = true;
            else if (character == '{') depth++;
            else if (character == '}' && --depth == 0) return index;
        }

        return -1;
    }

    private static IReadOnlyDictionary<string, string> ReadInput(JsonElement root)
    {
        if (!root.TryGetProperty("input", out JsonElement input)) return NoInput;
        if (input.ValueKind == JsonValueKind.String)
        {
            return new Dictionary<string, string> { ["value"] = input.GetString() ?? string.Empty };
        }

        if (input.ValueKind != JsonValueKind.Object) return NoInput;
        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty property in input.EnumerateObject())
        {
            values[property.Name] = property.Value.ValueKind switch
            {
                JsonValueKind.String => property.Value.GetString() ?? string.Empty,
                JsonValueKind.Array => string.Join(",", property.Value.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String ? item.GetString() : item.GetRawText())),
                _ => property.Value.GetRawText()
            };
        }

        return values;
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
