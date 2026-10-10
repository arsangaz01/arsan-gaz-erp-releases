using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed class ProtectedSettingsStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ArsanGazERP.AgentWatcher.Secrets.v1");
    private static readonly string SecretPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ArsanGazERP",
        "AgentWatcher",
        "secrets.json");

    private SecretValues _values = LoadValues();

    public string TelegramBotToken => _values.TelegramBotToken;
    public string GoogleApiKey => _values.GoogleApiKey;
    public string SerpApiKey => _values.SerpApiKey;
    public bool HasTelegramToken => !string.IsNullOrWhiteSpace(_values.TelegramBotToken);
    public bool HasGoogleApiKey => !string.IsNullOrWhiteSpace(_values.GoogleApiKey);
    public bool HasSerpApiKey => !string.IsNullOrWhiteSpace(_values.SerpApiKey);

    public void SaveTelegramBotToken(string? token)
    {
        _values = _values with { TelegramBotToken = token?.Trim() ?? string.Empty };
        SaveValues();
    }

    public void SaveGoogleApiKey(string? apiKey)
    {
        _values = _values with { GoogleApiKey = apiKey?.Trim() ?? string.Empty };
        SaveValues();
    }

    public void SaveSerpApiKey(string? apiKey)
    {
        _values = _values with { SerpApiKey = apiKey?.Trim() ?? string.Empty };
        SaveValues();
    }

    private static SecretValues LoadValues()
    {
        if (!File.Exists(SecretPath))
        {
            return new SecretValues();
        }

        try
        {
            SecretEnvelope? envelope = JsonSerializer.Deserialize<SecretEnvelope>(File.ReadAllText(SecretPath));
            if (envelope is null)
            {
                throw new InvalidDataException("Gizli ayar dosyası boş veya geçersiz.");
            }

            return new SecretValues(
                Unprotect(envelope.TelegramBotToken),
                Unprotect(envelope.GoogleApiKey),
                Unprotect(envelope.SerpApiKey ?? string.Empty));
        }
        catch (Exception exception) when (exception is JsonException or CryptographicException or FormatException)
        {
            throw new InvalidOperationException(
                $"Yerel gizli ayarlar açılamadı. Windows hesabı değiştiyse ayarları yeniden girin. Ayrıntı: {exception.Message}",
                exception);
        }
    }

    private void SaveValues()
    {
        string directory = Path.GetDirectoryName(SecretPath)!;
        Directory.CreateDirectory(directory);
        string temporaryPath = SecretPath + ".tmp";
        SecretEnvelope envelope = new(
            Protect(_values.TelegramBotToken),
            Protect(_values.GoogleApiKey),
            Protect(_values.SerpApiKey));
        File.WriteAllText(
            temporaryPath,
            JsonSerializer.Serialize(envelope, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
        File.Move(temporaryPath, SecretPath, overwrite: true);
    }

    private static string Protect(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser));

    private static string Unprotect(string value) =>
        string.IsNullOrEmpty(value)
            ? string.Empty
            : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), Entropy, DataProtectionScope.CurrentUser));

    private sealed record SecretEnvelope(string TelegramBotToken, string GoogleApiKey, string SerpApiKey = "");
    private sealed record SecretValues(string TelegramBotToken = "", string GoogleApiKey = "", string SerpApiKey = "");
}
