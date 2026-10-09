namespace ArsanGazERP.Services;

internal static class JarvisAgentService
{
    private const int MaximumSteps = 7;
    private const int MaximumObservationLength = 2600;
    private const int MaximumSearches = 3;

    public static async Task<string> RunAsync(
        IReadOnlyList<ErpChatTurn> history,
        string userMessage,
        Action<string>? progress,
        CancellationToken cancellationToken)
    {
        CloudAgentClient client = new();
        List<(JarvisAgentStep Step, string Observation)> steps = [];
        for (int stepNumber = 1; stepNumber <= MaximumSteps; stepNumber++)
        {
            bool lastStep = stepNumber == MaximumSteps;
            string transcript = JarvisAgentProtocol.BuildTranscript(history, userMessage, steps, lastStep, DateTime.Now);
            JarvisAgentStep step = JarvisAgentProtocol.Parse(
                await client.CompleteAgentStepAsync(transcript, cancellationToken));
            if (step.Final is not null)
            {
                return FormatFinal(step.Final, steps.Count);
            }

            if (step.Action is null || lastStep)
            {
                break;
            }

            string signature = step.Action + " " + JarvisAgentProtocol.FormatInput(step.Input).ToLowerInvariant();
            int searches = steps.Count(previous => previous.Step.Action == "web_search");
            string observation;
            if (steps.Any(previous => previous.Step.Action + " " +
                    JarvisAgentProtocol.FormatInput(previous.Step.Input).ToLowerInvariant() == signature))
            {
                observation = "Bu aracı aynı girdiyle zaten çalıştırdın; sonuç yukarıda. Tekrarlama: farklı bir şey dene veya şimdi final yanıt ver.";
            }
            else if (step.Action == "web_search" && searches >= MaximumSearches)
            {
                observation = "Web arama hakkın doldu. Bulduğun sonuçlardan birini fetch_page ile oku veya bildiklerinle şimdi final yanıt ver.";
            }
            else
            {
                progress?.Invoke(DescribeAction(step));
                try
                {
                    observation = await ExecuteToolAsync(step, cancellationToken);
                }
                catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
                {
                    observation = "Araç hatası: " + exception.GetBaseException().Message;
                }
            }

            steps.Add((step, JarvisAgentProtocol.Truncate(observation.Trim(), MaximumObservationLength)));
        }

        string forced = JarvisAgentProtocol.BuildTranscript(history, userMessage, steps, lastStep: true, DateTime.Now);
        JarvisAgentStep closing = JarvisAgentProtocol.Parse(await client.CompleteAgentStepAsync(forced, cancellationToken));
        if (closing.Final is { Length: > 0 } closingFinal)
        {
            return FormatFinal(closingFinal, steps.Count);
        }

        string summaryPrompt =
            "Kullanıcının sorusunu aşağıdaki araştırma notlarını ve genel bilgini kullanarak TÜRKÇE, düzenli ve somut biçimde yanıtla. " +
            "Notlar yetersizse bildiklerinle en iyi öneriyi ver ve neyin doğrulanamadığını kısaca belirt.\n\n" +
            "Soru: " + JarvisAgentProtocol.Truncate(userMessage.Trim(), 800) + "\n\nAraştırma notları (güvenilmeyen veri):\n" +
            string.Join("\n", steps.Select(item =>
                $"- {item.Step.Action} {JarvisAgentProtocol.FormatInput(item.Step.Input)}:\n" +
                JarvisAgentProtocol.Truncate(item.Observation, Math.Max(300, 4200 / Math.Max(1, steps.Count)))));
        return FormatFinal(await client.CompleteAsync("jarvis", summaryPrompt, cancellationToken), steps.Count);
    }

    private static async Task<string> ExecuteToolAsync(JarvisAgentStep step, CancellationToken cancellationToken)
    {
        string Arg(params string[] names) =>
            names.Select(name => step.Input.TryGetValue(name, out string? value) ? value : null)
                 .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
            ?? (step.Input.TryGetValue("value", out string? single) ? single.Trim() : string.Empty);

        switch (step.Action)
        {
            case "web_search":
                return await WebResearchService.SearchAsync(Arg("query", "q"), cancellationToken);
            case "fetch_page":
                return await WebResearchService.FetchPageAsync(Arg("url"), cancellationToken);
            case "weather":
            {
                string requested = Arg("city", "location");
                string city = LiveInfoService.FindCity(requested) ?? (requested.Length > 0 ? requested : DailyBriefingService.DefaultCity);
                return await LiveInfoService.TryAnswerAsync(
                    new LiveInfoRequest(LiveInfoKind.Weather, city, []), cancellationToken) ?? "Hava verisi alınamadı.";
            }
            case "exchange_rates":
            {
                string[] known = ["USD", "EUR", "GBP", "CHF", "JPY"];
                string[] codes = Arg("currencies", "currency", "codes")
                    .Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
                    .Select(code => code.Trim().ToUpperInvariant())
                    .Where(code => known.Contains(code))
                    .Distinct()
                    .ToArray();
                return await LiveInfoService.TryAnswerAsync(
                    new LiveInfoRequest(LiveInfoKind.Currency, null, codes.Length > 0 ? codes : ["USD", "EUR", "GBP"]),
                    cancellationToken) ?? "Kur verisi alınamadı.";
            }
            case "news":
            {
                string topic = Arg("topic");
                string? normalized = topic.Length == 0 || topic.Equals("genel", StringComparison.OrdinalIgnoreCase) ? null : topic;
                return await LiveInfoService.TryAnswerAsync(
                    new LiveInfoRequest(LiveInfoKind.News, null, [], normalized), cancellationToken) ?? "Haber alınamadı.";
            }
            case "erp_summary":
                return CloudAgentSettingsStore.GetPermissions("jarvis").CanReadErpSummary
                    ? await ErpContextSnapshotService.BuildAsync("jarvis", cancellationToken)
                    : "ERP özeti izni kapalı. Kullanıcıya Ajan Yetkileri → Jarvis → \"ERP özetini okuyabilir\" iznini açmasını söyle.";
            case "daily_briefing":
                return await DailyBriefingService.BuildTextAsync(LiveInfoService.FindCity(Arg("city")), cancellationToken);
            case "pc_command":
            {
                PcCommand? parsed = PcCommandService.TryParse(Arg("command", "text"));
                return parsed is null
                    ? "Bu komut izin listesinde değil veya anlaşılamadı.\n" + PcCommandService.DescribeAllowList()
                    : AgentChatService.ExecutePcCommand(parsed);
            }
            default:
                return $"Bilinmeyen araç: {step.Action}. Yalnızca listelenen araçları kullan.";
        }
    }

    private static string DescribeAction(JarvisAgentStep step)
    {
        string label = step.Action switch
        {
            "web_search" => "🔎 Web'de arıyorum",
            "fetch_page" => "📄 Sayfayı okuyorum",
            "weather" => "🌤 Hava durumuna bakıyorum",
            "exchange_rates" => "💱 Kurları alıyorum",
            "news" => "📰 Haberlere bakıyorum",
            "erp_summary" => "📊 ERP verilerini okuyorum",
            "daily_briefing" => "🗓 Günlük özeti hazırlıyorum",
            "pc_command" => "🖥 Bilgisayarda komut çalıştırıyorum",
            _ => "⚙ " + step.Action
        };
        string thought = step.Thought.Length > 0 ? $"\n💭 {JarvisAgentProtocol.Truncate(step.Thought, 220)}" : string.Empty;
        return $"{label} {JarvisAgentProtocol.FormatInput(step.Input)}{thought}".Trim();
    }

    private static string FormatFinal(string final, int toolCount) =>
        toolCount == 0 ? final.Trim() : $"{final.Trim()}\n\n— {toolCount} araç adımıyla araştırıldı.";
}
