using ArsanGazERP.Services;

internal static class LiveInfoTests
{
    public static void Run()
    {
        WeatherQuestionsResolveTheProvince();
        FollowUpsReuseThePreviousLiveQuestion();
        CurrencyQuestionsSelectMentionedCodes();
        OrdinaryErpQuestionsStayWithTheModel();
        Console.WriteLine("PASS: Jarvis live weather/currency intent, province parsing and follow-up detection (offline).");
    }

    private static void WeatherQuestionsResolveTheProvince()
    {
        LiveInfoRequest request = LiveInfoService.Detect("Adana'da hava durumu nasıl?", []);
        Assert(request is { Kind: LiveInfoKind.Weather, City: "Adana" }, "Apostrophe suffix must still match Adana.");

        request = LiveInfoService.Detect("izmirde yarın yağmur var mı", []);
        Assert(request is { Kind: LiveInfoKind.Weather, City: "İzmir" }, "Folded suffix form must match İzmir.");

        request = LiveInfoService.Detect("hava kaç derece", [new ErpChatTurn("user", "Şanlıurfa'ya gideceğim")]);
        Assert(request is { Kind: LiveInfoKind.Weather, City: "Şanlıurfa" }, "Missing city must come from earlier user turns.");

        request = LiveInfoService.Detect("bu bölümde hava durumu", []);
        Assert(request is { Kind: LiveInfoKind.Weather, City: null }, "'bölüm' must not be read as Bolu.");

        request = LiveInfoService.Detect("Bolu'da hava nasıl", []);
        Assert(request is { Kind: LiveInfoKind.Weather, City: "Bolu" }, "Apostrophe form must still match exact-only provinces.");

        request = LiveInfoService.Detect("hava durumu", []);
        Assert(request is { Kind: LiveInfoKind.Weather, City: null }, "Without any city the service must ask instead of guessing.");
    }

    private static void FollowUpsReuseThePreviousLiveQuestion()
    {
        ErpChatTurn[] history =
        [
            new("user", "@Jarvis Adana hava durumu nedir"),
            new("assistant", "[Jarvis] Gerçek zamanlı veriye erişimim yok.")
        ];
        LiveInfoRequest request = LiveInfoService.Detect("bunu sen neden yapamıyorsun ?", history);
        Assert(request is { Kind: LiveInfoKind.Weather, City: "Adana" }, "A short follow-up must retry the previous weather question.");

        request = LiveInfoService.Detect("bunu raporla", [new ErpChatTurn("user", "teklifleri listele")]);
        Assert(request.Kind == LiveInfoKind.None, "Follow-ups after non-live questions must stay with the model.");
    }

    private static void CurrencyQuestionsSelectMentionedCodes()
    {
        LiveInfoRequest request = LiveInfoService.Detect("dolar kaç TL", []);
        Assert(request.Kind == LiveInfoKind.Currency && request.Currencies.SequenceEqual(["USD"]), "Dollar question must select USD only.");

        request = LiveInfoService.Detect("euro ve sterlin kuru ne kadar", []);
        Assert(request.Kind == LiveInfoKind.Currency && request.Currencies.SequenceEqual(["EUR", "GBP"]), "Multiple currencies must be kept in order.");

        request = LiveInfoService.Detect("döviz kurları", []);
        Assert(request.Kind == LiveInfoKind.Currency && request.Currencies.Count == 3, "Generic exchange-rate question must use defaults.");
    }

    private static void OrdinaryErpQuestionsStayWithTheModel()
    {
        string[] inputs =
        [
            "Bugün hangi tahsilatlar var?",
            "Sarı tüp stoğu ne kadar",
            "Yeni müşteri kurulumunu anlat",
            "Ordu bayisi ile görüşme notu"
        ];
        foreach (string input in inputs)
        {
            Assert(LiveInfoService.Detect(input, []).Kind == LiveInfoKind.None, $"'{input}' must not trigger a live lookup.");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
