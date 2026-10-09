using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ArsanGazERP.Services;

internal static partial class WebResearchService
{
    private const int MaximumPageCharacters = 3500;
    private const int MaximumDownloadBytes = 1_500_000;
    private static readonly HttpClient Http = CreateClient(allowRedirects: true);
    private static readonly HttpClient PageHttp = CreateClient(allowRedirects: false);

    public static async Task<string> SearchAsync(string query, CancellationToken cancellationToken)
    {
        string trimmed = query.Trim();
        if (trimmed.Length is < 2 or > 200)
        {
            return "Arama sorgusu 2-200 karakter olmalı.";
        }

        string folded = FoldTurkish(trimmed);
        Task<List<(string, string, string)>>[] searches = folded == trimmed
            ? [TryBingAsync(trimmed, cancellationToken)]
            : [TryBingAsync(trimmed, cancellationToken), TryBingAsync(folded, cancellationToken)];
        List<(string Title, string Url, string Snippet)>[] batches = await Task.WhenAll(searches);
        List<(string Title, string Url, string Snippet)> results = [];
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        int longest = batches.Max(batch => batch.Count);
        for (int position = 0; position < longest; position++)
        {
            foreach (List<(string Title, string Url, string Snippet)> batch in batches)
            {
                if (position < batch.Count && seen.Add(batch[position].Url.TrimEnd('/')))
                {
                    results.Add(batch[position]);
                }
            }
        }

        if (results.Count == 0)
        {
            results = await SearchDuckDuckGoAsync(trimmed, cancellationToken);
        }

        if (results.Count == 0)
        {
            return $"\"{trimmed}\" için sonuç bulunamadı. Farklı kelimelerle tekrar dene.";
        }

        StringBuilder text = new();
        int index = 1;
        foreach ((string title, string url, string snippet) in results.Take(8))
        {
            text.AppendLine($"{index++}. {title}");
            text.AppendLine($"   {url}");
            if (snippet.Length > 0) text.AppendLine($"   {Truncate(snippet, 280)}");
        }

        return text.ToString().TrimEnd();
    }

    public static async Task<string> FetchPageAsync(string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            uri.UserInfo.Length > 0)
        {
            return "Geçersiz adres; yalnızca http(s) sayfaları okunabilir.";
        }

        HttpResponseMessage? response = null;
        for (int redirect = 0; ; redirect++)
        {
            if (!await IsPublicHostAsync(uri, cancellationToken))
            {
                return "Bu adres yerel/özel ağa işaret ediyor; güvenlik nedeniyle okunmadı.";
            }

            response = await PageHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode is < 300 or >= 400 || response.Headers.Location is null)
            {
                break;
            }

            Uri next = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(uri, response.Headers.Location);
            response.Dispose();
            if (redirect >= 4 || (next.Scheme != Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttp))
            {
                return "Sayfa çok fazla/uygunsuz yönlendirme yaptı; okunmadı.";
            }

            uri = next;
        }

        using HttpResponseMessage _ = response;
        if (!response.IsSuccessStatusCode)
        {
            return $"Sayfa açılamadı (HTTP {(int)response.StatusCode}).";
        }

        string? mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null && !mediaType.Contains("html", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("xml", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Contains("json", StringComparison.OrdinalIgnoreCase))
        {
            return $"Sayfa metin değil ({mediaType}); okunmadı.";
        }

        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using MemoryStream buffer = new();
        byte[] chunk = new byte[16_384];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0 && buffer.Length < MaximumDownloadBytes)
        {
            buffer.Write(chunk, 0, read);
        }

        string html = Encoding.UTF8.GetString(buffer.ToArray());
        string title = WebUtility.HtmlDecode(TitleRegex().Match(html).Groups[1].Value).Trim();
        string text = HtmlToText(html);
        return $"Sayfa: {uri.Host}{(title.Length > 0 ? " — " + title : string.Empty)}\n{Truncate(text, MaximumPageCharacters)}";
    }

    internal static string HtmlToText(string html)
    {
        string withoutBlocks = BlockRegex().Replace(html, " ");
        string withBreaks = BreakRegex().Replace(withoutBlocks, "\n");
        string withoutTags = TagRegex().Replace(withBreaks, " ");
        string decoded = WebUtility.HtmlDecode(withoutTags);
        string collapsedSpaces = SpaceRegex().Replace(decoded, " ");
        return NewlineRegex().Replace(collapsedSpaces, "\n").Trim();
    }

    internal static string FoldTurkish(string text)
    {
        StringBuilder builder = new(text.Length);
        foreach (char character in text)
        {
            builder.Append(character switch
            {
                'ç' => 'c', 'Ç' => 'C', 'ğ' => 'g', 'Ğ' => 'G', 'ı' => 'i', 'İ' => 'I',
                'ö' => 'o', 'Ö' => 'O', 'ş' => 's', 'Ş' => 'S', 'ü' => 'u', 'Ü' => 'U',
                _ => character
            });
        }

        return builder.ToString();
    }

    private static async Task<List<(string, string, string)>> TryBingAsync(string query, CancellationToken cancellationToken)
    {
        try
        {
            return await SearchBingAsync(query, cancellationToken);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested &&
                                          exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private static async Task<List<(string, string, string)>> SearchBingAsync(string query, CancellationToken cancellationToken)
    {
        string url = "https://www.bing.com/search?format=rss&q=" + Uri.EscapeDataString(query);
        using HttpResponseMessage response = await Http.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using Stream stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        XDocument document = await XDocument.LoadAsync(stream, LoadOptions.None, cancellationToken);
        return document.Descendants("item")
            .Select(item => (
                Title: ((string?)item.Element("title") ?? string.Empty).Trim(),
                Url: ((string?)item.Element("link") ?? string.Empty).Trim(),
                Snippet: WebUtility.HtmlDecode((string?)item.Element("description") ?? string.Empty).Trim()))
            .Where(item => item.Title.Length > 0 && item.Url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static async Task<List<(string, string, string)>> SearchDuckDuckGoAsync(string query, CancellationToken cancellationToken)
    {
        using HttpRequestMessage request = new(HttpMethod.Post, "https://html.duckduckgo.com/html/")
        {
            Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("q", query), new("kl", "tr-tr")])
        };
        using HttpResponseMessage response = await Http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return [];
        string html = await response.Content.ReadAsStringAsync(cancellationToken);
        List<(string, string, string)> results = [];
        foreach (Match match in DuckResultRegex().Matches(html))
        {
            string href = WebUtility.HtmlDecode(match.Groups["href"].Value);
            Match target = UddgRegex().Match(href);
            string resolved = target.Success ? Uri.UnescapeDataString(target.Groups[1].Value) : href;
            string title = WebUtility.HtmlDecode(TagRegex().Replace(match.Groups["title"].Value, string.Empty)).Trim();
            if (resolved.StartsWith("http", StringComparison.OrdinalIgnoreCase) && title.Length > 0)
            {
                results.Add((title, resolved, string.Empty));
            }
        }

        return results;
    }

    private static async Task<bool> IsPublicHostAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri.IsLoopback || uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return false;
        IPAddress[] addresses;
        try
        {
            addresses = IPAddress.TryParse(uri.Host, out IPAddress? literal)
                ? [literal]
                : await Dns.GetHostAddressesAsync(uri.Host, cancellationToken);
        }
        catch (SocketException)
        {
            return false;
        }

        return addresses.Length > 0 && addresses.All(IsPublicAddress);
    }

    internal static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || address.IsIPv6SiteLocal ||
            address.IsIPv6Multicast || address.Equals(IPAddress.IPv6Any) || address.Equals(IPAddress.Any))
        {
            return false;
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            byte first = address.GetAddressBytes()[0];
            return (first & 0xFE) != 0xFC;
        }

        byte[] b = address.GetAddressBytes();
        return !(b[0] == 10 || b[0] == 127 || b[0] == 0 || (b[0] == 169 && b[1] == 254) ||
                 (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
                 (b[0] == 100 && b[1] >= 64 && b[1] <= 127) || b[0] >= 224);
    }

    private static string Truncate(string text, int maximum) =>
        text.Length <= maximum ? text : text[..maximum] + " …";

    private static HttpClient CreateClient(bool allowRedirects)
    {
        HttpClient client = new(new SocketsHttpHandler { AllowAutoRedirect = allowRedirects, AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/140.0 Safari/537.36 ArsanGazERP-Jarvis/1.0");
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9,en;q=0.7");
        return client;
    }

    [GeneratedRegex(@"<title[^>]*>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"<(script|style|noscript|svg|head|nav|footer|form)[^>]*>.*?</\1>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex BlockRegex();

    [GeneratedRegex(@"<(br|/p|/div|/li|/h[1-6]|/tr)[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex BreakRegex();

    [GeneratedRegex(@"<[^>]+>")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"[ \t\r\f\v]+")]
    private static partial Regex SpaceRegex();

    [GeneratedRegex(@"\s*\n\s*(\n\s*)*")]
    private static partial Regex NewlineRegex();

    [GeneratedRegex("<a[^>]+class=\"result__a\"[^>]+href=\"(?<href>[^\"]+)\"[^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DuckResultRegex();

    [GeneratedRegex(@"[?&]uddg=([^&]+)")]
    private static partial Regex UddgRegex();
}
