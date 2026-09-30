using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace ArsanGazERP.Services;

public sealed record AutoUpdateResult(bool UpdateAvailable, bool InstallerStarted, string Message);

public sealed class AutoUpdateLaunchService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/arsangaz01/arsan-gaz-erp-releases/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    public async Task<AutoUpdateResult> CheckAndStartLatestInstallerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            using var checkTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            checkTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, checkTimeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, false, $"Güncelleme denetimi başarısız: HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(checkTimeout.Token);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, checkTimeout.Token);
            if (release is null || release.Draft || release.Prerelease)
                return new(false, false, "Yayımlanmış kararlı sürüm bulunamadı.");
            if (!TryVersion(release.TagName, out var latest))
                return new(false, false, "GitHub sürüm etiketi geçersiz.");

            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
            if (latest <= current)
                return new(false, false, $"Güncel sürüm kullanılıyor: {current}");

            var setup = release.Assets.FirstOrDefault(a => a.Name.Equals("ArsanGazERP_Setup.exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (setup is null || string.IsNullOrWhiteSpace(setup.BrowserDownloadUrl))
                return new(true, false, "Yeni sürüm var ancak Setup.exe bulunamadı.");

            if (MessageBox.Show($"Arsan Gaz ERP {latest} sürümü hazır. İndirip kurmak ister misiniz?", "Arsan Gaz ERP Güncelleme", MessageBoxButton.YesNo, MessageBoxImage.Information) != MessageBoxResult.Yes)
                return new(true, false, "Güncelleme ertelendi.");

            var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Updates", latest.ToString());
            Directory.CreateDirectory(directory);
            var installer = Path.Combine(directory, "ArsanGazERP_Setup.exe");
            var partial = installer + ".download";
            if (File.Exists(partial)) File.Delete(partial);

            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var download = await Http.GetAsync(setup.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token);
            download.EnsureSuccessStatusCode();
            await using (var input = await download.Content.ReadAsStreamAsync(downloadTimeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, downloadTimeout.Token);

            var expected = NormalizeDigest(setup.Digest) ?? await GetSidecarHashAsync(release, setup.Name, downloadTimeout.Token);
            var actual = await ComputeSha256Async(partial, downloadTimeout.Token);
            if (string.IsNullOrWhiteSpace(expected))
            {
                File.Delete(partial);
                return new(true, false, "SHA-256 doğrulama verisi bulunmadığı için güncelleme kurulmadı.");
            }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new(true, false, "Güncelleme SHA-256 doğrulaması başarısız.");
            }

            File.Move(partial, installer, true);
            Process.Start(new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
                Verb = "runas"
            });
            return new(true, true, $"Arsan Gaz ERP {latest} kurulumu başlatıldı.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, "Güncelleme denetimi zaman aşımına uğradı; uygulama normal açılacak.");
        }
        catch (Exception exception)
        {
            return new(false, false, "Güncelleme denetimi atlandı: " + exception.GetBaseException().Message);
        }
    }

    private static async Task<string?> GetSidecarHashAsync(GitHubRelease release, string installerName, CancellationToken cancellationToken)
    {
        var asset = release.Assets.FirstOrDefault(a => a.Name.Equals(installerName + ".sha256", StringComparison.OrdinalIgnoreCase))
            ?? release.Assets.FirstOrDefault(a => a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (asset is null) return null;
        using var response = await Http.GetAsync(asset.BrowserDownloadUrl, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit) && (parts.Length == 1 || line.Contains(installerName, StringComparison.OrdinalIgnoreCase)))
                return parts[0];
        }
        return null;
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var sha = SHA256.Create();
        return Convert.ToHexString(await sha.ComputeHashAsync(stream, cancellationToken));
    }

    private static string? NormalizeDigest(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        return value.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? value[7..] : value;
    }

    private static bool TryVersion(string? tag, out Version version)
    {
        var value = (tag ?? string.Empty).Trim().TrimStart('v', 'V');
        var separator = value.IndexOf('-');
        if (separator >= 0) value = value[..separator];
        return Version.TryParse(value, out version!);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP-Updater/8.0.7");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        client.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return client;
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = string.Empty;
        [JsonPropertyName("draft")] public bool Draft { get; set; }
        [JsonPropertyName("prerelease")] public bool Prerelease { get; set; }
        [JsonPropertyName("assets")] public GitHubAsset[] Assets { get; set; } = Array.Empty<GitHubAsset>();
    }
    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = string.Empty;
        [JsonPropertyName("browser_download_url")] public string BrowserDownloadUrl { get; set; } = string.Empty;
        [JsonPropertyName("digest")] public string? Digest { get; set; }
    }
}
