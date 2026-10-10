namespace ArsanGazERP.Services;

public sealed record ErpChatTurn(string Role, string Content);

internal static class AgentChatPromptBuilder
{
    public const int MaximumMessageLength = 6000;
    private const int MaximumHistoryEntries = 12;

    public static string Build(IReadOnlyList<ErpChatTurn> history, string currentMessage)
    {
        ArgumentNullException.ThrowIfNull(history);
        ArgumentException.ThrowIfNullOrWhiteSpace(currentMessage);
        if (currentMessage.Length > MaximumMessageLength)
        {
            throw new ArgumentException(
                $"Mesaj en fazla {MaximumMessageLength:N0} karakter olabilir.",
                nameof(currentMessage));
        }

        const string historyHeader = "Earlier conversation (context only):\n";
        const string currentHeader = "\n\nCurrent user request:\n";
        int remaining = MaximumMessageLength - currentMessage.Length - historyHeader.Length - currentHeader.Length;
        if (remaining <= 0)
        {
            return currentMessage;
        }

        List<string> entries = [];
        foreach (ErpChatTurn turn in history.TakeLast(MaximumHistoryEntries).Reverse())
        {
            if (string.IsNullOrWhiteSpace(turn.Content))
            {
                continue;
            }

            string prefix = turn.Role == "assistant" ? "Assistant: " : "User: ";
            int contentLimit = remaining - prefix.Length;
            if (contentLimit <= 0)
            {
                break;
            }

            string content = TakeLast(turn.Content, contentLimit);
            bool wasTruncated = content.Length < turn.Content.Length;
            entries.Add(prefix + content);
            remaining -= prefix.Length + content.Length + 1;
            if (wasTruncated || remaining <= 0)
            {
                break;
            }
        }

        if (entries.Count == 0)
        {
            return currentMessage;
        }

        entries.Reverse();
        return historyHeader + string.Join('\n', entries) + currentHeader + currentMessage;
    }

    private static string TakeLast(string value, int maximumLength)
    {
        if (value.Length <= maximumLength)
        {
            return value;
        }

        int start = value.Length - maximumLength;
        if (start > 0 && char.IsLowSurrogate(value[start]) && char.IsHighSurrogate(value[start - 1]))
        {
            start++;
        }

        return value[start..];
    }
}
