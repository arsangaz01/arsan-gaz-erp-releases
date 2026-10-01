using System.Text.Json.Serialization;

namespace ArsanGazERP.Services.V9;

public sealed record NvidiaChatMessage(
    [property: JsonPropertyName("role")] string Role,
    [property: JsonPropertyName("content")] string Content);

public sealed class NvidiaChatRequest
{
    [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;
    [JsonPropertyName("messages")] public IReadOnlyList<NvidiaChatMessage> Messages { get; init; } = [];
    [JsonPropertyName("temperature")] public double Temperature { get; init; }
    [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
    [JsonPropertyName("stream")] public bool Stream { get; init; }
}

public sealed class NvidiaChatResponse
{
    [JsonPropertyName("id")] public string? Id { get; init; }
    [JsonPropertyName("choices")] public List<NvidiaChoice> Choices { get; init; } = [];
    [JsonPropertyName("usage")] public NvidiaUsage? Usage { get; init; }
}

public sealed class NvidiaChoice
{
    [JsonPropertyName("message")] public NvidiaChatMessage? Message { get; init; }
    [JsonPropertyName("finish_reason")] public string? FinishReason { get; init; }
}

public sealed class NvidiaUsage
{
    [JsonPropertyName("prompt_tokens")] public int PromptTokens { get; init; }
    [JsonPropertyName("completion_tokens")] public int CompletionTokens { get; init; }
    [JsonPropertyName("total_tokens")] public int TotalTokens { get; init; }
}

public sealed record NvidiaNimResult(bool Success, string Content, string Model, int TotalTokens, string? Error = null);
public sealed record NvidiaNimHealth(bool Configured, bool Reachable, string Model, string Status, DateTimeOffset CheckedAtUtc);
