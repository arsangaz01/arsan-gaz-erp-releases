using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace ArsanGazERP.Services;

public sealed class AutoUpdateLaunchService
{
    private const string Api = "https://api.github.com/repos/arsangaz01/arsan-gaz-erp-releases/releases/latest";
    private static readonly HttpClient Http = CreateClient();

    public async Task<AutoUpdateResult> CheckAndStartLatestInstallerAsync()
    {
        try
        {
            using HttpResponseMessage response = await Http.GetAsync(Api);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new AutoUpdateResult(false, false, "Yayinlanmis guncelleme bulunamadi.");
            if (!response.IsSuccessStatusCode)
                return new AutoUpdateResult(false, false, "Guncelleme denetimi basarisiz: " + (int)response.StatusCode);

            using JsonDocument document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            JsonElement root = document.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "0.0.0";
            Version current = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            Version latest;
            if (!TryVersion(tag, out latest) || latest <= current)
                return new AutoUpdateResult(false, false, "Uygulama guncel: " + current);

            JsonElement assets;
            if (!root.TryGetProperty("assets", out assets))
                return new AutoUpdateResult(true, false, "Yeni surum bulundu fakat installer yok.");

            string selectedName = string.Empty;
            string selectedUrl = string.Empty;
            int selectedPriority = -1;
            foreach (JsonElement asset in assets.EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                string url = asset.GetProperty("browser_download_url").GetString() ?? string.Empty;
                int priority = Priority(name);
                if (priority > selectedPriority && !string.IsNullOrWhiteSpace(url))
                {
                    selectedName = name;
                    selectedUrl = url;
                    selectedPriority = priority;
                }
            }
            if (selectedPriority < 0)
                return new AutoUpdateResult(true, false, "Yeni surum bulundu fakat uygun installer yok.");

            string folder = Path.Combine(Path.GetTempPath(), "ArsanGazERP", "Updates", latest.ToString());
            Directory.CreateDirectory(folder);
            string target = Path.Combine(folder, selectedName);
            string temporary = target + ".download";
            if (File.Exists(temporary)) File.Delete(temporary);
            await File.WriteAllBytesAsync(temporary, await Http.GetByteArrayAsync(selectedUrl));
            File.Move(temporary, target, true);

            ProcessStartInfo start = new ProcessStartInfo();
            start.WorkingDirectory = folder;
            start.UseShellExecute = true;
            string extension = Path.GetExtension(target);
            if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase))
            {
                start.FileName = Environment.GetEnvironmentVariable("COMSPEC") ?? "cmd.exe";
                start.Arguments = "/d /c \"\"" + target + "\"\"";
            }
            else start.FileName = target;

            Process process = Process.Start(start) ?? throw new InvalidOperationException("Installer baslatilamadi.");
            return new AutoUpdateResult(true, true, "Guncelleme indirildi ve baslatildi.");
        }
        catch (Exception exception)
        {
            return new AutoUpdateResult(false, false, "Otomatik guncelleme denetlenemedi: " + exception.GetBaseException().Message);
        }
    }

    private static int Priority(string name)
    {
        bool installer = name.Contains("setup", StringComparison.OrdinalIgnoreCase) || name.Contains("installer", StringComparison.OrdinalIgnoreCase) || name.Contains("update", StringComparison.OrdinalIgnoreCase) || name.Contains("onarim", StringComparison.OrdinalIgnoreCase) || name.Contains("kurulum", StringComparison.OrdinalIgnoreCase);
        if (!installer) return -1;
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return 30;
        if (name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase)) return 20;
        if (name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase)) return 10;
        return -1;
    }

    private static bool TryVersion(string value, out Version version)
    {
        string normalized = value.Trim().TrimStart('v', 'V');
        int dash = normalized.IndexOf('-');
        if (dash >= 0) normalized = normalized.Substring(0, dash);
        int plus = normalized.IndexOf('+');
        if (plus >= 0) normalized = normalized.Substring(0, plus);
        Version parsed;
        bool ok = Version.TryParse(normalized, out parsed!);
        version = parsed ?? new Version(0, 0);
        return ok;
    }

    private static HttpClient CreateClient()
    {
        HttpClient client = new HttpClient();
        client.Timeout = TimeSpan.FromMinutes(5);
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ArsanGazERP-AutoUpdater/7.0.5");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }
}

public sealed record AutoUpdateResult(bool UpdateAvailable, bool InstallerStarted, string Message);
