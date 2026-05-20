using System.Text;
using System.Text.Json;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

public sealed class AnthropicResponseMapperTests
{
    private static readonly ResolvedModelConfig TestModel = new()
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

    [Fact]
    public void MapMessageResponse_CombinesTextBlocks()
    {
        var mapper = new AnthropicResponseMapper();
        var response = new AnthropicMessagesResponse
        {
            Content =
            [
                new AnthropicContentBlock { Type = "text", Text = "Hello" },
                new AnthropicContentBlock { Type = "text", Text = " world" }
            ],
            StopReason = "end_turn"
        };

        var result = mapper.MapMessageResponse(TestModel, response);

        Assert.Equal("claude-sonnet-4-5", result.Model);
        Assert.Equal("assistant", result.Message.Role);
        Assert.Equal("Hello world", result.Message.Content);
        Assert.True(result.Done);
        Assert.Equal("end_turn", result.DoneReason);
    }

    [Fact]
    public async Task WriteStreamAsync_MapsAnthropicSseToOllamaNdjson()
    {
        var mapper = new AnthropicResponseMapper();
        var sse = """
        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"text":"Hello"}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"text":" world"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

        """;

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await using var output = new MemoryStream();

        await mapper.WriteStreamAsync(TestModel, input, output, CancellationToken.None);

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var lines = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        Assert.Equal(3, lines.Count);

        var first = JsonSerializer.Deserialize<OllamaChatChunkResponse>(lines[0]);
        var second = JsonSerializer.Deserialize<OllamaChatChunkResponse>(lines[1]);
        var last = JsonSerializer.Deserialize<OllamaChatChunkResponse>(lines[2]);

        Assert.Equal("Hello", first?.Message.Content);
        Assert.False(first?.Done);
        Assert.Equal(" world", second?.Message.Content);
        Assert.False(second?.Done);
        Assert.True(last?.Done);
        Assert.Equal("end_turn", last?.DoneReason);
    }
}
