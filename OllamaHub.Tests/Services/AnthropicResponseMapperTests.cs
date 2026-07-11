using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

/// <summary>
/// 验证 <see cref="AnthropicResponseMapper"/> 将 Anthropic Messages API 响应
/// 转换为 Ollama NDJSON 与 OpenAI Chat Completions（含流式 SSE）格式的行为。
/// </summary>
public sealed class AnthropicResponseMapperTests
{
    /// <summary>各测试共用的已解析模型配置（Anthropic 提供方）。</summary>
    private static readonly ResolvedModelConfig TestModel = new()
    {
        ModelId = "claude-sonnet-4-5",
        OllamaModelName = "claude-sonnet-4-5",
        DisplayName = "Claude Sonnet",
        ProviderId = "anthropic",
        ApiModes = ["anthropic"],
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "secret",
        AnthropicModel = "claude-sonnet-4-5"
    };

    /// <summary>
    /// 非流式：多个 text 内容块应拼接为一条 assistant 消息，并映射 done / done_reason。
    /// </summary>
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

    /// <summary>
    /// 流式：Anthropic SSE（content_block_delta / message_delta）应转为多行 Ollama NDJSON；
    /// 文本分块、中间块 done=false，最后一包 done=true 且携带 stop_reason。
    /// </summary>
    [Fact]
    public async Task WriteStreamAsync_MapsAnthropicSseToOllamaNdjson()
    {
        var mapper = new AnthropicResponseMapper();
        // 模拟 Anthropic 流式事件：两段文本增量 + 结束原因
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

    /// <summary>
    /// OpenAI 兼容：text + tool_use 块应映射为 message.content、tool_calls 及 finish_reason；
    /// usage 中 prompt = input + cache_read，total = prompt + completion。
    /// </summary>
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

    /// <summary>
    /// stop_reason 为 tool_use 时，即使没有内容块，finish_reason 也应为 tool_calls。
    /// </summary>
    [Fact]
    public void MapOpenAiResponse_MapsToolUseStopReasonToToolCalls()
    {
        var mapper = new AnthropicResponseMapper();
        var response = new AnthropicMessagesResponse
        {
            Content = [],
            StopReason = "tool_use"
        };

        var result = mapper.MapOpenAiResponse(TestModel, response);

        Assert.Equal("tool_calls", result.Choices[0].FinishReason);
    }

    /// <summary>
    /// OpenAI 流式：文本增量应产出 role delta、content delta、finish_reason=stop，并以 data: [DONE] 结束。
    /// </summary>
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

    /// <summary>
    /// OpenAI 流式工具调用：content_block_start 映射 tool call 元数据；
    /// input_json_delta 映射 arguments 增量；stop_reason tool_use 映射 finish_reason tool_calls。
    /// </summary>
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
        Assert.Equal("tool_calls", finishChunk?.Choices[0].FinishReason);
        Assert.Equal("[DONE]", payloads[^1]);
    }

    /// <summary>
    /// OpenAI 流式：message_start / message_delta 中的 token 用量应在结束前输出 usage 块（含 cache_read）。
    /// </summary>
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

    /// <summary>
    /// OpenAI 兼容：max_tokens stop_reason 应映射为 finish_reason=length。
    /// </summary>
    [Fact]
    public void MapOpenAiResponse_MapsMaxTokensStopReasonToLength()
    {
        var mapper = new AnthropicResponseMapper();
        var response = new AnthropicMessagesResponse
        {
            Content =
            [
                new AnthropicContentBlock { Type = "text", Text = "partial" }
            ],
            StopReason = "max_tokens"
        };

        var result = mapper.MapOpenAiResponse(TestModel, response);

        Assert.Equal("length", result.Choices[0].FinishReason);
        Assert.Equal("partial", result.Choices[0].Message.Content?.GetValue<string>());
    }

    /// <summary>
    /// OpenAI 兼容：空 token 用量仍应输出 usage 对象，避免调用方因 null 分支丢失统计字段。
    /// </summary>
    [Fact]
    public void MapOpenAiResponse_MapsEmptyUsageToZeroUsage()
    {
        var mapper = new AnthropicResponseMapper();
        var response = new AnthropicMessagesResponse
        {
            Content = [],
            StopReason = "end_turn",
            Usage = new AnthropicUsage()
        };

        var result = mapper.MapOpenAiResponse(TestModel, response);

        Assert.NotNull(result.Usage);
        Assert.Equal(0, result.Usage.PromptTokens);
        Assert.Equal(0, result.Usage.CompletionTokens);
        Assert.Equal(0, result.Usage.TotalTokens);
    }

    /// <summary>
    /// Ollama 流式：非 data 行和 [DONE] 负载应被忽略，只输出有效内容增量。
    /// </summary>
    [Fact]
    public async Task WriteStreamAsync_IgnoresNonDataAndDoneLines()
    {
        var mapper = new AnthropicResponseMapper();
        var sse = """
        : keep-alive
        event: content_block_delta
        data: {"type":"content_block_delta","delta":{"text":"Hello"}}

        data: [DONE]

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

        var chunk = Assert.Single(lines);
        var result = JsonSerializer.Deserialize<OllamaChatChunkResponse>(chunk);
        Assert.Equal("Hello", result?.Message.Content);
        Assert.False(result?.Done);
    }
}
