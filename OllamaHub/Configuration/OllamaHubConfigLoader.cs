using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OllamaHub.Configuration;

public interface IOllamaHubConfigProvider
{
    string ConfigPath { get; }

    IReadOnlyList<string> GetServerUrls();

    IReadOnlyList<ResolvedModelConfig> GetModels();

    ResolvedModelConfig? FindModel(string modelName);
}

public sealed class OllamaHubConfigLoader : IOllamaHubConfigProvider
{
    public const string DefaultConfigFileName = "settings.json";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private readonly IReadOnlyList<ResolvedModelConfig> _models;
    private readonly IReadOnlyList<string> _serverUrls;

    public OllamaHubConfigLoader(ILogger<OllamaHubConfigLoader> logger)
    {
        ConfigPath = Path.Combine(AppContext.BaseDirectory, DefaultConfigFileName);
        var resolved = Load(ConfigPath, logger);
        _models = resolved.Models;
        _serverUrls = resolved.Server.Urls;
    }

    public string ConfigPath { get; }

    public IReadOnlyList<string> GetServerUrls() => _serverUrls;

    public IReadOnlyList<ResolvedModelConfig> GetModels() => _models;

    public ResolvedModelConfig? FindModel(string modelName) => FindModel(_models, modelName);

    internal static IReadOnlyList<ResolvedModelConfig> LoadModels(string configPath, ILogger logger) => Load(configPath, logger).Models;

    internal static ResolvedServerConfig LoadServer(string configPath, ILogger logger) => Load(configPath, logger).Server;

    internal static ResolvedModelConfig? FindModel(IReadOnlyList<ResolvedModelConfig> models, string? modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
        {
            return null;
        }

        var normalizedModelName = modelName.Trim();
        var candidateNames = GetCandidateModelNames(normalizedModelName);
        var exactOllamaMatch = models.FirstOrDefault(model =>
            candidateNames.Any(candidate => string.Equals(model.OllamaModelName, candidate, StringComparison.OrdinalIgnoreCase)));

        if (exactOllamaMatch is not null)
        {
            return exactOllamaMatch;
        }

        return models
            .Where(model =>
                candidateNames.Any(candidate =>
                    string.Equals(model.ModelId, candidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(model.AnthropicModel, candidate, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(model.DisplayName, candidate, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(model => string.Equals(model.OllamaModelName, model.ModelId, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .FirstOrDefault();
    }

    private static IReadOnlyList<string> GetCandidateModelNames(string modelName)
    {
        var candidates = new List<string> { modelName };
        var separatorIndex = modelName.IndexOf('/');
        if (separatorIndex > 0 && separatorIndex < modelName.Length - 1)
        {
            candidates.Add(modelName[(separatorIndex + 1)..]);
        }

        return candidates;
    }

    private static (ResolvedServerConfig Server, IReadOnlyList<ResolvedModelConfig> Models) Load(string configPath, ILogger logger)
    {
        if (!File.Exists(configPath))
        {
            logger.LogWarning("Config file not found: {ConfigPath}", configPath);
            return (new ResolvedServerConfig(), []);
        }

        using var stream = File.OpenRead(configPath);
        var config = JsonSerializer.Deserialize<OllamaHubConfig>(stream, SerializerOptions);

        if (config is null)
        {
            logger.LogWarning("Config file is empty or invalid: {ConfigPath}", configPath);
            return (new ResolvedServerConfig(), []);
        }

        var server = new ResolvedServerConfig
        {
            Urls = ResolveServerUrls(config)
        };

        var providers = config.Providers.ToDictionary(provider => provider.Id, StringComparer.OrdinalIgnoreCase);
        var models = new List<ResolvedModelConfig>();

        foreach (var model in config.Models)
        {
            var providerId = GetProviderId(model);
            providers.TryGetValue(providerId, out var provider);

            var apiMode = model.ApiMode ?? provider?.ApiMode ?? "openai";
            if (!string.Equals(apiMode, "anthropic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var baseUrl = model.BaseUrl ?? provider?.BaseUrl ?? config.BaseUrl;
            var apiKey = model.ApiKey ?? provider?.ApiKey;
            if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
            {
                logger.LogWarning("Skipping model {ModelId} because baseUrl or apiKey is missing.", model.Id);
                continue;
            }

            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (provider is not null)
            {
                foreach (var pair in provider.Headers)
                {
                    headers[pair.Key] = pair.Value;
                }
            }

            foreach (var pair in model.Headers)
            {
                headers[pair.Key] = pair.Value;
            }

            models.Add(new ResolvedModelConfig
            {
                ModelId = model.Id,
                AnthropicModel = model.Id,
                ProviderId = providerId,
                ApiMode = "anthropic",
                BaseUrl = baseUrl.TrimEnd('/'),
                ApiKey = apiKey,
                DisplayName = model.DisplayName ?? model.Id,
                OllamaModelName = BuildOllamaModelName(model),
                Family = model.Family ?? "claude",
                ContextLength = model.ContextLength ?? 128000,
                MaxTokens = model.MaxTokens ?? 4096,
                Temperature = model.Temperature,
                TopP = model.TopP,
                Headers = headers,
                Extra = new Dictionary<string, JsonNode?>(model.Extra, StringComparer.OrdinalIgnoreCase)
            });
        }

        logger.LogInformation("Loaded {Count} anthropic model(s) from {ConfigPath}", models.Count, configPath);
        return (server, models);
    }

    public static string BuildDigest(ResolvedModelConfig model)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{model.ProviderId}:{model.ModelId}:{model.OllamaModelName}"));
        return Convert.ToHexStringLower(bytes);
    }

    private static string GetProviderId(ModelConfig model)
    {
        return model.OwnedBy ?? model.Provider ?? model.Provide ?? "default";
    }

    private static string BuildOllamaModelName(ModelConfig model)
    {
        return string.IsNullOrWhiteSpace(model.ConfigId)
            ? model.Id
            : $"{model.Id}::{model.ConfigId}";
    }

    private static IReadOnlyList<string> ResolveServerUrls(OllamaHubConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.Url))
        {
            return [config.Url];
        }

        if (!string.IsNullOrWhiteSpace(config.Host) && config.Port is > 0)
        {
            return [$"http://{config.Host}:{config.Port}"];
        }

        return [];
    }
}