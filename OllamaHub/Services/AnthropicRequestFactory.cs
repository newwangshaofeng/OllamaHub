using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;

namespace OllamaHub.Services;

public interface IAnthropicRequestFactory
{
    AnthropicMessagesRequest Create(ResolvedModelConfig model, OllamaChatRequest request);
}

public sealed class AnthropicRequestFactory : IAnthropicRequestFactory
{
    public AnthropicMessagesRequest Create(ResolvedModelConfig model, OllamaChatRequest request)
    {
        var systemMessages = new List<string>();
        var messages = new List<AnthropicMessage>();

        foreach (var message in request.Messages)
        {
            if (string.Equals(message.Role, "system", StringComparison.OrdinalIgnoreCase))
            {
                if (!string.IsNullOrWhiteSpace(message.Content))
                {
                    systemMessages.Add(message.Content);
                }

                continue;
            }

            if (string.Equals(message.Role, "tool", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new AnthropicMessage
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
                });

                continue;
            }

            var role = string.Equals(message.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                ? "assistant"
                : "user";

            var content = new List<AnthropicContentBlock>();
            if (!string.IsNullOrWhiteSpace(message.Content))
            {
                content.Add(new AnthropicContentBlock
                {
                    Type = "text",
                    Text = message.Content
                });
            }

            if (message.ToolCalls is not null)
            {
                foreach (var toolCall in message.ToolCalls)
                {
                    content.Add(new AnthropicContentBlock
                    {
                        Type = "tool_use",
                        Id = toolCall.Id,
                        Name = toolCall.Function.Name,
                        Input = toolCall.Function.Arguments
                    });
                }
            }

            messages.Add(new AnthropicMessage
            {
                Role = role,
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
            });
        }

        var extra = new Dictionary<string, JsonNode?>(model.Extra, StringComparer.OrdinalIgnoreCase);
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
}
