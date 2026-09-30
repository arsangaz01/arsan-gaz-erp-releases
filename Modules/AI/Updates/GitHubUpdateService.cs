using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Modules.AI.Updates;

public sealed class GitHubUpdateService
{
    private readonly HttpClient _httpClient;

    public GitHubUpdateService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string DownloadUrl);

    public sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string Body,
        [property: JsonPropertyName("assets")] List<Asset> Assets);

    public async Task<Release?> GetLatestAsync(
        string owner,
        string repository,
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://api.github.com/repos/{owner}/{repository}/releases/latest");

        request.Headers.UserAgent.Add(
            new ProductInfoHeaderValue("ArsanGazERP", "6.5.10"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<Release>(
            cancellationToken: cancellationToken);
    }

    public static bool IsNewer(string tag)
    {
        var current = Assembly.GetEntryAssembly()?.GetName().Version
                      ?? new Version(0, 0);
        var normalized = tag.Trim().TrimStart('v', 'V').Split('-', '+')[0];
        return Version.TryParse(normalized, out var latest) && latest > current;
    }

    public static Asset? FindWindowsInstaller(Release release) =>
        release.Assets.FirstOrDefault(asset =>
            asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
            (asset.Name.Contains("setup", StringComparison.OrdinalIgnoreCase) ||
             asset.Name.Contains("installer", StringComparison.OrdinalIgnoreCase)));
}
