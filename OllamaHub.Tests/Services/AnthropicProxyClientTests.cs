using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

public sealed class AnthropicProxyClientTests
{
    [Fact]
    public async Task SendAsync_SendsAnthropicRequestWithConfiguredHeaders()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
            {
              "content": [
                { "type": "text", "text": "Hello" }
              ],
              "stop_reason": "end_turn"
            }
            """, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicProxyClient(httpClient, NullLogger<AnthropicProxyClient>.Instance);

        var result = await client.SendAsync(CreateModel(), CreateRequest(stream: false), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Null(result.Error);
        Assert.Equal("Hello", result.Response?.Content?[0].Text);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.RequestUri?.ToString());
        Assert.Equal("secret", handler.RequestHeaders["x-api-key"]);
        Assert.Equal("2023-06-01", handler.RequestHeaders["anthropic-version"]);
        Assert.Equal("beta-value", handler.RequestHeaders["anthropic-beta"]);
        Assert.Contains("application/json", handler.AcceptHeaders);
        Assert.Contains("text/event-stream", handler.AcceptHeaders);
        Assert.NotNull(handler.RequestBody);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("claude-sonnet-4-5", document.RootElement.GetProperty("model").GetString());
        Assert.False(document.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task SendAsync_ReturnsAnthropicErrorMessageForFailureResponse()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("""
            {
              "error": {
                "type": "invalid_request_error",
                "message": "invalid model"
              }
            }
            """, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicProxyClient(httpClient, NullLogger<AnthropicProxyClient>.Instance);

        var result = await client.SendAsync(CreateModel(), CreateRequest(stream: false), CancellationToken.None);

        Assert.Equal(HttpStatusCode.BadRequest, result.StatusCode);
        Assert.Null(result.Response);
        Assert.Equal("invalid model", result.Error);
    }

    [Fact]
    public async Task SendAsync_ReturnsGenericErrorForInvalidErrorJson()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("not-json", Encoding.UTF8, "text/plain")
        });
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicProxyClient(httpClient, NullLogger<AnthropicProxyClient>.Instance);

        var result = await client.SendAsync(CreateModel(), CreateRequest(stream: false), CancellationToken.None);

        Assert.Equal(HttpStatusCode.TooManyRequests, result.StatusCode);
        Assert.Null(result.Response);
        Assert.Equal("Anthropic 请求失败，状态码 429。", result.Error);
    }

    [Fact]
    public async Task SendStreamAsync_ReturnsResponseStreamForSuccessResponse()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("data: {\"type\":\"message_delta\"}\n\n", Encoding.UTF8, "text/event-stream")
        });
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicProxyClient(httpClient, NullLogger<AnthropicProxyClient>.Instance);

        var result = await client.SendStreamAsync(CreateModel(), CreateRequest(stream: true), CancellationToken.None);

        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        Assert.Null(result.Error);
        Assert.NotNull(result.Stream);
        using var reader = new StreamReader(result.Stream!, Encoding.UTF8);
        Assert.Contains("message_delta", await reader.ReadToEndAsync());
        Assert.NotNull(handler.RequestBody);
        using var document = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(document.RootElement.GetProperty("stream").GetBoolean());
    }

    [Fact]
    public async Task SendStreamAsync_ReturnsErrorForFailureResponse()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent("""
            {
              "error": {
                "message": "bad key"
              }
            }
            """, Encoding.UTF8, "application/json")
        });
        using var httpClient = new HttpClient(handler);
        var client = new AnthropicProxyClient(httpClient, NullLogger<AnthropicProxyClient>.Instance);

        var result = await client.SendStreamAsync(CreateModel(), CreateRequest(stream: true), CancellationToken.None);

        Assert.Equal(HttpStatusCode.Unauthorized, result.StatusCode);
        Assert.Null(result.Stream);
        Assert.Equal("bad key", result.Error);
    }

    private static ResolvedModelConfig CreateModel() => new()
    {
        ModelId = "claude-sonnet-4-5",
        OllamaModelName = "claude-sonnet-4-5",
        DisplayName = "Claude Sonnet",
        ProviderId = "anthropic",
        ApiModes = ["anthropic"],
        BaseUrl = "https://api.anthropic.com",
        ApiKey = "secret",
        AnthropicModel = "claude-sonnet-4-5",
        MaxTokens = 4096,
        Headers = new Dictionary<string, string>
        {
            ["anthropic-beta"] = "beta-value"
        }
    };

    private static AnthropicMessagesRequest CreateRequest(bool stream) => new()
    {
        Model = "claude-sonnet-4-5",
        Stream = stream,
        MaxTokens = 1024,
        Messages =
        [
            new AnthropicMessage
            {
                Role = "user",
                Content =
                [
                    new AnthropicContentBlock
                    {
                        Type = "text",
                        Text = "hello"
                    }
                ]
            }
        ],
        Extra = new Dictionary<string, object?>
        {
            ["metadata"] = JsonNode.Parse("""{"source":"test"}""")
        }
    };

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        public Dictionary<string, string> RequestHeaders { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> AcceptHeaders { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            foreach (var header in request.Headers)
            {
                RequestHeaders[header.Key] = string.Join(",", header.Value);
            }

            AcceptHeaders.AddRange(request.Headers.Accept.Select(header => header.MediaType ?? string.Empty));
            return responseFactory(request);
        }
    }
}
