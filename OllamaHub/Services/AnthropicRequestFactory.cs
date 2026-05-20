using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;

namespace OllamaHub.Services;

public interface IAnthropicRequestFactory
{
    AnthropicMessagesRequest Create(ResolvedModelConfig model, OllamaChatRequest request);

    AnthropicMessagesRequest Create(ResolvedModelConfig model, OpenAIChatCompletionsRequest request);
}

public sealed class AnthropicRequestFactory : IAnthropicRequestFactory
{
    public AnthropicMessagesRequest Create(ResolvedModelConfig model, OllamaChatRequest request)
    {
        var extra = new Dictionary<string, JsonNode?>(model.Extra, StringComparer.OrdinalIgnoreCase);
        var systemMessages = new List<string>();
        var messages = request.Messages.SelectMany(message => ConvertOllamaMessage(message, systemMessages)).ToList();

        return new AnthropicMessagesRequest
        {
            Model = model.AnthropicModel,
            System = systemMessages.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, systemMessages) : null,
            Messages = messages,
            Stream = request.Stream,
            Temperature = request.Options?.Temperature ?? model.Temperature,
            TopP = request.Options?.TopP ?? model.TopP,
            MaxTokens = request.Options?.NumPredict ?? model.MaxTokens,
            Extra = extra
        };
    }

    public AnthropicMessagesRequest Create(ResolvedModelConfig model, OpenAIChatCompletionsRequest request)
    {
        var systemMessages = new List<string>();
        var extra = new Dictionary<string, JsonNode?>(model.Extra, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in request.Extra)
        {
            extra[pair.Key] = pair.Value;
        }

        var messages = request.Messages.SelectMany(message => ConvertOpenAiMessage(message, systemMessages)).ToList();

        return new AnthropicMessagesRequest
        {
            Model = model.AnthropicModel,
            System = systemMessages.Count > 0 ? string.Join(Environment.NewLine + Environment.NewLine, systemMessages) : null,
            Messages = messages,
            Stream = request.Stream,
            Temperature = request.Temperature ?? model.Temperature,
            TopP = request.TopP ?? model.TopP,
            MaxTokens = request.MaxTokens ?? model.MaxTokens,
            Tools = request.Tools?.Select(tool => new AnthropicToolDefinition
            {
                Name = tool.Function.Name,
                Description = tool.Function.Description,
                InputSchema = tool.Function.Parameters
            }).ToArray(),
            ToolChoice = request.ToolChoice,
            Extra = extra
        };
    }

    private static IEnumerable<AnthropicMessage> ConvertOllamaMessage(OllamaChatMessage message, List<string> systemMessages)
    {
        if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                systemMessages.Add(message.Content);
            }

            return [];
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContentBlock
                        {
                            Type = "tool_result",
                            ToolUseId = message.ToolCallId ?? message.ToolName ?? "tool-call",
                            Content = message.Content ?? string.Empty,
                            IsError = false
                        }
                    ]
                }
            ];
        }

        return [CreateStandardMessage(message.Role, message.Content, message.ToolCalls?.Select(ToAnthropicToolUse).ToArray())];
    }

    private static IEnumerable<AnthropicMessage> ConvertOpenAiMessage(OpenAIChatMessage message, List<string> systemMessages)
    {
        if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
        {
            var text = ExtractText(message.Content);
            if (!string.IsNullOrWhiteSpace(text))
            {
                systemMessages.Add(text);
            }

            return [];
        }

        if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new AnthropicMessage
                {
                    Role = "user",
                    Content =
                    [
                        new AnthropicContentBlock
                        {
                            Type = "tool_result",
                            ToolUseId = message.ToolCallId ?? message.Name ?? "tool-call",
                            Content = ExtractText(message.Content) ?? string.Empty,
                            IsError = false
                        }
                    ]
                }
            ];
        }

        var toolCalls = message.ToolCalls?.Select(toolCall => new AnthropicContentBlock
        {
            Type = "tool_use",
            Id = toolCall.Id,
            Name = toolCall.Function.Name,
            Input = ParseArguments(toolCall.Function.Arguments)
        }).ToArray();

        return [CreateStandardMessage(message.Role, ExtractText(message.Content), toolCalls)];
    }

    private static AnthropicMessage CreateStandardMessage(string role, string? text, IReadOnlyList<AnthropicContentBlock>? toolCalls)
    {
        var content = new List<AnthropicContentBlock>();
        if (!string.IsNullOrWhiteSpace(text))
        {
            content.Add(new AnthropicContentBlock
            {
                Type = "text",
                Text = text
            });
        }

        if (toolCalls is not null)
        {
            content.AddRange(toolCalls);
        }

        return new AnthropicMessage
        {
            Role = string.Equals(role, "assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user",
            Content = content.Count > 0
                ? content
                :
                [
                    new AnthropicContentBlock
                    {
                        Type = "text",
                        Text = string.Empty
                    }
                ]
        };
    }

    private static AnthropicContentBlock ToAnthropicToolUse(OllamaToolCall toolCall) =>
        new()
        {
            Type = "tool_use",
            Id = toolCall.Id,
            Name = toolCall.Function.Name,
            Input = toolCall.Function.Arguments
        };

    private static JsonNode? ParseArguments(string arguments)
    {
        if (string.IsNullOrWhiteSpace(arguments))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(arguments);
        }
        catch
        {
            return JsonValue.Create(arguments);
        }
    }

    private static string? ExtractText(JsonNode? content)
    {
        return content switch
        {
            null => null,
            JsonValue value => value.TryGetValue<string>(out var text) ? text : content.ToJsonString(),
            JsonArray array => string.Join(string.Empty, array.SelectMany(static item => ExtractContentParts(item))),
            _ => content.ToJsonString()
        };
    }

    private static IEnumerable<string> ExtractContentParts(JsonNode? node)
    {
        if (node is null)
        {
            return [];
        }

        if (node is JsonObject obj)
        {
            if (obj["type"]?.GetValue<string>() == "text" && obj["text"] is JsonNode textNode)
            {
                var text = textNode.GetValue<string>();
                return string.IsNullOrEmpty(text) ? [] : [text];
            }

            return [];
        }

        if (node is JsonValue value && value.TryGetValue<string>(out var stringValue))
        {
            return string.IsNullOrEmpty(stringValue) ? [] : [stringValue];
        }

        return [];
    }
}
