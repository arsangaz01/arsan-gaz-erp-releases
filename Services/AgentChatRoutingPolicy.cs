namespace ArsanGazERP.Services;

internal static class AgentChatRoutingPolicy
{
    private const int MaximumPublicResearchQueryLength = 180;

    public static string NormalizeNamedAction(string input, string? addressedAgentId)
    {
        if (string.IsNullOrWhiteSpace(input) ||
            addressedAgentId is null ||
            input.StartsWith("/", StringComparison.Ordinal))
        {
            return input;
        }

        (string AgentId, string Phrase, string Command)[] actions =
        [
            ("crm", "not ekle", "/not"),
            ("crm", "takip planla", "/takip"),
            ("planner", "görev oluştur", "/gorev"),
            ("finance", "tahsilat ekle", "/tahsilat"),
            ("finance", "tahsilatları listele", "/tahsilatlar"),
            ("supervisor", "uzmanlara görev dağıt", "/denetle"),
            ("supervisor", "görev dağıt", "/denetle")
        ];

        foreach ((string agentId, string phrase, string command) in actions)
        {
            if (!string.Equals(addressedAgentId, agentId, StringComparison.OrdinalIgnoreCase) ||
                !TryStripActionPhrase(input, phrase, out string arguments))
            {
                continue;
            }

            return arguments.Length == 0 ? command : command + " " + arguments;
        }

        return input;
    }

    public static bool ShouldForwardRecommendationsToCursor(string input)
    {
        if (string.IsNullOrWhiteSpace(input) || input.StartsWith("/", StringComparison.Ordinal))
        {
            return false;
        }

        string text = input.Trim();
        bool mentionsCursor = text.Contains("cursor", StringComparison.OrdinalIgnoreCase);
        bool mentionsRecommendations = text.Contains("öneri", StringComparison.OrdinalIgnoreCase)
            || text.Contains("uygula", StringComparison.OrdinalIgnoreCase)
            || text.Contains("ilet", StringComparison.OrdinalIgnoreCase);
        return mentionsCursor && mentionsRecommendations;
    }

    public static IReadOnlyList<string> SelectParticipantIds(
        string input,
        string? addressedAgentId,
        IReadOnlyList<string> allAgentIds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        ArgumentNullException.ThrowIfNull(allAgentIds);

        if (input.StartsWith("/", StringComparison.Ordinal))
        {
            return [addressedAgentId ?? "supervisor"];
        }

        if (addressedAgentId is not null)
        {
            return allAgentIds.Contains(addressedAgentId, StringComparer.OrdinalIgnoreCase)
                ? [addressedAgentId]
                : [];
        }

        // Keep chat conversational without waking every specialist. Slash
        // commands still take the supervisor path above unless explicitly
        // addressed to their permissioned owner.
        if (allAgentIds.Contains("jarvis", StringComparer.OrdinalIgnoreCase))
        {
            return ["jarvis"];
        }

        if (allAgentIds.Contains("supervisor", StringComparer.OrdinalIgnoreCase))
        {
            return ["supervisor"];
        }

        return allAgentIds.Take(1).ToArray();
    }

    public static string? BuildAcknowledgement(
        string profileId,
        string? addressedAgentId,
        string input,
        bool authorizedCommand)
    {
        if (!string.Equals(profileId, addressedAgentId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string command = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (string.Equals(command, "/komutlar", StringComparison.OrdinalIgnoreCase))
        {
            return "Efendim, kullanabileceğiniz komutları hazırlıyorum.";
        }

        if (!input.StartsWith("/", StringComparison.Ordinal))
        {
            return "Efendim, isteğinizi inceliyor ve yanıtlıyorum.";
        }

        return authorizedCommand
            ? "Efendim, emrinizi uygulamaya başlıyorum."
            : "Efendim, talebinizi aldım; yetki ve kapsamını doğruluyorum.";
    }

    public static bool CanExecuteLocalCommand(string agentId, string input, bool permissionEnabled)
    {
        if (!permissionEnabled)
        {
            return false;
        }

        string command = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        return command.ToLowerInvariant() switch
        {
            "/not" or "/takip" => agentId == "crm",
            "/gorev" => agentId == "planner",
            "/tahsilat" => agentId == "finance",
            "/denetle" => agentId is "supervisor" or "jarvis",
            _ => false
        };
    }

    private static bool TryStripActionPhrase(string input, string phrase, out string arguments)
    {
        arguments = string.Empty;
        if (!input.StartsWith(phrase, StringComparison.OrdinalIgnoreCase) ||
            (input.Length > phrase.Length &&
             !char.IsWhiteSpace(input[phrase.Length]) &&
             input[phrase.Length] != ':'))
        {
            return false;
        }

        arguments = input[phrase.Length..].TrimStart(' ', '\t', '\r', '\n', ':');
        return true;
    }

    public static string? BuildPublicResearchQuery(
        string profileId,
        string? addressedAgentId,
        string input,
        bool canSearchPublicWeb)
    {
        if (!string.Equals(profileId, "researcher", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(addressedAgentId, profileId, StringComparison.OrdinalIgnoreCase) ||
            !canSearchPublicWeb ||
            input.StartsWith("/", StringComparison.Ordinal))
        {
            return null;
        }

        return NormalizePublicResearchQuery(input);
    }

    public static string? BuildAgentTeamResearchQuery(
        string agentId,
        string? title,
        string? task)
    {
        if (!string.Equals(agentId, "researcher", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string combinedQuery = string.Join(
            ' ',
            new[] { title, task }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return NormalizePublicResearchQuery(combinedQuery);
    }

    private static string? NormalizePublicResearchQuery(string? query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        string normalized = string.Join(
            ' ',
            query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (normalized.Length <= MaximumPublicResearchQueryLength)
        {
            return normalized;
        }

        int length = MaximumPublicResearchQueryLength;
        if (char.IsHighSurrogate(normalized[length - 1]) && char.IsLowSurrogate(normalized[length]))
        {
            length--;
        }

        int wordBoundary = normalized.LastIndexOf(' ', length - 1);
        if (wordBoundary >= MaximumPublicResearchQueryLength / 2)
        {
            length = wordBoundary;
        }

        string bounded = normalized[..length].TrimEnd();
        return bounded.Length == 0 ? null : bounded;
    }
}
