using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

public sealed class AnthropicRequestFactoryTests
{
    [Fact]
    public void Create_MapsSystemAndChatMessages()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiMode = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5",
            MaxTokens = 4096,
            Temperature = 0.2,
            TopP = 0.9,
            Extra = new Dictionary<string, JsonNode?>
            {
                ["service_tier"] = "standard_only"
            }
        };

        var request = new OllamaChatRequest
        {
            Model = "claude-sonnet-4-5",
            Stream = true,
            Options = new OllamaChatOptions
            {
                NumPredict = 2048
            },
            Messages =
            [
                new OllamaChatMessage { Role = "system", Content = "you are helpful" },
                new OllamaChatMessage { Role = "user", Content = "hello" },
                new OllamaChatMessage { Role = "assistant", Content = "hi" }
            ]
        };

        var result = factory.Create(model, request);

        Assert.Equal("claude-sonnet-4-5", result.Model);
        Assert.Equal("you are helpful", result.System);
        Assert.True(result.Stream);
        Assert.Equal(2048, result.MaxTokens);
        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("user", result.Messages[0].Role);
        Assert.Equal("hello", result.Messages[0].Content[0].Text);
        Assert.Equal("assistant", result.Messages[1].Role);
        Assert.Equal("standard_only", result.Extra["service_tier"]?.GetValue<string>());
    }

    [Fact]
    public void Create_MapsToolCallsAndToolResults()
    {
        var factory = new AnthropicRequestFactory();
        var model = new ResolvedModelConfig
        {
            ModelId = "claude-sonnet-4-5",
            OllamaModelName = "claude-sonnet-4-5",
            DisplayName = "Claude Sonnet",
            ProviderId = "anthropic",
            ApiMode = "anthropic",
            BaseUrl = "https://api.anthropic.com",
            ApiKey = "secret",
            AnthropicModel = "claude-sonnet-4-5"
        };

        var request = new OllamaChatRequest
        {
            Model = "claude-sonnet-4-5",
            Messages =
            [
                new OllamaChatMessage
                {
                    Role = "assistant",
                    Content = "calling tool",
                    ToolCalls =
                    [
                        new OllamaToolCall
                        {
                            Id = "call_1",
                            Function = new OllamaToolCallFunction
                            {
                                Name = "read_file",
                                Arguments = JsonNode.Parse("""{"path":"README.md"}""")
                            }
                        }
                    ]
                },
                new OllamaChatMessage
                {
                    Role = "tool",
                    ToolCallId = "call_1",
                    ToolName = "read_file",
                    Content = "# README"
                }
            ]
        };

        var result = factory.Create(model, request);

        Assert.Equal(2, result.Messages.Count);
        Assert.Equal("assistant", result.Messages[0].Role);
        Assert.Equal(2, result.Messages[0].Content.Count);
        Assert.Equal("text", result.Messages[0].Content[0].Type);
        Assert.Equal("tool_use", result.Messages[0].Content[1].Type);
        Assert.Equal("call_1", result.Messages[0].Content[1].Id);
        Assert.Equal("read_file", result.Messages[0].Content[1].Name);
        Assert.Equal("README.md", result.Messages[0].Content[1].Input?["path"]?.GetValue<string>());

        Assert.Equal("user", result.Messages[1].Role);
        Assert.Single(result.Messages[1].Content);
        Assert.Equal("tool_result", result.Messages[1].Content[0].Type);
        Assert.Equal("call_1", result.Messages[1].Content[0].ToolUseId);
        Assert.Equal("# README", result.Messages[1].Content[0].Content);
    }
}
