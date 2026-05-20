using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    [Fact]
    public void MapOpenAiResponse_MapsTextAndToolCalls()
    {
        var mapper = new AnthropicResponseMapper();
        var response = new AnthropicMessagesResponse
        {
            Content =
            [
                new AnthropicContentBlock { Type = "text", Text = "Hello" },
                new AnthropicContentBlock
                {
                    Type = "tool_use",
                    Id = "call_1",
                    Name = "read_file",
                    Input = JsonNode.Parse("""{"path":"README.md"}""")
                }
            ],
            StopReason = "end_turn",
            Usage = new AnthropicUsage
            {
                InputTokens = 100,
                CacheReadInputTokens = 20,
                OutputTokens = 80
            }
        };

        var result = mapper.MapOpenAiResponse(TestModel, response);

        Assert.Equal("chat.completion", result.Object);
        Assert.Single(result.Choices);
        Assert.Equal("assistant", result.Choices[0].Message.Role);
        Assert.Equal("Hello", result.Choices[0].Message.Content?.GetValue<string>());
        Assert.Single(result.Choices[0].Message.ToolCalls!);
        Assert.Equal("call_1", result.Choices[0].Message.ToolCalls![0].Id);
        Assert.Equal("read_file", result.Choices[0].Message.ToolCalls![0].Function.Name);
        Assert.Equal("{\"path\":\"README.md\"}", result.Choices[0].Message.ToolCalls![0].Function.Arguments);
        Assert.Equal("tool_calls", result.Choices[0].FinishReason);
        Assert.Equal(120, result.Usage?.PromptTokens);
        Assert.Equal(80, result.Usage?.CompletionTokens);
        Assert.Equal(200, result.Usage?.TotalTokens);
    }

    [Fact]
    public async Task WriteOpenAiStreamAsync_MapsAnthropicSseToOpenAiSse()
    {
        var mapper = new AnthropicResponseMapper();
        var sse = """
        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"text":"Hello"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

        """;

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await using var output = new MemoryStream();

        await mapper.WriteOpenAiStreamAsync(TestModel, input, output, CancellationToken.None);

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var payloads = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                payloads.Add(line[6..]);
            }
        }

        Assert.True(payloads.Count >= 3);
        var roleChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[0]);
        var textChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[1]);
        var finishChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[2]);

        Assert.Equal("assistant", roleChunk?.Choices[0].Delta.Role);
        Assert.Equal("Hello", textChunk?.Choices[0].Delta.Content);
        Assert.Equal("stop", finishChunk?.Choices[0].FinishReason);
        Assert.Equal("[DONE]", payloads[^1]);
    }

    [Fact]
    public async Task WriteOpenAiStreamAsync_MapsToolCallStartAndArgumentDeltas()
    {
        var mapper = new AnthropicResponseMapper();
        var sse = """
        event: content_block_start
        data: {"type":"content_block_start","index":0,"content_block":{"type":"tool_use","id":"call_1","name":"read_file"}}

        event: content_block_delta
        data: {"type":"content_block_delta","index":0,"delta":{"type":"input_json_delta","partial_json":"{\"path\":\"README.md\"}"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"tool_use"}}

        """;

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await using var output = new MemoryStream();

        await mapper.WriteOpenAiStreamAsync(TestModel, input, output, CancellationToken.None);

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var payloads = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                payloads.Add(line[6..]);
            }
        }

        Assert.True(payloads.Count >= 4);
        var startChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[1]);
        var argsChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[2]);
        var finishChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[3]);

        Assert.Single(startChunk!.Choices[0].Delta.ToolCalls!);
        Assert.Equal(0, startChunk.Choices[0].Delta.ToolCalls![0].Index);
        Assert.Equal("call_1", startChunk.Choices[0].Delta.ToolCalls![0].Id);
        Assert.Equal("read_file", startChunk.Choices[0].Delta.ToolCalls![0].Function.Name);
        Assert.Equal(string.Empty, startChunk.Choices[0].Delta.ToolCalls![0].Function.Arguments);

        Assert.Single(argsChunk!.Choices[0].Delta.ToolCalls!);
        Assert.Equal(0, argsChunk.Choices[0].Delta.ToolCalls![0].Index);
        Assert.Equal("{\"path\":\"README.md\"}", argsChunk.Choices[0].Delta.ToolCalls![0].Function.Arguments);
        Assert.Equal("tool_use", finishChunk?.Choices[0].FinishReason);
        Assert.Equal("[DONE]", payloads[^1]);
    }

    [Fact]
    public async Task WriteOpenAiStreamAsync_EmitsFinalUsageChunk()
    {
        var mapper = new AnthropicResponseMapper();
        var sse = """
        event: message_start
        data: {"type":"message_start","message":{"usage":{"input_tokens":100,"cache_read_input_tokens":20}}}

        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"text":"Hello"}}

        event: message_delta
        data: {"type":"message_delta","delta":{"stop_reason":"end_turn"},"usage":{"output_tokens":80}}

        """;

        await using var input = new MemoryStream(Encoding.UTF8.GetBytes(sse));
        await using var output = new MemoryStream();

        await mapper.WriteOpenAiStreamAsync(TestModel, input, output, CancellationToken.None);

        output.Position = 0;
        using var reader = new StreamReader(output, Encoding.UTF8);
        var payloads = new List<string>();
        while (await reader.ReadLineAsync() is { } line)
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                payloads.Add(line[6..]);
            }
        }

        Assert.True(payloads.Count >= 5);
        var usageChunk = JsonSerializer.Deserialize<OpenAIChatCompletionChunk>(payloads[^2]);
        Assert.Equal(120, usageChunk?.Usage?.PromptTokens);
        Assert.Equal(80, usageChunk?.Usage?.CompletionTokens);
        Assert.Equal(200, usageChunk?.Usage?.TotalTokens);
        Assert.Equal("[DONE]", payloads[^1]);
    }
}
