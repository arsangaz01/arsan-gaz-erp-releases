using ArsanGazERP.Services;

internal static class JarvisAssistantTests
{
    public static void Run()
    {
        NewsAndBriefingIntentsAreDetected();
        PcCommandsOnlyMatchTheAllowList();
        FolderCommandsStayInsideSafeFolders();
        WakeWordAcceptsWhisperSpellings();
        MailDraftsSplitReplyAndTranslation();
        AgentStepsParseToolCallsAndFinals();
        WebResearchBlocksPrivateNetworks();
        Console.WriteLine("PASS: Jarvis news/briefing intents, allow-listed PC commands, folder sandboxing, wake word and mail draft parsing (offline).");
    }

    private static void NewsAndBriefingIntentsAreDetected()
    {
        Assert(LiveInfoService.Detect("Günaydın Jarvis", []).Kind == LiveInfoKind.Briefing, "Günaydın must produce the daily briefing.");
        Assert(LiveInfoService.Detect("günün özeti İzmir", []) is { Kind: LiveInfoKind.Briefing, City: "İzmir" }, "Briefing must keep a named city.");
        Assert(LiveInfoService.Detect("bugünün haberleri neler", []) is { Kind: LiveInfoKind.News, NewsTopic: null }, "General news question.");
        Assert(LiveInfoService.Detect("LPG haberleri", []) is { Kind: LiveInfoKind.News, NewsTopic: "Enerji ve LPG" }, "LPG topic must be selected.");
        Assert(LiveInfoService.Detect("müşteriye haber ver", []).Kind == LiveInfoKind.None, "'haber ver' is not a news request.");
        Assert(LiveInfoService.Detect("Bugün hangi tahsilatlar var?", []).Kind == LiveInfoKind.None, "ERP question must not become a briefing.");
    }

    private static void PcCommandsOnlyMatchTheAllowList()
    {
        Assert(PcCommandService.TryParse("Excel'i aç") is { Kind: PcCommandKind.OpenApp, Target: "excel.exe" }, "Excel must open.");
        Assert(PcCommandService.TryParse("hesap makinesini açar mısın") is { Kind: PcCommandKind.OpenApp, Target: "calc.exe" }, "Calculator must open.");
        Assert(PcCommandService.TryParse("YouTube'u aç") is { Kind: PcCommandKind.OpenSite, Target: "https://www.youtube.com" }, "YouTube must open.");
        PcCommand? search = PcCommandService.TryParse("Google'da LPG fiyatları ara");
        Assert(search is { Kind: PcCommandKind.OpenSite } && search.Target.StartsWith("https://www.google.com/search?q=LPG", StringComparison.Ordinal),
            "Google search must carry the query.");
        Assert(PcCommandService.TryParse("Outlook web aç") is { Target: "https://outlook.office.com/mail" }, "Outlook web must be the site.");

        string[] rejected =
        [
            "cmd aç", "powershell çalıştır", "format c: çalıştır", "Teklif dosyasını aç", "/not Excel aç",
            "excel raporunda bu ay satışlar ne kadar yükseldi bunu bana detaylı anlat ve aç"
        ];
        foreach (string input in rejected)
        {
            Assert(PcCommandService.TryParse(input) is null, $"'{input}' must not run anything.");
        }
    }

    private static void FolderCommandsStayInsideSafeFolders()
    {
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        PcCommand? folder = PcCommandService.TryParse("Masaüstünde Teklifler klasörü oluştur");
        Assert(folder is { Kind: PcCommandKind.CreateFolder } && folder.Target == Path.Combine(desktop, "Teklifler"), "Desktop folder.");

        string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        folder = PcCommandService.TryParse("Belgelerde \"Arsan 2026\" adlı klasör oluştur");
        Assert(folder is not null && folder.Target == Path.Combine(documents, "Arsan 2026"), "Quoted name in Documents.");

        folder = PcCommandService.TryParse("masaüstünde \"..\\Windows\" klasörü oluştur");
        Assert(folder is { Kind: PcCommandKind.CreateFolder, Target: "" }, "Traversal names must be rejected.");
        Assert(PcCommandService.ValidateFolderName("CON") is null, "Reserved device names must be rejected.");
        Assert(PcCommandService.ValidateFolderName("a/b") is null, "Separators must be rejected.");
    }

    private static void WakeWordAcceptsWhisperSpellings()
    {
        Assert(WakeWordDetector.TryMatch("Hey Jarvis, Adana'da hava nasıl?", out string command) &&
               command == "Adana'da hava nasıl", "Wake word with a command.");
        Assert(WakeWordDetector.TryMatch("Çarvis", out command) && command.Length == 0, "Bare wake word arms listening.");
        Assert(WakeWordDetector.TryMatch("Cervis Excel'i aç", out command) && command == "Excel'i aç", "Whisper spelling variant.");
        Assert(!WakeWordDetector.TryMatch("Bugün toplantıda Jarvis'ten bahsettik", out _), "Mid-sentence mention is not a wake word.");
        Assert(!WakeWordDetector.TryMatch("Çarşıya gidiyorum", out _), "Similar words must not wake Jarvis.");
    }

    private static void MailDraftsSplitReplyAndTranslation()
    {
        MailReplyDraft draft = MailReplyDraftParser.Parse(
            "[DİL] İngilizce\n[YANIT]\nDear John,\nThanks.\nBest regards\n[TÜRKÇE]\nSayın John,\nTeşekkürler.");
        Assert(draft.Language == "İngilizce", "Language must be parsed.");
        Assert(draft.Reply == "Dear John,\nThanks.\nBest regards", "Reply must be isolated.");
        Assert(draft.TurkishTranslation == "Sayın John,\nTeşekkürler.", "Translation must be isolated.");

        draft = MailReplyDraftParser.Parse("Sadece düz metin");
        Assert(draft.Reply == "Sadece düz metin" && draft.TurkishTranslation.Length == 0, "Unformatted output stays editable.");
    }

    private static void AgentStepsParseToolCallsAndFinals()
    {
        JarvisAgentStep step = JarvisAgentProtocol.Parse(
            "```json\n{\"thought\":\"Hava için araç lazım\",\"action\":\"Weather\",\"input\":{\"city\":\"Adana\"}}\n```");
        Assert(step is { Action: "weather", Final: null } && step.Input["city"] == "Adana", "Fenced tool call must parse.");

        step = JarvisAgentProtocol.Parse("Tamam. {\"thought\":\"bitti\",\"final\":\"Adana'da hava 24 °C {güneşli}.\"} ek metin");
        Assert(step.Final == "Adana'da hava 24 °C {güneşli}.", "Braces inside strings and trailing text must not break parsing.");

        step = JarvisAgentProtocol.Parse("{\"action\":\"exchange_rates\",\"input\":{\"currencies\":[\"USD\",\"EUR\"]}}");
        Assert(step.Input["currencies"] == "USD,EUR", "Array inputs must flatten.");

        step = JarvisAgentProtocol.Parse("Merhaba! Size nasıl yardımcı olabilirim?");
        Assert(step.Final == "Merhaba! Size nasıl yardımcı olabilirim?", "Plain text is treated as the final answer.");

        string transcript = JarvisAgentProtocol.BuildTranscript(
            [new ErpChatTurn("user", "merhaba")],
            "Adana hava",
            [(new JarvisAgentStep("bak", "weather", new Dictionary<string, string> { ["city"] = "Adana" }, null), new string('x', 20_000))],
            lastStep: false,
            new DateTime(2026, 10, 10, 9, 0, 0));
        Assert(transcript.Length <= JarvisAgentProtocol.MaximumTranscriptLength, "Transcript must respect the Worker size limit.");
        Assert(transcript.Contains("10 Ekim 2026 Cumartesi", StringComparison.Ordinal), "Transcript must carry today's date.");
    }

    private static void WebResearchBlocksPrivateNetworks()
    {
        Assert(!WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("127.0.0.1")), "Loopback blocked.");
        Assert(!WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("192.168.1.10")), "LAN blocked.");
        Assert(!WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("10.0.0.5")), "10/8 blocked.");
        Assert(!WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("169.254.169.254")), "Link-local blocked.");
        Assert(!WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("::1")), "IPv6 loopback blocked.");
        Assert(!WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("fd00::1")), "IPv6 ULA blocked.");
        Assert(WebResearchService.IsPublicAddress(System.Net.IPAddress.Parse("8.8.8.8")), "Public IPv4 allowed.");

        string text = WebResearchService.HtmlToText(
            "<html><head><title>T</title></head><body><script>alert(1)</script><p>Merhaba &amp; hoş geldiniz</p><div>LPG</div></body></html>");
        Assert(text == "Merhaba & hoş geldiniz\nLPG", "HTML must reduce to readable text: " + text);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
