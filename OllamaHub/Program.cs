using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Interop;
using OllamaHub.Logging;
using OllamaHub.Services;

// 优先处理命令行命令；如果命令已处理，则不再启动 Web 服务。
var configPath = Path.Combine(AppContext.BaseDirectory, OllamaHubConfigLoader.DefaultConfigFileName);
if (TryHandleCommand(args, configPath))
{
    Environment.Exit(0);
    return;
}

var builder = WebApplication.CreateBuilder(args);
var logPath = Path.Combine(AppContext.BaseDirectory, "OllamaHub.log");

// 读取应用配置，并根据配置决定日志级别和是否启用控制台输出。
var appConfig = OllamaHubConfigLoader.LoadConfig(configPath, NullLogger.Instance);
var minLogLevel = appConfig.Logging.GetLogLevel();
var enableConsoleLogging = WindowsConsoleManager.ShouldEnableConsole(minLogLevel);

if (enableConsoleLogging)
{
    WindowsConsoleManager.EnsureConsole();
}

using var startupLoggerFactory = LoggerFactory.Create(logging =>
{
    logging.ClearProviders();
    logging.AddProvider(new FileLoggerProvider(logPath, minLogLevel));

    if (enableConsoleLogging)
    {
        logging.AddSimpleConsole();
    }
});

var startupLogger = startupLoggerFactory.CreateLogger("Startup");
startupLogger.LogInformation("Loaded configuration from {ConfigPath}", configPath);

// 替换默认日志提供器，统一写入文件，并在需要时输出到控制台。
builder.Logging.ClearProviders();
builder.Logging.AddProvider(new FileLoggerProvider(logPath, minLogLevel));

if (enableConsoleLogging)
{
    builder.Logging.AddSimpleConsole();
}

if (appConfig.Server.Urls.Count > 0)
{
    builder.WebHost.UseUrls(appConfig.Server.Urls.ToArray());
}

// 保持 JSON 属性名不被自动转换，避免影响兼容 OpenAI/Ollama 的响应字段。
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

// 注册配置、请求转换、响应映射和上游代理相关服务。
builder.Services.AddSingleton<IOllamaHubConfigProvider, OllamaHubConfigLoader>();
builder.Services.AddSingleton<IAnthropicRequestFactory, AnthropicRequestFactory>();
builder.Services.AddSingleton<IAnthropicResponseMapper, AnthropicResponseMapper>();
builder.Services.AddHttpClient<IAnthropicProxyClient, AnthropicProxyClient>();
builder.Services.AddHttpClient<IProtocolPassthroughClient, ProtocolPassthroughClient>();

var app = builder.Build();

// 基础健康检查和 Ollama 兼容接口。
app.MapGet("/", () => Results.Ok(new { name = "OllamaHub", status = "ok" }));
app.MapGet("/api/version", () => Results.Ok(new { version = "0.12.6" }));
app.MapGet("/api/ps", () => Results.Ok(new { models = Array.Empty<object>() }));

// 返回 Ollama 和 OpenAI 兼容格式的模型列表。
app.MapGet("/api/tags", (IOllamaHubConfigProvider configProvider) =>
    Results.Ok(new OllamaTagListResponse
    {
        Models = configProvider.GetModels().Select(ToDescriptor).ToArray()
    }));
app.MapGet("/v1/models", GetOpenAiModels);
app.MapGet("/models", GetOpenAiModels);

// 查询指定模型的 Ollama 兼容详情。
app.MapPost("/api/show", (IOllamaHubConfigProvider configProvider, OllamaShowRequest request) =>
{
    var modelName = request.Model;
    if (string.IsNullOrWhiteSpace(modelName))
    {
        return Results.BadRequest(new OllamaErrorResponse
        {
            Error = "Model name is required."
        });
    }

    var model = configProvider.FindModel(modelName);
    if (model is null)
    {
        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{modelName}' is not configured."
        });
    }

    var capabilities = model.Vision
        ? new[] { "completion", "tools", "vision" }
        : new[] { "completion", "tools" };

    return Results.Ok(new OllamaShowResponse
    {
        Modelfile = $"FROM {model.AnthropicModel}",
        Parameters = $"family={model.Family}\ncontext_length={model.ContextLength}\nmax_tokens={model.MaxTokens}",
        Details = ToDescriptor(model).Details,
        Capabilities = capabilities,
        ModelInfo = new Dictionary<string, object>
        {
            ["provider"] = model.ProviderId,
            ["anthropic_model"] = model.AnthropicModel,
            ["context_length"] = model.ContextLength,
            ["max_tokens"] = model.MaxTokens,
            ["capabilities"] = capabilities,
            ["vision"] = model.Vision,
        }
    });
});


// 兼容多个 OpenAI Chat Completions 路径，统一交给同一个处理函数。
app.MapPost("/v1/chat/completions", HandleChatCompletionsAsync);
app.MapPost("/openai/v1/chat/completions", HandleChatCompletionsAsync);
app.MapPost("/chat/completions", HandleChatCompletionsAsync);

// 对未识别路由返回统一错误；未识别的 POST 请求额外记录错误日志。
app.MapFallback((HttpContext httpContext, ILogger<Program> logger) =>
{
    if (HttpMethods.IsPost(httpContext.Request.Method))
    {
        logger.LogError("Unrecognized POST route: {Path}", httpContext.Request.Path.Value);
    }

    return Results.NotFound(new OllamaErrorResponse
    {
        Error = $"Route '{httpContext.Request.Path.Value}' is not recognized."
    });
});

// 应用启动后输出最终监听地址，便于排查绑定配置。
app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var configuredUrls = app.Services.GetRequiredService<IOllamaHubConfigProvider>().GetConfig().Server.Urls;

    if (configuredUrls.Count > 0)
    {
        logger.LogInformation("OllamaHub listening on configured URLs: {Urls}", string.Join(", ", configuredUrls));
    }
    else
    {
        logger.LogInformation("OllamaHub listening on default ASP.NET Core URLs.");
    }
});

app.Run();

// 将内部模型配置转换为 Ollama /api/tags 使用的模型描述。
static OllamaModelDescriptor ToDescriptor(ResolvedModelConfig model) =>
    new()
    {
        Name = model.OllamaModelName,
        Model = model.ModelId,
        ModifiedAt = DateTimeOffset.UtcNow.ToString("O"),
        Size = 0,
        Digest = OllamaHubConfigLoader.BuildDigest(model),
        Details = new OllamaModelDetails
        {
            Family = "",
            Families = [""],
            ParameterSize = "",
            QuantizationLevel = "proxy"
        }
    };

// 返回 OpenAI /v1/models 兼容的模型列表。
static IResult GetOpenAiModels(IOllamaHubConfigProvider configProvider) =>
    Results.Ok(new
    {
        @object = "list",
        data = configProvider.GetModels().Select(model => new
        {
            id = model.OllamaModelName,
            @object = "model",
            created = 0,
            owned_by = model.ProviderId
        }).ToArray()
    });

// 将上游错误状态码转换为当前 API 的统一错误响应。
static IResult ToError(HttpStatusCode statusCode, string? error)
{
    var payload = new OllamaErrorResponse
    {
        Error = error ?? "Upstream request failed."
    };

    return (int)statusCode switch
    {
        StatusCodes.Status400BadRequest => Results.BadRequest(payload),
        StatusCodes.Status401Unauthorized => Results.Json(payload, statusCode: StatusCodes.Status401Unauthorized),
        StatusCodes.Status403Forbidden => Results.Json(payload, statusCode: StatusCodes.Status403Forbidden),
        StatusCodes.Status404NotFound => Results.NotFound(payload),
        StatusCodes.Status429TooManyRequests => Results.Json(payload, statusCode: StatusCodes.Status429TooManyRequests),
        _ => Results.Json(payload, statusCode: StatusCodes.Status502BadGateway)
    };
}

// 从 JSON 对象中读取非空字符串属性。
static bool TryGetString(JsonObject jsonObject, string propertyName, out string value)
{
    value = string.Empty;
    if (jsonObject[propertyName] is not JsonValue jsonValue
        || !jsonValue.TryGetValue<string>(out var stringValue)
        || string.IsNullOrWhiteSpace(stringValue))
    {
        return false;
    }

    value = stringValue;
    return true;
}

// 处理命令行模式，例如安全写入 API Key；返回 true 表示命令已处理。
static bool TryHandleCommand(string[] args, string configPath)
{
    if (args.Length == 0)
    {
        return false;
    }

    if (!string.Equals(args[0], "SetApiKey", StringComparison.OrdinalIgnoreCase))
    {
        return false;
    }

    WindowsConsoleManager.EnsureConsole();

    if (args.Length < 3)
    {
        Console.Error.WriteLine("Usage: OllamaHub SetApiKey <providerOrModelId> <apiKey>");
        return true;
    }

    try
    {
        var target = args[1];
        var apiKey = args[2];
        SetProtectedApiKey(configPath, target, apiKey);
        Console.WriteLine($"API Key for '{target}' has been stored securely.");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex.Message);
    }

    return true;
}

static void SetProtectedApiKey(string configPath, string target, string apiKey)
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("SetApiKey is only supported on Windows.");
    }

    if (string.IsNullOrWhiteSpace(target))
    {
        throw new ArgumentException("Target provider or model id is required.", nameof(target));
    }

    if (string.IsNullOrWhiteSpace(apiKey))
    {
        throw new ArgumentException("API key is required.", nameof(apiKey));
    }

    if (!File.Exists(configPath))
    {
        throw new FileNotFoundException("Config file not found.", configPath);
    }

    var protectedApiKey = ProtectedApiKeyStore.Protect(apiKey);
    OllamaHubConfigLoader.SetProtectedApiKey(configPath, target, protectedApiKey);
}

// 处理 OpenAI Chat Completions 请求，并根据模型配置选择透传 OpenAI 或转换为 Anthropic 请求。
async Task<IResult> HandleChatCompletionsAsync(
    HttpContext httpContext,
    IOllamaHubConfigProvider configProvider,
    IAnthropicRequestFactory requestFactory,
    IAnthropicProxyClient proxyClient,
    IAnthropicResponseMapper responseMapper,
    IProtocolPassthroughClient passthroughClient,
    ILogger<Program> logger,
    JsonNode? requestJson,
    CancellationToken cancellationToken)
{
    if (requestJson is not JsonObject requestObject)
    {
        return Results.BadRequest(new OllamaErrorResponse
        {
            Error = "Request body must be a JSON object."
        });
    }

    if (!TryGetString(requestObject, "model", out var modelName))
    {
        return Results.BadRequest(new OllamaErrorResponse
        {
            Error = "Model name is required."
        });
    }

    var model = configProvider.FindModel(modelName);
    if (model is null)
    {
        logger.LogWarning(
            "OpenAI chat completion model not configured. Requested model: {RequestedModel}. Available models: {AvailableModels}",
            modelName,
            string.Join(", ", configProvider.GetModels().Select(m => m.OllamaModelName)));

        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{modelName}' is not configured."
        });
    }

    // 支持 OpenAI 协议的模型直接透传，同时合并模型级默认参数。
    if (model.SupportsApiMode("openai"))
    {
        requestObject["model"] = model.ModelId;

        if (model.Extra != null && model.Extra.Count > 0)
        {
            foreach (var kvp in model.Extra)
            {
                requestObject[kvp.Key] = kvp.Value?.DeepClone();
            }
        }

        if (model.Temperature.HasValue && !requestObject.ContainsKey("temperature"))
            requestObject["temperature"] = model.Temperature.Value;

        if (model.TopP.HasValue && !requestObject.ContainsKey("top_p"))
            requestObject["top_p"] = model.TopP.Value;

        await passthroughClient.ProxyAsync(httpContext, model, "openai", "/v1/chat/completions", requestObject, cancellationToken);
        return Results.Empty;
    }

    // 非 OpenAI 协议模型会转换为 Anthropic 请求，并在返回时映射回 OpenAI 格式。
    var anthropicRequest = requestFactory.Create(model, requestObject);

    if (!anthropicRequest.Stream)
    {
        var (statusCode, response, error) = await proxyClient.SendAsync(model, anthropicRequest, cancellationToken);
        if (response is null)
        {
            return ToError(statusCode, error);
        }

        return Results.Ok(responseMapper.MapOpenAiResponse(model, response));
    }

    // 流式响应以 SSE 形式返回给客户端。
    var streamResult = await proxyClient.SendStreamAsync(model, anthropicRequest, cancellationToken);
    if (streamResult.Stream is null)
    {
        return ToError(streamResult.StatusCode, streamResult.Error);
    }

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "text/event-stream";
    httpContext.Response.Headers.CacheControl = "no-cache";

    await using var anthropicStream = streamResult.Stream;
    await responseMapper.WriteOpenAiStreamAsync(model, anthropicStream, httpContext.Response.Body, cancellationToken);
    return Results.Empty;
}