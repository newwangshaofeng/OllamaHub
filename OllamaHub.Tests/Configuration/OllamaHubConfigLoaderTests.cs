using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using Xunit;

namespace OllamaHub.Tests.Configuration;

public sealed class OllamaHubConfigLoaderTests
{
    [Fact]
    public void LoadModels_ResolvesAnthropicModelsAndAliases()
    {
        var configPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configPath, """
            {
              "baseUrl": "https://unused.example.com",
              "providers": [
                {
                  "id": "anthropic",
                  "baseUrl": "https://api.anthropic.com",
                  "apiKey": "test-key",
                  "apiMode": "anthropic",
                  "headers": {
                    "anthropic-beta": "tools-2024-04-04"
                  }
                }
              ],
              "models": [
                {
                  "id": "claude-sonnet-4-5",
                  "configId": "fast",
                  "owned_by": "anthropic",
                  "family": "claude",
                  "context_length": 200000,
                  "max_tokens": 8192,
                  "headers": {
                    "x-test": "1"
                  },
                  "extra": {
                    "service_tier": "standard_only"
                  }
                },
                {
                  "id": "ignored-openai",
                  "owned_by": "anthropic",
                  "apiMode": "openai"
                }
              ]
            }
            """);

            var models = OllamaHubConfigLoader.LoadModels(configPath, NullLogger.Instance);

            Assert.Equal(2, models.Count);
            var model = Assert.Single(models, candidate => candidate.ModelId == "claude-sonnet-4-5");
            Assert.Equal("claude-sonnet-4-5::fast", model.OllamaModelName);
            Assert.Equal("claude-sonnet-4-5", model.AnthropicModel);
            Assert.Equal(["anthropic"], model.ApiModes);
            Assert.Equal("https://api.anthropic.com", model.BaseUrl);
            Assert.Equal("test-key", model.ApiKey);
            Assert.Equal("tools-2024-04-04", model.Headers["anthropic-beta"]);
            Assert.Equal("1", model.Headers["x-test"]);
            Assert.Equal("standard_only", model.Extra["service_tier"]?.GetValue<string>());

            var openAiModel = Assert.Single(models, candidate => candidate.ModelId == "ignored-openai");
            Assert.Equal(["openai"], openAiModel.ApiModes);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void LoadServer_ResolvesHostAndPortToUrl()
    {
        var configPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configPath, """
            {
              "host": "127.0.0.1",
              "port": 11434,
              "models": []
            }
            """);

            var server = OllamaHubConfigLoader.LoadServer(configPath, NullLogger.Instance);

            var url = Assert.Single(server.Urls);
            Assert.Equal("http://127.0.0.1:11434", url);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void FindModel_AllowsMatchingByBaseModelId()
    {
        var models = new[]
        {
            new ResolvedModelConfig
            {
                ModelId = "claude-sonnet-4-5",
                OllamaModelName = "claude-sonnet-4-5",
                DisplayName = "Claude Sonnet 4.5",
                ProviderId = "anthropic",
                ApiModes = ["anthropic"],
                BaseUrl = "https://api.anthropic.com",
                ApiKey = "test-key",
                AnthropicModel = "claude-sonnet-4-5"
            },
            new ResolvedModelConfig
            {
                ModelId = "claude-sonnet-4-5",
                OllamaModelName = "claude-sonnet-4-5::thinking",
                DisplayName = "Claude Sonnet 4.5 Thinking",
                ProviderId = "anthropic",
                ApiModes = ["anthropic"],
                BaseUrl = "https://api.anthropic.com",
                ApiKey = "test-key",
                AnthropicModel = "claude-sonnet-4-5"
            }
        };

        var result = OllamaHubConfigLoader.FindModel(models, "claude-sonnet-4-5");

        Assert.NotNull(result);
        Assert.Equal("claude-sonnet-4-5", result.OllamaModelName);
    }

    [Fact]
    public void LoadModels_ParsesSemicolonSeparatedApiModes()
    {
        var configPath = Path.GetTempFileName();

        try
        {
            File.WriteAllText(configPath, """
            {
              "providers": [
                {
                  "id": "hybrid",
                  "baseUrl": "https://api.example.com",
                  "apiKey": "test-key",
                  "apiMode": "openai; anthropic"
                }
              ],
              "models": [
                {
                  "id": "model-a",
                  "owned_by": "hybrid"
                }
              ]
            }
            """);

            var model = Assert.Single(OllamaHubConfigLoader.LoadModels(configPath, NullLogger.Instance));

            Assert.Equal(["openai", "anthropic"], model.ApiModes);
            Assert.True(model.SupportsApiMode("openai"));
            Assert.True(model.SupportsApiMode("anthropic"));
            Assert.False(model.SupportsApiMode("ollama"));
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
