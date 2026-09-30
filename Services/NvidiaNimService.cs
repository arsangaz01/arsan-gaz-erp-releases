using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ArsanGazERP.Services;

public sealed class NvidiaNimService
{
    private const string BaseUrl = "https://integrate.api.nvidia.com/v1/";
    private const string DefaultModel = "meta/llama-3.1-8b-instruct";
    private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArsanGazERP", "Nvidia");
    private static readonly string KeyFile = Path.Combine(Folder, "nim-key.bin");
    private static readonly string SettingsFile = Path.Combine(Folder, "nim-settings.json");
    private readonly HttpClient _http = new() { BaseAddress = new Uri(BaseUrl), Timeout = TimeSpan.FromMinutes(3) };

    public bool IsConfigured => File.Exists(KeyFile) && !string.IsNullOrWhiteSpace(GetApiKey());
    public string Model { get; private set; } = LoadModel();

    public void Save(string apiKey, string model)
    {
        string key = apiKey?.Trim() ?? string.Empty;
        if (!key.StartsWith("nvapi-", StringComparison.Ordinal)) throw new ArgumentException("NVIDIA API anahtari nvapi- ile baslamalidir.");
        Directory.CreateDirectory(Folder);
        byte[] encrypted = ProtectedData.Protect(Encoding.UTF8.GetBytes(key), null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(KeyFile, encrypted);
        Model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();
        File.WriteAllText(SettingsFile, JsonSerializer.Serialize(new { model = Model }, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));
    }

    public void Remove() { if (File.Exists(KeyFile)) File.Delete(KeyFile); if (File.Exists(SettingsFile)) File.Delete(SettingsFile); }
    public Task<string> TestAsync(CancellationToken ct = default) => CompleteAsync("Yalnizca TAMAM yaz.", ct);

    public async Task<string> CompleteAsync(string prompt, CancellationToken ct = default)
    {
        string key = GetApiKey() ?? throw new InvalidOperationException("NVIDIA NIM API anahtari ayarlanmamis.");
        using HttpRequestMessage request = new(HttpMethod.Post, "chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        request.Content = new StringContent(JsonSerializer.Serialize(new { model = Model, messages = new[] { new { role = "user", content = prompt } }, temperature = 0.2, max_tokens = 1024, stream = false }), Encoding.UTF8, "application/json");
        using HttpResponseMessage response = await _http.SendAsync(request, ct);
        string raw = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"NVIDIA NIM hatasi: {(int)response.StatusCode} {response.ReasonPhrase}{Environment.NewLine}{raw}");
        using JsonDocument doc = JsonDocument.Parse(raw);
        return doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
    }

    private static string? GetApiKey()
    {
        try { if (!File.Exists(KeyFile)) return null; return Encoding.UTF8.GetString(ProtectedData.Unprotect(File.ReadAllBytes(KeyFile), null, DataProtectionScope.CurrentUser)); }
        catch { return null; }
    }

    private static string LoadModel()
    {
        try { if (!File.Exists(SettingsFile)) return DefaultModel; using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(SettingsFile)); return doc.RootElement.TryGetProperty("model", out JsonElement m) ? m.GetString() ?? DefaultModel : DefaultModel; }
        catch { return DefaultModel; }
    }
}
