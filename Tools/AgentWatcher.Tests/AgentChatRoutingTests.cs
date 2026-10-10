using ArsanGazERP.Services;

internal static class AgentChatRoutingTests
{
    public static void Run()
    {
        NamedAgentAcknowledgementIsRespectfulAndTargeted();
        ExplicitCommandsRemainPermissionAndRoleBound();
        ResearchIsLimitedToTheNamedPermittedResearcher();
        OrdinaryMessagesRouteToOneAgent();
        Console.WriteLine("PASS: ERP chat named-agent acknowledgement, local command routing, public research gating, and ordinary-message routing.");
    }

    private static void NamedAgentAcknowledgementIsRespectfulAndTargeted()
    {
        const string input = "CRM fırsatını incele";
        string? acknowledgement = AgentChatRoutingPolicy.BuildAcknowledgement(
            "crm",
            "crm",
            input,
            authorizedCommand: false);
        Check(
            acknowledgement == "Efendim, isteğinizi inceliyor ve yanıtlıyorum.",
            "An explicitly addressed conversational request must not be acknowledged as an action.");
        Check(
            AgentChatRoutingPolicy.BuildAcknowledgement("sales", "crm", input, false) is null,
            "A broadcast participant that was not addressed must not claim the user's command.");
        Check(
            AgentChatRoutingPolicy.BuildAcknowledgement("crm", null, input, false) is null,
            "Ordinary unaddressed messages must not acquire named-agent acknowledgements.");
        Check(
            AgentChatRoutingPolicy.BuildAcknowledgement("crm", "crm", "/komutlar", false)
                == "Efendim, kullanabileceğiniz komutları hazırlıyorum.",
            "The help command must receive an acknowledgement without claiming a mutation.");
        Check(
            AgentChatRoutingPolicy.BuildAcknowledgement("crm", "crm", "/not Firma | Not", true)
                == "Efendim, emrinizi uygulamaya başlıyorum.",
            "An explicitly authorized local command must acknowledge that execution is starting.");
        Check(
            AgentChatRoutingPolicy.BuildAcknowledgement("crm", "crm", "/not Firma | Not", false)
                == "Efendim, talebinizi aldım; yetki ve kapsamını doğruluyorum.",
            "A disabled or misrouted command must not claim it is being executed.");
    }

    private static void ExplicitCommandsRemainPermissionAndRoleBound()
    {
        Check(
            AgentChatRoutingPolicy.NormalizeNamedAction("not ekle Firma | Not", "crm") == "/not Firma | Not",
            "A direct CRM note request must enter the existing local command executor.");
        Check(
            AgentChatRoutingPolicy.NormalizeNamedAction("görev oluştur Teklif ara | 2026-10-20", "planner")
                == "/gorev Teklif ara | 2026-10-20",
            "A direct planner task request must enter the existing ERP task executor.");
        Check(
            AgentChatRoutingPolicy.NormalizeNamedAction("takip planla Firma | 2026-10-20", "crm")
                == "/takip Firma | 2026-10-20",
            "A direct CRM follow-up request must enter the existing follow-up executor.");
        Check(
            AgentChatRoutingPolicy.NormalizeNamedAction("uzmanlara görev dağıt Arayüzü incele", "supervisor")
                == "/denetle Arayüzü incele",
            "A direct supervisor delegation request must enter the existing guarded queue.");
        Check(
            AgentChatRoutingPolicy.NormalizeNamedAction("görev oluştur Teklif ara", "sales")
                == "görev oluştur Teklif ara",
            "A natural-language action must not be redirected through an agent that does not own it.");
        Check(
            AgentChatRoutingPolicy.NormalizeNamedAction("not ekleme yöntemini anlat", "crm")
                == "not ekleme yöntemini anlat",
            "A question about an action must not be mistaken for an execution request.");

        Check(
            AgentChatRoutingPolicy.CanExecuteLocalCommand("crm", "/not Firma | Not", permissionEnabled: true),
            "A permitted CRM note command must route to the CRM agent.");
        Check(
            !AgentChatRoutingPolicy.CanExecuteLocalCommand("sales", "/not Firma | Not", permissionEnabled: true),
            "CRM writes must not route through a different agent.");
        Check(
            !AgentChatRoutingPolicy.CanExecuteLocalCommand("crm", "/not Firma | Not", permissionEnabled: false),
            "A disabled local permission must block command execution.");
        Check(
            AgentChatRoutingPolicy.CanExecuteLocalCommand("planner", "/gorev Teklif ara", permissionEnabled: true),
            "ERP task creation must remain restricted to the planner agent.");
        Check(
            !AgentChatRoutingPolicy.CanExecuteLocalCommand("planner", "/sil kayıt", permissionEnabled: true),
            "Unknown or destructive commands must remain unsupported.");

        IReadOnlyList<string> commandParticipants = AgentChatRoutingPolicy.SelectParticipantIds(
            "/gorev Teklif ara",
            "planner",
            ["crm", "sales", "finance", "planner", "researcher", "supervisor", "jarvis"]);
        Check(
            commandParticipants.SequenceEqual(["planner"]),
            "An explicit command must only be dispatched to its addressed agent.");
    }

    private static void ResearchIsLimitedToTheNamedPermittedResearcher()
    {
        const string query = "WPF sohbet arayüzü açık kaynak örnekleri";
        Check(
            AgentChatRoutingPolicy.BuildPublicResearchQuery("researcher", "researcher", query, true) == query,
            "A named researcher with web permission must pass the current request as the public search query.");
        Check(
            AgentChatRoutingPolicy.BuildPublicResearchQuery("researcher", "researcher", query, false) is null,
            "Public search must not run after its permission is disabled.");
        Check(
            AgentChatRoutingPolicy.BuildPublicResearchQuery("researcher", null, query, true) is null,
            "Unaddressed conversation history or broadcast turns must not be sent to public search.");
        Check(
            AgentChatRoutingPolicy.BuildPublicResearchQuery("crm", "crm", query, true) is null,
            "Only the researcher profile may issue public research queries.");
        Check(
            AgentChatRoutingPolicy.BuildPublicResearchQuery("researcher", "researcher", "/komutlar", true) is null,
            "Local command messages must not be sent to public search.");

        string longQuery = new string('x', 179) + "😀";
        string? boundedQuery = AgentChatRoutingPolicy.BuildPublicResearchQuery(
            "researcher",
            "researcher",
            longQuery,
            true);
        Check(boundedQuery is { Length: <= 180 } && !char.IsHighSurrogate(boundedQuery[^1]),
            "Public search queries must obey the provider limit without splitting a surrogate pair.");

        string? teamQuery = AgentChatRoutingPolicy.BuildAgentTeamResearchQuery(
            "researcher",
            "  WPF   ERP araştırması  ",
            string.Join(
                ' ',
                Enumerable.Repeat(
                    "Compare data-grid usability, keyboard navigation, and accessible filtering examples.",
                    5)));
        Check(teamQuery is { Length: <= 180 } && !char.IsWhiteSpace(teamQuery[^1]),
            "Agent-team research queries must be normalized and bounded before reaching the provider.");
        Check(teamQuery!.StartsWith("WPF ERP araştırması Compare data-grid usability", StringComparison.Ordinal),
            "Bounding an agent-team query must retain useful search terms from its title and assigned task.");
        Check(teamQuery.Split(' ').All(token => token.Length > 0),
            "Normalizing an agent-team query must not produce empty search terms.");
        Check(
            AgentChatRoutingPolicy.BuildAgentTeamResearchQuery("crm", "Title", "Task") is null &&
            AgentChatRoutingPolicy.BuildAgentTeamResearchQuery("researcher", " ", "\t") is null,
            "Agent-team public research must remain researcher-only and reject empty normalized queries.");
    }

    private static void OrdinaryMessagesRouteToOneAgent()
    {
        IReadOnlyList<string> participants = AgentChatRoutingPolicy.SelectParticipantIds(
            "selam",
            null,
            ["crm", "sales", "finance", "planner", "researcher", "supervisor", "jarvis"]);
        Check(
            participants.SequenceEqual(["jarvis"]),
            "A bare greeting or other unaddressed message must receive one conversational AI response, not a health report or specialist broadcast.");

        IReadOnlyList<string> addressedResearcher = AgentChatRoutingPolicy.SelectParticipantIds(
            "Açık kaynak WPF örneklerini araştır",
            "researcher",
            ["crm", "sales", "finance", "planner", "researcher", "supervisor", "jarvis"]);
        Check(
            addressedResearcher.SequenceEqual(["researcher"]),
            "An explicitly addressed researcher request must only reach the researcher.");

        IReadOnlyList<string> addressedCrm = AgentChatRoutingPolicy.SelectParticipantIds(
            "CRM fırsatını incele",
            "crm",
            ["crm", "sales", "finance", "planner", "researcher", "supervisor", "jarvis"]);
        Check(
            addressedCrm.SequenceEqual(["crm"]),
            "An explicitly addressed specialist request must only reach the selected specialist.");

        Check(
            AgentChatRoutingPolicy.SelectParticipantIds("selam", null, ["crm", "sales"])
                .SequenceEqual(["crm"]),
            "The routing policy must keep a single-agent fallback if neither Jarvis nor supervisor is available.");

        Check(
            AgentChatRoutingPolicy.SelectParticipantIds("selam", null, ["crm", "supervisor"])
                .SequenceEqual(["supervisor"]),
            "The routing policy must fall back to the supervisor if Jarvis is unavailable.");

        Check(
            AgentChatRoutingPolicy.SelectParticipantIds(
                "/not Firma | Not",
                null,
                ["crm", "sales", "supervisor", "jarvis"])
                .SequenceEqual(["supervisor"]),
            "An unaddressed command must keep routing to the non-mutating supervisor rather than the conversational default.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}
