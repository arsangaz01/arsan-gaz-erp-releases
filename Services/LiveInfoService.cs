using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;

namespace ArsanGazERP.Services;

internal enum LiveInfoKind
{
    None,
    Weather,
    Currency,
    News,
    Briefing
}

internal sealed record LiveInfoRequest(
    LiveInfoKind Kind,
    string? City,
    IReadOnlyList<string> Currencies,
    string? NewsTopic = null);

internal static class LiveInfoService
{
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly HttpClient Http = CreateHttpClient();
    private static readonly string[] DefaultCurrencies = ["USD", "EUR", "GBP"];

    private static readonly string[] Provinces =
    [
        "Adana", "Adıyaman", "Afyonkarahisar", "Ağrı", "Amasya", "Ankara", "Antalya", "Artvin", "Aydın", "Balıkesir",
        "Bilecik", "Bingöl", "Bitlis", "Bolu", "Burdur", "Bursa", "Çanakkale", "Çankırı", "Çorum", "Denizli",
        "Diyarbakır", "Edirne", "Elazığ", "Erzincan", "Erzurum", "Eskişehir", "Gaziantep", "Giresun", "Gümüşhane", "Hakkari",
        "Hatay", "Isparta", "Mersin", "İstanbul", "İzmir", "Kars", "Kastamonu", "Kayseri", "Kırklareli", "Kırşehir",
        "Kocaeli", "Konya", "Kütahya", "Malatya", "Manisa", "Kahramanmaraş", "Mardin", "Muğla", "Muş", "Nevşehir",
        "Niğde", "Ordu", "Rize", "Sakarya", "Samsun", "Siirt", "Sinop", "Sivas", "Tekirdağ", "Tokat",
        "Trabzon", "Tunceli", "Şanlıurfa", "Uşak", "Van", "Yozgat", "Zonguldak", "Aksaray", "Bayburt", "Karaman",
        "Kırıkkale", "Batman", "Şırnak", "Bartın", "Ardahan", "Iğdır", "Yalova", "Karabük", "Kilis", "Osmaniye", "Düzce"
    ];

    // Suffixed forms of these collide with everyday words ("bölüm", "karşı", "ordum"), so only exact tokens count.
    private static readonly HashSet<string> ExactOnlyProvinces = new(StringComparer.Ordinal)
    {
        "bolu", "kars", "ordu", "rize", "aydin", "batman", "hatay", "kilis", "sivas", "tokat"
    };

    private static readonly (string Code, string Name, string[] Keywords)[] CurrencyNames =
    [
        ("USD", "ABD doları", ["dolar", "usd"]),
        ("EUR", "Euro", ["euro", "avro", "eur"]),
        ("GBP", "İngiliz sterlini", ["sterlin", "gbp", "pound"]),
        ("CHF", "İsviçre frangı", ["frank", "chf"]),
        ("JPY", "Japon yeni", ["japon yen", "jpy"])
    ];

    private static readonly string[] WeatherKeywords =
    [
        "hava durumu", "hava nasil", "havalar nasil", "hava kac derece", "kac derece", "sicaklik", "yagmur", "yagis",
        "kar yagacak", "ruzgar", "nem orani", "hava tahmini", "havasi nasil", "havasi"
    ];

    private static readonly string[] BriefingKeywords =
    [
        "gunaydin", "gunun ozeti", "gunluk ozet", "sabah ozeti", "brifing", "gundemim", "bugun neler var",
        "bugunku ozet"
    ];

    private static readonly string[] NewsKeywords =
    [
        "haberler", "haberleri", "son dakika", "manset", "gundem", "bugun ne oldu", "haber basliklari", "son haber"
    ];

    private static readonly (string Topic, string Query, string[] Keywords)[] NewsTopics =
    [
        ("Ekonomi", "ekonomi", ["ekonomi"]),
        ("Enerji ve LPG", "LPG OR tüpgaz OR doğalgaz OR akaryakıt", ["lpg", "tupgaz", "tup gaz", "dogalgaz", "akaryakit", "enerji"]),
        ("Spor", "spor", ["spor"]),
        ("Teknoloji", "teknoloji", ["teknoloji"]),
        ("Dünya", "dünya", ["dunya"]),
        ("Sağlık", "sağlık", ["saglik"])
    ];

    private static readonly string[] FollowUpPhrases =
    [
        "bunu", "sen bak", "sen yap", "yapamiyor", "yapabilir misin", "sen soyle", "ogren", "bakar misin", "orasi"
    ];

    public static LiveInfoRequest Detect(string input, IReadOnlyList<ErpChatTurn> history)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(history);

        LiveInfoRequest direct = DetectSingle(input);
        if (direct.Kind == LiveInfoKind.Weather && direct.City is null)
        {
            return direct with { City = FindCityInHistory(history) };
        }

        if (direct.Kind != LiveInfoKind.None)
        {
            return direct;
        }

        string folded = Fold(input);
        if (folded.Length > 120 || !FollowUpPhrases.Any(folded.Contains))
        {
            return direct;
        }

        ErpChatTurn? lastUserTurn = history.LastOrDefault(turn =>
            string.Equals(turn.Role, "user", StringComparison.OrdinalIgnoreCase));
        if (lastUserTurn is null)
        {
            return direct;
        }

        LiveInfoRequest previous = DetectSingle(lastUserTurn.Content);
        if (previous.Kind == LiveInfoKind.Weather)
        {
            return previous with { City = FindCity(input) ?? previous.City ?? FindCityInHistory(history) };
        }

        return previous;
    }

    public static async Task<string?> TryAnswerAsync(
        string input,
        IReadOnlyList<ErpChatTurn> history,
        CancellationToken cancellationToken)
    {
        return await TryAnswerAsync(Detect(input, history), cancellationToken);
    }

    public static async Task<string?> TryAnswerAsync(LiveInfoRequest request, CancellationToken cancellationToken)
    {
        try
        {
            return request.Kind switch
            {
                LiveInfoKind.Weather when request.City is null =>
                    "Hangi il için hava durumuna bakayım? Örneğin: \"Adana hava durumu\".",
                LiveInfoKind.Weather => await GetWeatherAsync(request.City!, cancellationToken),
                LiveInfoKind.Currency => await GetCurrencyRatesAsync(request.Currencies, cancellationToken),
                LiveInfoKind.News => await GetNewsAsync(request.NewsTopic, 8, cancellationToken),
                _ => null
            };
        }
        catch (Exception exception) when (IsLiveDataFailure(exception, cancellationToken))
        {
            return DescribeFailure(request.Kind, exception);
        }
    }

    internal static bool IsLiveDataFailure(Exception exception, CancellationToken cancellationToken) =>
        !cancellationToken.IsCancellationRequested &&
        exception is HttpRequestException or TaskCanceledException or JsonException or XmlException
            or InvalidDataException or KeyNotFoundException or FormatException;

    internal static string DescribeFailure(LiveInfoKind kind, Exception exception)
    {
        string source = kind switch
        {
            LiveInfoKind.Weather => "Open-Meteo",
            LiveInfoKind.News => "Google Haberler",
            _ => "TCMB"
        };
        return $"Canlı veri şu an alınamadı ({source}: {exception.GetBaseException().Message}). " +
               "İnternet bağlantısını kontrol edip birkaç dakika sonra yeniden deneyin.";
    }

    private static LiveInfoRequest DetectSingle(string text)
    {
        string folded = Fold(text);
        if (folded.Length <= 80 && BriefingKeywords.Any(folded.Contains))
        {
            return new LiveInfoRequest(LiveInfoKind.Briefing, FindCity(text), []);
        }

        if (NewsKeywords.Any(keyword => ContainsWord(folded, keyword)))
        {
            string? topic = NewsTopics
                .FirstOrDefault(entry => entry.Keywords.Any(keyword => ContainsWord(folded, keyword)))
                .Topic;
            return new LiveInfoRequest(LiveInfoKind.News, null, [], topic);
        }

        if (WeatherKeywords.Any(folded.Contains))
        {
            return new LiveInfoRequest(LiveInfoKind.Weather, FindCity(text), []);
        }

        string[] currencies = CurrencyNames
            .Where(currency => currency.Keywords.Any(keyword => ContainsWord(folded, keyword)))
            .Select(currency => currency.Code)
            .ToArray();
        bool asksRate = folded.Contains("kur") || folded.Contains("kac") || folded.Contains("ne kadar") ||
                        folded.Contains("fiyat") || folded.Contains("doviz");
        if ((currencies.Length > 0 && asksRate) || folded.Contains("doviz kur"))
        {
            return new LiveInfoRequest(LiveInfoKind.Currency, null, currencies.Length > 0 ? currencies : DefaultCurrencies);
        }

        return new LiveInfoRequest(LiveInfoKind.None, null, []);
    }

    internal static string? FindCity(string text)
    {
        string[] tokens = Fold(text).Split(
            [' ', '\t', '\r', '\n', '\'', '’', ',', '.', '?', '!', ':', ';', '"', '(', ')', '-', '/'],
            StringSplitOptions.RemoveEmptyEntries);
        foreach (string token in tokens)
        {
            foreach (string province in Provinces)
            {
                string foldedProvince = Fold(province);
                if (token == foldedProvince ||
                    (foldedProvince.Length >= 4 && !ExactOnlyProvinces.Contains(foldedProvince) &&
                     token.StartsWith(foldedProvince, StringComparison.Ordinal) &&
                     token.Length - foldedProvince.Length <= 4))
                {
                    return province;
                }
            }
        }

        return null;
    }

    private static string? FindCityInHistory(IReadOnlyList<ErpChatTurn> history)
    {
        for (int index = history.Count - 1; index >= 0; index--)
        {
            if (string.Equals(history[index].Role, "user", StringComparison.OrdinalIgnoreCase) &&
                FindCity(history[index].Content) is { } city)
            {
                return city;
            }
        }

        return null;
    }

    internal static async Task<string> GetWeatherAsync(string city, CancellationToken cancellationToken)
    {
        string geocodeUrl = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=tr&countryCode=TR&name=" +
                            Uri.EscapeDataString(city);
        using JsonDocument geocode = await GetJsonAsync(geocodeUrl, cancellationToken);
        if (!geocode.RootElement.TryGetProperty("results", out JsonElement results) || results.GetArrayLength() == 0)
        {
            return $"\"{city}\" için konum bulunamadı; il adını kontrol edin.";
        }

        JsonElement place = results[0];
        string latitude = place.GetProperty("latitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        string longitude = place.GetProperty("longitude").GetDouble().ToString(CultureInfo.InvariantCulture);
        string forecastUrl =
            $"https://api.open-meteo.com/v1/forecast?latitude={latitude}&longitude={longitude}" +
            "&current=temperature_2m,apparent_temperature,relative_humidity_2m,precipitation,weather_code,wind_speed_10m" +
            "&daily=weather_code,temperature_2m_max,temperature_2m_min,precipitation_probability_max" +
            "&timezone=Europe%2FIstanbul&forecast_days=3";
        using JsonDocument forecast = await GetJsonAsync(forecastUrl, cancellationToken);
        JsonElement current = forecast.RootElement.GetProperty("current");
        JsonElement daily = forecast.RootElement.GetProperty("daily");

        string measuredAt = DateTime.TryParse(
            current.GetProperty("time").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime time)
            ? time.ToString("HH:mm", Turkish)
            : "?";
        StringBuilder answer = new();
        answer.AppendLine($"{city} için anlık hava durumu (ölçüm saati {measuredAt}):");
        answer.AppendLine(
            $"• {Number(current, "temperature_2m")} °C, hissedilen {Number(current, "apparent_temperature")} °C — " +
            DescribeWeather(current.GetProperty("weather_code").GetInt32()));
        answer.AppendLine(
            $"• Nem %{Number(current, "relative_humidity_2m")}, rüzgâr {Number(current, "wind_speed_10m")} km/s, " +
            $"yağış {Number(current, "precipitation")} mm");
        answer.AppendLine();
        answer.AppendLine("Önümüzdeki günler:");
        string[] dayLabels = ["Bugün", "Yarın"];
        int days = daily.GetProperty("time").GetArrayLength();
        for (int day = 0; day < days; day++)
        {
            string label = day < dayLabels.Length
                ? dayLabels[day]
                : DateTime.Parse(daily.GetProperty("time")[day].GetString()!, CultureInfo.InvariantCulture)
                    .ToString("dddd", Turkish);
            JsonElement probability = daily.GetProperty("precipitation_probability_max")[day];
            string rainChance = probability.ValueKind == JsonValueKind.Number ? $"%{probability.GetInt32()}" : "bilinmiyor";
            answer.AppendLine(
                $"• {label}: {Number(daily.GetProperty("temperature_2m_min")[day])}–" +
                $"{Number(daily.GetProperty("temperature_2m_max")[day])} °C, " +
                $"{DescribeWeather(daily.GetProperty("weather_code")[day].GetInt32())}, yağış olasılığı {rainChance}");
        }

        answer.Append("Kaynak: Open-Meteo (open-meteo.com)");
        return answer.ToString();
    }

    internal static Task<string> GetDefaultCurrencyRatesAsync(CancellationToken cancellationToken) =>
        GetCurrencyRatesAsync(DefaultCurrencies, cancellationToken);

    internal static async Task<string> GetNewsAsync(string? topic, int count, CancellationToken cancellationToken)
    {
        string? query = NewsTopics.FirstOrDefault(entry => entry.Topic == topic).Query;
        string url = query is null
            ? "https://news.google.com/rss?hl=tr&gl=TR&ceid=TR:tr"
            : "https://news.google.com/rss/search?hl=tr&gl=TR&ceid=TR:tr&q=" + Uri.EscapeDataString(query + " when:1d");
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        XDocument document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        List<string> headlines = document.Descendants("item")
            .Select(item => ((string?)item.Element("title"))?.Trim())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Select(title => title!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(count)
            .ToList();
        if (headlines.Count == 0)
        {
            throw new InvalidDataException("Haber akışında başlık bulunamadı.");
        }

        StringBuilder answer = new();
        answer.AppendLine(topic is null ? "Türkiye gündemi — son başlıklar:" : $"{topic} gündemi — son 24 saat:");
        foreach (string headline in headlines)
        {
            answer.AppendLine("• " + headline);
        }

        answer.Append("Kaynak: Google Haberler (news.google.com); ayrıntı için kaynağın kendi sitesine bakın.");
        return answer.ToString();
    }

    private static async Task<string> GetCurrencyRatesAsync(
        IReadOnlyList<string> currencies,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync("https://www.tcmb.gov.tr/kurlar/today.xml", cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        XDocument document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        XElement root = document.Root ?? throw new InvalidDataException("TCMB yanıtı boş.");
        string date = (string?)root.Attribute("Tarih") ?? "?";

        StringBuilder answer = new();
        answer.AppendLine($"TCMB gösterge kurları ({date} tarihli bülten):");
        foreach (string code in currencies)
        {
            XElement? currency = root.Elements("Currency")
                .FirstOrDefault(element => (string?)element.Attribute("CurrencyCode") == code);
            if (currency is null)
            {
                continue;
            }

            string name = CurrencyNames.First(entry => entry.Code == code).Name;
            int unit = int.TryParse((string?)currency.Element("Unit"), out int parsedUnit) ? parsedUnit : 1;
            string unitLabel = unit == 1 ? "" : $" ({unit} birim)";
            answer.AppendLine(
                $"• {name}{unitLabel}: alış {Rate(currency, "ForexBuying")} TL, satış {Rate(currency, "ForexSelling")} TL");
        }

        answer.Append("Kaynak: Türkiye Cumhuriyet Merkez Bankası (tcmb.gov.tr). Hafta sonu ve tatillerde son iş gününün bülteni gösterilir.");
        return answer.ToString();
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static string Rate(XElement currency, string elementName) =>
        decimal.TryParse((string?)currency.Element(elementName), NumberStyles.Number, CultureInfo.InvariantCulture, out decimal value)
            ? value.ToString("N4", Turkish)
            : "-";

    private static string Number(JsonElement parent, string property) => Number(parent.GetProperty(property));

    private static string Number(JsonElement value) =>
        value.ValueKind == JsonValueKind.Number ? Math.Round(value.GetDouble(), 1).ToString("0.#", Turkish) : "-";

    private static string DescribeWeather(int code) => code switch
    {
        0 => "açık",
        1 => "çoğunlukla açık",
        2 => "parçalı bulutlu",
        3 => "kapalı",
        45 or 48 => "sisli",
        51 or 53 or 55 => "çiseleme",
        56 or 57 => "dondurucu çiseleme",
        61 => "hafif yağmurlu",
        63 => "yağmurlu",
        65 => "kuvvetli yağmurlu",
        66 or 67 => "dondurucu yağmur",
        71 => "hafif kar yağışlı",
        73 => "kar yağışlı",
        75 => "yoğun kar yağışlı",
        77 => "kar taneli",
        80 or 81 => "sağanak yağışlı",
        82 => "şiddetli sağanak",
        85 or 86 => "kar sağanaklı",
        95 => "gök gürültülü fırtına",
        96 or 99 => "dolu ile gök gürültülü fırtına",
        _ => "değişken"
    };

    private static bool ContainsWord(string foldedText, string keyword)
    {
        int index = foldedText.IndexOf(keyword, StringComparison.Ordinal);
        return index >= 0 && (index == 0 || !char.IsLetter(foldedText[index - 1]));
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

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new() { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP-LiveInfo/1.0");
        return client;
    }
}
