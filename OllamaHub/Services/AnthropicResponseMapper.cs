using System.Text.Json;
using OllamaHub.Configuration;
using OllamaHub.Contracts;

namespace OllamaHub.Services;

public interface IAnthropicResponseMapper
{
    OllamaChatChunkResponse MapMessageResponse(ResolvedModelConfig model, AnthropicMessagesResponse response);

    Task WriteStreamAsync(ResolvedModelConfig model, Stream anthropicStream, Stream output, CancellationToken cancellationToken);
}

public sealed class AnthropicResponseMapper : IAnthropicResponseMapper
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public OllamaChatChunkResponse MapMessageResponse(ResolvedModelConfig model, AnthropicMessagesResponse response)
    {
        var text = string.Concat(response.Content?
            .Where(content => string.Equals(content.Type, "text", StringComparison.OrdinalIgnoreCase))
            .Select(content => content.Text) ?? []);

        return CreateChunk(model.OllamaModelName, text, true, response.StopReason);
    }

    public async Task WriteStreamAsync(ResolvedModelConfig model, Stream anthropicStream, Stream output, CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(anthropicStream);
        await using var writer = new StreamWriter(output, leaveOpen: true);
        string? eventName = null;

        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (line.Length == 0)
            {
                eventName = null;
                continue;
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                eventName = line[6..].Trim();
                continue;
            }

            if (!line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line[5..].Trim();
            if (payload.Length == 0 || string.Equals(payload, "[DONE]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var eventType = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : eventName;

            if (string.Equals(eventType, "content_block_delta", StringComparison.OrdinalIgnoreCase)
                && root.TryGetProperty("delta", out var delta)
                && delta.TryGetProperty("text", out var textElement))
            {
                await WriteChunkAsync(writer, CreateChunk(model.OllamaModelName, textElement.GetString() ?? string.Empty, false, null), cancellationToken);
                continue;
            }

            if (string.Equals(eventType, "message_delta", StringComparison.OrdinalIgnoreCase))
            {
                var stopReason = root.TryGetProperty("delta", out var deltaNode)
                    && deltaNode.TryGetProperty("stop_reason", out var stopReasonNode)
                    ? stopReasonNode.GetString()
                    : null;

                await WriteChunkAsync(writer, CreateChunk(model.OllamaModelName, string.Empty, true, stopReason), cancellationToken);
            }
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static OllamaChatChunkResponse CreateChunk(string model, string content, bool done, string? doneReason) =>
        new()
        {
            Model = model,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Message = new OllamaChatMessage
            {
                Role = "assistant",
                Content = content
            },
            Done = done,
            DoneReason = doneReason
        };

    private static async Task WriteChunkAsync(StreamWriter writer, OllamaChatChunkResponse chunk, CancellationToken cancellationToken)
    {
        await writer.WriteLineAsync(JsonSerializer.Serialize(chunk, JsonOptions).AsMemory(), cancellationToken);
        await writer.FlushAsync(cancellationToken);
    }
}
