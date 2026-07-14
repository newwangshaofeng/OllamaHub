using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Services;
using Xunit;

namespace OllamaHub.Tests.Services;

public sealed class ProtocolPassthroughClientTests
{
    [Fact]
    public async Task ProxyAsync_OpenAiRequest_SendsConfiguredModelId()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek/deepseek-v4-pro",
            OllamaModelName = "360智脑/deepseek-v4-pro",
            DisplayName = "360智脑/deepseek-v4-pro",
            ProviderId = "360智脑",
            ApiModes = ["openai"],
            BaseUrl = "https://api.360.cn",
            ApiKey = "secret",
            AnthropicModel = "deepseek/deepseek-v4-pro"
        };

        var payload = new OpenAIChatCompletionsRequest
        {
            Model = model.ModelId,
            Messages =
            [
                new OpenAIChatMessage
                {
                    Role = "user",
                    Content = JsonValue.Create("hello")
                }
            ],
            Stream = false
        };

        await client.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", payload, CancellationToken.None);

        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("deepseek/deepseek-v4-pro", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProxyAsync_OpenAiBaseUrlAlreadyContainsV1_DoesNotDuplicatePath()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Request.QueryString = new QueryString("?api-version=2026-01-01");
        httpContext.Response.Body = new MemoryStream();

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek-v4-pro",
            OllamaModelName = "deepseek-v4-pro",
            DisplayName = "deepseek-v4-pro",
            ProviderId = "OpenCodeGeneric",
            ApiModes = ["openai"],
            BaseUrl = "https://opencode.ai/zen/go/v1",
            ApiKey = "secret",
            AnthropicModel = "deepseek-v4-pro"
        };

        var payload = new OpenAIChatCompletionsRequest
        {
            Model = model.ModelId,
            Messages =
            [
                new OpenAIChatMessage
                {
                    Role = "user",
                    Content = JsonValue.Create("hello")
                }
            ]
        };

        await client.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", payload, CancellationToken.None);

        Assert.Equal(
            "https://opencode.ai/zen/go/v1/chat/completions?api-version=2026-01-01",
            handler.RequestUri?.ToString());
    }

    [Fact]
    public async Task ProxyAsync_OpenAiJsonNode_PreservesRawJsonFields()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek/deepseek-v4-pro",
            OllamaModelName = "360智脑/deepseek-v4-pro",
            DisplayName = "360智脑/deepseek-v4-pro",
            ProviderId = "360智脑",
            ApiModes = ["openai"],
            BaseUrl = "https://api.360.cn",
            ApiKey = "secret",
            AnthropicModel = "deepseek/deepseek-v4-pro"
        };

        var payload = JsonNode.Parse("""
        {
          "model": "alias-model",
          "messages": [
            {
              "role": "user",
              "content": [
                { "type": "text", "text": "hello" }
              ]
            }
          ],
          "tool_choice": { "type": "function", "function": { "name": "read_file" } },
          "custom_field": { "enabled": true }
        }
        """)!.AsObject();

        payload["model"] = model.ModelId;

        await client.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", payload, CancellationToken.None);

        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("deepseek/deepseek-v4-pro", json.RootElement.GetProperty("model").GetString());
        Assert.Equal("function", json.RootElement.GetProperty("tool_choice").GetProperty("type").GetString());
        Assert.True(json.RootElement.GetProperty("custom_field").GetProperty("enabled").GetBoolean());
        Assert.Equal("text", json.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ProxyAsync_OpenAiJsonNode_MergesModelExtraFieldsAtTopLevel()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        var payload = JsonNode.Parse("""
        {
          "model": "alias-model",
          "messages": [
            {"role": "user", "content": "Hello!"}
          ]
        }
        """)!.AsObject();

        payload["model"] = "deepseek/deepseek-v4-pro";

        var modelExtraValue = JsonNode.Parse("""
        {
          "type": "enabled"
        }
        """);

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek/deepseek-v4-pro",
            OllamaModelName = "360智脑/deepseek-v4-pro",
            DisplayName = "360智脑/deepseek-v4-pro",
            ProviderId = "360智脑",
            ApiModes = ["openai"],
            BaseUrl = "https://api.360.cn",
            ApiKey = "secret",
            AnthropicModel = "deepseek/deepseek-v4-pro",
            Extra = new Dictionary<string, JsonNode?>
            {
                ["thinking"] = modelExtraValue,
                ["reasoning_effort"] = JsonValue.Create("high")
            }
        };

        // Merge model.Extra fields at top level (same behavior as HandleChatCompletionsAsync)
        foreach (var kvp in model.Extra)
        {
            payload[kvp.Key] = kvp.Value?.DeepClone();
        }

        await client.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", payload, CancellationToken.None);

        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.True(json.RootElement.GetProperty("thinking").GetProperty("type").GetString() == "enabled");
        Assert.Equal("high", json.RootElement.GetProperty("reasoning_effort").GetString());
        Assert.Equal("deepseek/deepseek-v4-pro", json.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task ProxyAsync_OllamaRequest_SendsConfiguredModelId()
    {
        var handler = new CapturingHandler();
        using var httpClient = new HttpClient(handler);
        var client = new ProtocolPassthroughClient(httpClient, NullLogger<ProtocolPassthroughClient>.Instance);
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Method = HttpMethods.Post;
        httpContext.Request.ContentType = "application/json";
        httpContext.Response.Body = new MemoryStream();

        var model = new ResolvedModelConfig
        {
            ModelId = "deepseek/deepseek-v4-pro",
            OllamaModelName = "360智脑/deepseek-v4-pro",
            DisplayName = "360智脑/deepseek-v4-pro",
            ProviderId = "360智脑",
            ApiModes = ["ollama"],
            BaseUrl = "https://api.360.cn",
            ApiKey = "secret",
            AnthropicModel = "deepseek/deepseek-v4-pro"
        };

        var payload = new OllamaChatRequest
        {
            Model = model.ModelId,
            Messages =
            [
                new OllamaChatMessage
                {
                    Role = "user",
                    Content = "hello"
                }
            ],
            Stream = false
        };

        await client.ProxyAsync(httpContext, model, "ollama", "/api/chat", payload, CancellationToken.None);

        Assert.NotNull(handler.RequestBody);
        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("deepseek/deepseek-v4-pro", json.RootElement.GetProperty("model").GetString());
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json")
            };
        }
    }
}
