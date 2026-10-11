using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.Services;

public sealed record AgentPermissionSettings(
    bool CanSearchPublicWeb = false,
    bool CanCreateCrmNote = false,
    bool CanScheduleCrmFollowUp = false,
    bool CanCreateErpTask = false,
    bool CanDelegateTasks = false,
    bool CanManageCollectionReminders = false,
    bool CanReadErpSummary = false,
    bool CanControlComputer = false);

public sealed record CloudAgentConnectionSettings(string EndpointUrl, bool HasAccessToken);

public static class CloudAgentSettingsStore
{
    private static readonly object Sync = new();
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArsanGazERP.CloudAgent.Secret.v1");
    private static readonly string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "CloudAgent");
    private static readonly string SettingsPath = Path.Combine(Folder, "settings.json");
    private static readonly string TokenPath = Path.Combine(Folder, "access-token.bin");

    public static CloudAgentConnectionSettings LoadConnection()
    {
        lock (Sync)
        {
            PersistedSettings settings = LoadSettings();
            return new CloudAgentConnectionSettings(settings.EndpointUrl, File.Exists(TokenPath));
        }
    }

    public static IReadOnlyDictionary<string, AgentPermissionSettings> LoadPermissions()
    {
        lock (Sync)
        {
            PersistedSettings settings = LoadSettings();
            Dictionary<string, AgentPermissionSettings> permissions = CreateDefaultPermissions();
            if (settings.Permissions is not null)
            {
                foreach ((string id, AgentPermissionSettings value) in settings.Permissions)
                {
                    if (permissions.ContainsKey(id))
                    {
                        permissions[id] = value;
                    }
                }
            }

            return permissions;
        }
    }

    public static AgentPermissionSettings GetPermissions(string agentId)
    {
        IReadOnlyDictionary<string, AgentPermissionSettings> permissions = LoadPermissions();
        return permissions.TryGetValue(agentId, out AgentPermissionSettings? value)
            ? value
            : throw new ArgumentException($"Bilinmeyen ajan kimliği: {agentId}", nameof(agentId));
    }

    public static void SaveConnection(string endpointUrl, string? accessToken)
    {
        string normalizedEndpoint = NormalizeEndpoint(endpointUrl);
        lock (Sync)
        {
            PersistedSettings settings = LoadSettings() with { EndpointUrl = normalizedEndpoint };
            SaveSettings(settings);

            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                byte[] protectedToken = LocalDataProtector.Protect(
                    Encoding.UTF8.GetBytes(accessToken.Trim()),
                    Entropy);
                WriteAtomically(TokenPath, protectedToken);
            }
        }
    }

    public static void RemoveAccessToken()
    {
        lock (Sync)
        {
            if (File.Exists(TokenPath))
            {
                File.Delete(TokenPath);
            }
        }
    }

    public static void SavePermissions(
        IReadOnlyDictionary<string, AgentPermissionSettings> permissions)
    {
        ArgumentNullException.ThrowIfNull(permissions);
        lock (Sync)
        {
            Dictionary<string, AgentPermissionSettings> allowed = CreateDefaultPermissions();
            foreach ((string id, AgentPermissionSettings value) in permissions)
            {
                if (!allowed.ContainsKey(id))
                {
                    throw new ArgumentException($"Bilinmeyen ajan kimliği: {id}", nameof(permissions));
                }

                allowed[id] = value ?? throw new ArgumentException(
                    $"Ajan izinleri boş olamaz: {id}",
                    nameof(permissions));
            }

            SaveSettings(LoadSettings() with { Permissions = allowed });
        }
    }

    internal static string GetEndpointUrl()
    {
        string endpoint = LoadConnection().EndpointUrl;
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            throw new InvalidOperationException("Cloudflare Worker adresi Ajan Yetki ve Bulut Ayarları bölümünde yapılandırılmamış.");
        }

        return endpoint;
    }

    internal static string GetAccessToken()
    {
        lock (Sync)
        {
            if (!File.Exists(TokenPath))
            {
                throw new InvalidOperationException("Cloudflare Worker erişim anahtarı kaydedilmemiş.");
            }

            try
            {
                byte[] protectedToken = File.ReadAllBytes(TokenPath);
                return Encoding.UTF8.GetString(
                    LocalDataProtector.Unprotect(protectedToken, Entropy));
            }
            catch (Exception exception) when (
                exception is IOException or CryptographicException or UnauthorizedAccessException)
            {
                throw new InvalidOperationException(
                    "Cloudflare Worker erişim anahtarı bu Windows hesabında açılamadı. Anahtarı yeniden kaydedin.",
                    exception);
            }
        }
    }

    private static PersistedSettings LoadSettings()
    {
        if (!File.Exists(SettingsPath))
        {
            return new PersistedSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<PersistedSettings>(File.ReadAllText(SettingsPath))
                ?? throw new InvalidDataException("Bulut ajan ayar dosyası boş.");
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Bulut ajan ayarları okunamadı. Dosyayı inceleyin: " + SettingsPath,
                exception);
        }
    }

    private static void SaveSettings(PersistedSettings settings)
    {
        Directory.CreateDirectory(Folder);
        byte[] contents = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(
            settings,
            new JsonSerializerOptions { WriteIndented = true }));
        WriteAtomically(SettingsPath, contents);
    }

    private static void WriteAtomically(string path, byte[] contents)
    {
        Directory.CreateDirectory(Folder);
        string temporaryPath = path + ".tmp";
        LocalDataProtector.WriteAllBytes(temporaryPath, contents);
        File.Move(temporaryPath, path, overwrite: true);
    }

    private static string NormalizeEndpoint(string endpointUrl)
    {
        string value = endpointUrl?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return string.Empty;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
             !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)) ||
            endpoint.UserInfo.Length > 0 ||
            endpoint.Query.Length > 0 ||
            endpoint.Fragment.Length > 0 ||
            endpoint.AbsolutePath is not ("" or "/"))
        {
            throw new ArgumentException(
                "Worker adresi HTTPS tabanlı bir kök adres olmalıdır. Yalnızca yerel geliştirme için HTTP/localhost kabul edilir.",
                nameof(endpointUrl));
        }

        return endpoint.GetLeftPart(UriPartial.Authority).TrimEnd('/');
    }

    private static Dictionary<string, AgentPermissionSettings> CreateDefaultPermissions() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["crm"] = new(),
            ["sales"] = new(),
            ["finance"] = new(),
            ["planner"] = new(),
            ["researcher"] = new(CanSearchPublicWeb: true),
            ["supervisor"] = new(),
            ["jarvis"] = new()
        };

    private sealed record PersistedSettings(
        string EndpointUrl = "",
        Dictionary<string, AgentPermissionSettings>? Permissions = null);
}
