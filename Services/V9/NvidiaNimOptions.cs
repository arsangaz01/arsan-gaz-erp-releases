namespace ArsanGazERP.Services.V9;

public sealed class NvidiaNimOptions
{
    public const string ApiKeyEnvironmentVariable = "ARSANGAZ_NVIDIA_API_KEY";
    public string BaseUrl { get; init; } = "https://integrate.api.nvidia.com";
    public string Model { get; init; } = "deepseek-ai/deepseek-v4.1-flash";
    public int TimeoutSeconds { get; init; } = 120;
    public double Temperature { get; init; } = 0.2;
    public int MaxTokens { get; init; } = 2048;
}
