using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OllamaHub.Configuration;
using OllamaHub.Contracts;

namespace OllamaHub.Services;

public interface IAnthropicProxyClient
{
    Task<(HttpStatusCode StatusCode, OllamaChatChunkResponse? Response, string? Error)> SendAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken);

    Task<(HttpStatusCode StatusCode, Stream? Stream, string? Error)> SendStreamAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken);
}

public sealed class AnthropicProxyClient(HttpClient httpClient) : IAnthropicProxyClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<(HttpStatusCode StatusCode, OllamaChatChunkResponse? Response, string? Error)> SendAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken)
    {
        using var message = BuildRequestMessage(model, request);
        using var response = await httpClient.SendAsync(message, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return (response.StatusCode, null, await ReadErrorAsync(response, cancellationToken));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var result = await JsonSerializer.DeserializeAsync<AnthropicMessagesResponse>(stream, JsonOptions, cancellationToken);
        if (result is null)
        {
            return (HttpStatusCode.BadGateway, null, "Anthropic 返回了空响应。");
        }

        var mapped = new AnthropicResponseMapper().MapMessageResponse(model, result);
        return (response.StatusCode, mapped, null);
    }

    public async Task<(HttpStatusCode StatusCode, Stream? Stream, string? Error)> SendStreamAsync(ResolvedModelConfig model, AnthropicMessagesRequest request, CancellationToken cancellationToken)
    {
        var message = BuildRequestMessage(model, request);
        var response = await httpClient.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await ReadErrorAsync(response, cancellationToken);
            response.Dispose();
            message.Dispose();
            return (response.StatusCode, null, error);
        }

        message.Dispose();
        return (response.StatusCode, await response.Content.ReadAsStreamAsync(cancellationToken), null);
    }

    private static HttpRequestMessage BuildRequestMessage(ResolvedModelConfig model, AnthropicMessagesRequest request)
    {
        var message = new HttpRequestMessage(HttpMethod.Post, $"{model.BaseUrl}/v1/messages");
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        message.Headers.Add("x-api-key", model.ApiKey);
        message.Headers.Add("anthropic-version", "2023-06-01");

        var payload = JsonSerializer.Serialize(request, JsonOptions);
        message.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        foreach (var header in model.Headers)
        {
            if (!message.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                message.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return message;
    }

    private static async Task<string> ReadErrorAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var error = await JsonSerializer.DeserializeAsync<AnthropicErrorEnvelope>(stream, JsonOptions, cancellationToken);
            return error?.Error?.Message ?? $"Anthropic 请求失败，状态码 {(int)response.StatusCode}。";
        }
        catch
        {
            return $"Anthropic 请求失败，状态码 {(int)response.StatusCode}。";
        }
    }
}
