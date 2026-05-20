using System.Net;
using OllamaHub.Configuration;
using OllamaHub.Contracts;
using OllamaHub.Logging;
using OllamaHub.Services;

var builder = WebApplication.CreateBuilder(args);
var logPath = Path.Combine(AppContext.BaseDirectory, "OllamaHub.log");

builder.Logging.AddProvider(new FileLoggerProvider(logPath));

var startupLogger = LoggerFactory.Create(logging => logging.AddSimpleConsole()).CreateLogger("Startup");
var serverConfig = OllamaHubConfigLoader.LoadServer(Path.Combine(AppContext.BaseDirectory, OllamaHubConfigLoader.DefaultConfigFileName), startupLogger);

if (serverConfig.Urls.Count > 0)
{
    builder.WebHost.UseUrls(serverConfig.Urls.ToArray());
}

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = null;
});

builder.Services.AddSingleton<IOllamaHubConfigProvider, OllamaHubConfigLoader>();
builder.Services.AddSingleton<IAnthropicRequestFactory, AnthropicRequestFactory>();
builder.Services.AddSingleton<IAnthropicResponseMapper, AnthropicResponseMapper>();
builder.Services.AddHttpClient<IAnthropicProxyClient, AnthropicProxyClient>();

var app = builder.Build();

app.MapGet("/", () => Results.Ok(new { name = "OllamaHub", status = "ok" }));
app.MapGet("/api/version", () => Results.Ok(new { version = "0.1.0" }));
app.MapGet("/api/ps", () => Results.Ok(new { models = Array.Empty<object>() }));

app.MapGet("/api/tags", (IOllamaHubConfigProvider configProvider) =>
    Results.Ok(new OllamaTagListResponse
    {
        Models = configProvider.GetModels().Select(ToDescriptor).ToArray()
    }));

app.MapPost("/api/show", (IOllamaHubConfigProvider configProvider, OllamaShowRequest request) =>
{
    var modelName = request.Name ?? request.Model;
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

    return Results.Ok(new OllamaShowResponse
    {
        Modelfile = $"FROM {model.AnthropicModel}",
        Parameters = $"family={model.Family}\ncontext_length={model.ContextLength}\nmax_tokens={model.MaxTokens}",
        Details = ToDescriptor(model).Details,
        ModelInfo = new Dictionary<string, object>
        {
            ["provider"] = model.ProviderId,
            ["anthropic_model"] = model.AnthropicModel,
            ["context_length"] = model.ContextLength,
            ["max_tokens"] = model.MaxTokens,
            ["display_name"] = model.DisplayName
        }
    });
});

app.MapPost("/api/chat", async (
    HttpContext httpContext,
    IOllamaHubConfigProvider configProvider,
    IAnthropicRequestFactory requestFactory,
    IAnthropicProxyClient proxyClient,
    IAnthropicResponseMapper responseMapper,
    OllamaChatRequest request,
    CancellationToken cancellationToken) =>
{
    var model = configProvider.FindModel(request.Model);
    if (model is null)
    {
        return Results.NotFound(new OllamaErrorResponse
        {
            Error = $"Model '{request.Model}' is not configured."
        });
    }

    var anthropicRequest = requestFactory.Create(model, request);

    if (!request.Stream)
    {
        var (statusCode, response, error) = await proxyClient.SendAsync(model, anthropicRequest, cancellationToken);
        if (response is null)
        {
            return ToError(statusCode, error);
        }

        return Results.Ok(response);
    }

    var streamResult = await proxyClient.SendStreamAsync(model, anthropicRequest, cancellationToken);
    if (streamResult.Stream is null)
    {
        return ToError(streamResult.StatusCode, streamResult.Error);
    }

    httpContext.Response.StatusCode = StatusCodes.Status200OK;
    httpContext.Response.ContentType = "application/x-ndjson";

    await using var anthropicStream = streamResult.Stream;
    await responseMapper.WriteStreamAsync(model, anthropicStream, httpContext.Response.Body, cancellationToken);
    return Results.Empty;
});

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

app.Lifetime.ApplicationStarted.Register(() =>
{
    var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    var configuredUrls = app.Services.GetRequiredService<IOllamaHubConfigProvider>().GetServerUrls();

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

static OllamaModelDescriptor ToDescriptor(ResolvedModelConfig model) =>
    new()
    {
        Name = model.OllamaModelName,
        Model = model.OllamaModelName,
        ModifiedAt = DateTimeOffset.UtcNow.ToString("O"),
        Size = 0,
        Digest = OllamaHubConfigLoader.BuildDigest(model),
        Details = new OllamaModelDetails
        {
            Family = model.Family,
            Families = [model.Family],
            ParameterSize = model.ContextLength.ToString(),
            QuantizationLevel = "proxy"
        }
    };

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
