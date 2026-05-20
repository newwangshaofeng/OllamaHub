using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OllamaHub.Contracts;

public sealed class AnthropicMessagesRequest
{
    [JsonPropertyName("model")]
    public required string Model { get; init; }

    [JsonPropertyName("system")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? System { get; init; }

    [JsonPropertyName("messages")]
    public required IReadOnlyList<AnthropicMessage> Messages { get; init; }

    [JsonPropertyName("stream")]
    public bool Stream { get; init; }

    [JsonPropertyName("temperature")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? Temperature { get; init; }

    [JsonPropertyName("top_p")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public double? TopP { get; init; }

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonExtensionData]
    public Dictionary<string, JsonNode?> Extra { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class AnthropicMessage
{
    [JsonPropertyName("role")]
    public required string Role { get; init; }

    [JsonPropertyName("content")]
    public required IReadOnlyList<AnthropicContentBlock> Content { get; init; }
}

public sealed class AnthropicContentBlock
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("text")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Text { get; init; }

    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Id { get; init; }

    [JsonPropertyName("name")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Name { get; init; }

    [JsonPropertyName("input")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonNode? Input { get; init; }

    [JsonPropertyName("tool_use_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ToolUseId { get; init; }

    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Content { get; init; }

    [JsonPropertyName("is_error")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? IsError { get; init; }
}

public sealed class AnthropicMessagesResponse
{
    [JsonPropertyName("content")]
    public IReadOnlyList<AnthropicContentBlock>? Content { get; init; }

    [JsonPropertyName("stop_reason")]
    public string? StopReason { get; init; }
}

public sealed class AnthropicErrorEnvelope
{
    [JsonPropertyName("error")]
    public AnthropicErrorDetail? Error { get; init; }
}

public sealed class AnthropicErrorDetail
{
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}