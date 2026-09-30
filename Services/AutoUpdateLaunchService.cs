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
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            using var response = await Http.GetAsync(LatestReleaseApi, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return new(false, false, $"Güncelleme denetimi başarısız: HTTP {(int)response.StatusCode}");

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, JsonOptions, timeout.Token);
            if (release is null || release.Draft || release.Prerelease)
                return new(false, false, "Yayımlanmış kararlı sürüm bulunamadı.");

            if (!TryVersion(release.TagName, out var latest))
                return new(false, false, "GitHub sürüm etiketi geçersiz.");

            var current = Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0, 0);
            if (latest <= current)
                return new(false, false, $"Güncel sürüm kullanılıyor: {current}");

            var asset = release.Assets.FirstOrDefault(a =>
                a.Name.Equals("ArsanGazERP_Setup.exe", StringComparison.OrdinalIgnoreCase))
                ?? release.Assets.FirstOrDefault(a => a.Name.EndsWith("Setup.exe", StringComparison.OrdinalIgnoreCase));
            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return new(true, false, "Yeni sürüm var ancak kurulum dosyası bulunamadı.");

            var answer = MessageBox.Show(
                $"Arsan Gaz ERP {latest} sürümü hazır. Güvenli kurulum dosyası indirilsin ve uygulama kapatılarak güncelleme başlatılsın mı?",
                "Arsan Gaz ERP Güncelleme",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            if (answer != MessageBoxResult.Yes)
                return new(true, false, "Güncelleme kullanıcı tarafından ertelendi.");

            var updateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Updates", latest.ToString());
            Directory.CreateDirectory(updateDir);
            var installer = Path.Combine(updateDir, "ArsanGazERP_Setup.exe");
            var partial = installer + ".download";
            if (File.Exists(partial)) File.Delete(partial);

            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            using var download = await Http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token);
            download.EnsureSuccessStatusCode();
            await using (var input = await download.Content.ReadAsStreamAsync(downloadTimeout.Token))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, true))
                await input.CopyToAsync(output, downloadTimeout.Token);

            var expected = NormalizeDigest(asset.Digest);
            if (string.IsNullOrWhiteSpace(expected))
                expected = await TryReadSidecarHashAsync(release, asset.Name, downloadTimeout.Token);
            var actual = await Sha256Async(partial, downloadTimeout.Token);
            if (string.IsNullOrWhiteSpace(expected))
            {
                File.Delete(partial);
                return new(true, false, "Güncelleme SHA-256 doğrulama bilgisi olmadığı için güvenlik amacıyla kurulmadı.");
            }
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(partial);
                return new(true, false, "Güncelleme SHA-256 doğrulaması başarısız.");
            }

            File.Move(partial, installer, true);
            var psi = new ProcessStartInfo
            {
                FileName = installer,
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
                UseShellExecute = true,
                Verb = "runas"
            };
            Process.Start(psi);
            return new(true, true, $"Arsan Gaz ERP {latest} kurulumu başlatıldı.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(false, false, "Güncelleme denetimi zaman aşımına uğradı; uygulama normal açılacak.");
        }
        catch (Exception ex)
        {
            return new(false, false, "Güncelleme denetimi atlandı: " + ex.Message);
        }
    }

    private static async Task<string?> TryReadSidecarHashAsync(GitHubRelease release, string installerName, CancellationToken ct)
    {
        var sidecar = release.Assets.FirstOrDefault(a =>
            a.Name.Equals(installerName + ".sha256", StringComparison.OrdinalIgnoreCase) ||
            a.Name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase));
        if (sidecar is null) return null;
        using var response = await Http.GetAsync(sidecar.BrowserDownloadUrl, ct);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(ct);
        foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && parts[0].Length == 64 && parts[0].All(Uri.IsHexDigit))
                if (parts.Length == 1 || line.Contains(installerName, StringComparison.OrdinalIgnoreCase)) return parts[0];
        }
        return null;
    }

    private static async Task<string> Sha256Async(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, ct);
        return Convert.ToHexString(hash);
    }

    private static string? NormalizeDigest(string? digest)
    {
        if (string.IsNullOrWhiteSpace(digest)) return null;
        const string prefix = "sha256:";
        return digest.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? digest[prefix.Length..] : digest;
    }

    private static bool TryVersion(string? tag, out Version version)
    {
        var value = (tag ?? string.Empty).Trim();
        if (value.StartsWith('v') || value.StartsWith('V')) value = value[1..];
        var dash = value.IndexOf('-');
        if (dash >= 0) value = value[..dash];
        return Version.TryParse(value, out version!);
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP-Updater/8.0.5");
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
