using System.Text;
using System.Text.Json;

namespace ArsanGazERP.AgentWatcher;

internal sealed class AppSettings
{
    private string? _settingsPath;

    public bool StartWithWindows { get; set; } = true;
    public bool StartErp { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool QueuePaused { get; set; }
    public bool RunInBackground { get; set; } = true;
    public bool EnableDailyReview { get; set; }
    public bool EnableDailyStatusReport { get; set; }
    public bool EnableDailyResearch { get; set; }
    public bool EnableWorkflowProgressNotifications { get; set; }
    public bool AutoForwardAgentRecommendations { get; set; } = true;
    public string AutoForwardAgentRecommendationsDate { get; set; } = "";
    public int AutoForwardAgentRecommendationsCount { get; set; }
    public Dictionary<string, string> AgentInstructions { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public AutonomyPolicy GozcuAutonomy { get; set; } = new();
    public bool ForwardTelegramCommandsToCopilotChat { get; set; } = true;
    public string CodingBridge { get; set; } = CodingBridgeNames.Cursor;
    public long TelegramConversationResetUpdateId { get; set; }
    public int ResearchIntervalHours { get; set; } = 12;
    public string LastResearchAttemptUtc { get; set; } = "";
    public string ResearchRetryAfterUtc { get; set; } = "";
    public int ResearchCycle { get; set; }
    public string LastDailyStatusReportDate { get; set; } = "";
    public string LastDailyReviewQueueDate { get; set; } = "";
    public int PollSeconds { get; set; } = 5;
    public string DefaultProvider { get; set; } = "Cloudflare Workers AI";
    public string NvidiaModel { get; set; } = "deepseek-ai/deepseek-v4.1-flash";
    public string ResearchProvider { get; set; } = "SerpApi";
    public string GoogleSearchEngineId { get; set; } = "";
    public string TelegramChatId { get; set; } = "";
    // true: the server agent owns Telegram getUpdates and the 7/24 relay heartbeat; this PC only sends messages.
    public bool TelegramPollingDisabled { get; set; }

    public static AppSettings Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AppSettings settings;
        try
        {
            using FileStream stream = File.OpenRead(path);
            settings = JsonSerializer.Deserialize<AppSettings>(stream)
                ?? throw new InvalidDataException("AgentWatcher ayar dosyası boş veya null.");
        }
        catch (FileNotFoundException)
        {
            return new AppSettings { _settingsPath = path };
        }
        catch (DirectoryNotFoundException)
        {
            return new AppSettings { _settingsPath = path };
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"AgentWatcher ayar dosyası geçerli JSON değil ve otomatik varsayılana sıfırlanmadı: {path}",
                exception);
        }
        catch (IOException exception)
        {
            throw new IOException(
                $"AgentWatcher ayar dosyası okunamadı ve otomatik varsayılana sıfırlanmadı: {path}",
                exception);
        }
        catch (UnauthorizedAccessException exception)
        {
            throw new UnauthorizedAccessException(
                $"AgentWatcher ayar dosyasına erişilemiyor ve otomatik varsayılana sıfırlanmadı: {path}",
                exception);
        }

        settings._settingsPath = path;
        if (settings.GozcuAutonomy is null)
            throw new InvalidDataException("Gözcü otomasyon politikası null olamaz.");
        settings.GozcuAutonomy.Validate();
        AgentRecommendationPolicy.ValidateSettings(settings);
        settings.AgentInstructions = AgentInstructionPolicy.NormalizeAll(settings.AgentInstructions);
        if (string.Equals(settings.DefaultProvider, "Copilot Pro", StringComparison.OrdinalIgnoreCase))
        {
            settings.DefaultProvider = "Cloudflare Workers AI";
        }

        settings.CodingBridge = CodingBridgeNames.Normalize(settings.CodingBridge);

        return settings;
    }

    public void Save()
    {
        AgentRecommendationPolicy.ValidateSettings(this);
        string path = _settingsPath
            ?? throw new InvalidOperationException("AgentWatcher ayar dosyası yüklenmeden kaydedilemez.");
        string directory = Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException("AgentWatcher ayar dosyası için klasör yolu geçersiz.");
        Directory.CreateDirectory(directory);
        string temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}
