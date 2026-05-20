using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using OllamaHub.Configuration;

namespace OllamaHub.Services;

public interface IProtocolPassthroughClient
{
    Task ProxyAsync<TRequest>(HttpContext httpContext, ResolvedModelConfig model, string apiMode, string upstreamPath, TRequest payload, CancellationToken cancellationToken);
}

public sealed class ProtocolPassthroughClient(HttpClient httpClient) : IProtocolPassthroughClient
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task ProxyAsync<TRequest>(HttpContext httpContext, ResolvedModelConfig model, string apiMode, string upstreamPath, TRequest payload, CancellationToken cancellationToken)
    {
        using var upstreamRequest = BuildRequestMessage(httpContext, model, apiMode, upstreamPath, payload);
        using var upstreamResponse = await httpClient.SendAsync(upstreamRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        httpContext.Response.StatusCode = (int)upstreamResponse.StatusCode;
        CopyResponseHeaders(upstreamResponse, httpContext.Response);

        await using var responseStream = await upstreamResponse.Content.ReadAsStreamAsync(cancellationToken);
        await responseStream.CopyToAsync(httpContext.Response.Body, cancellationToken);
    }

    private static HttpRequestMessage BuildRequestMessage<TRequest>(HttpContext httpContext, ResolvedModelConfig model, string apiMode, string upstreamPath, TRequest payload)
    {
        var upstreamUri = $"{model.BaseUrl.TrimEnd('/')}{upstreamPath}{httpContext.Request.QueryString}";
        var upstreamRequest = new HttpRequestMessage(new HttpMethod(httpContext.Request.Method), upstreamUri);

        if (payload is not null)
        {
            var requestBody = JsonSerializer.Serialize(payload, JsonOptions);
            upstreamRequest.Content = new StringContent(requestBody, Encoding.UTF8, httpContext.Request.ContentType ?? "application/json");
        }

        CopyRequestHeaders(httpContext.Request, upstreamRequest);
        ApplyDefaultProtocolHeaders(upstreamRequest, model, apiMode);
        ApplyConfiguredHeaders(upstreamRequest, model.Headers);
        return upstreamRequest;
    }

    private static void CopyRequestHeaders(HttpRequest request, HttpRequestMessage upstreamRequest)
    {
        foreach (var header in request.Headers)
        {
            if (string.Equals(header.Key, "Host", StringComparison.OrdinalIgnoreCase)
                || string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray()) && upstreamRequest.Content is not null)
            {
                upstreamRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
            }
        }
    }

    private static void ApplyDefaultProtocolHeaders(HttpRequestMessage upstreamRequest, ResolvedModelConfig model, string apiMode)
    {
        if (string.Equals(apiMode, "openai", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(model.ApiKey))
        {
            upstreamRequest.Headers.Authorization = null;
            upstreamRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", model.ApiKey);
            return;
        }

        if (string.Equals(apiMode, "anthropic", StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrWhiteSpace(model.ApiKey))
        {
            AddOrReplaceHeader(upstreamRequest.Headers, "x-api-key", model.ApiKey);
            AddOrReplaceHeader(upstreamRequest.Headers, "anthropic-version", "2023-06-01");
        }
    }

    private static void ApplyConfiguredHeaders(HttpRequestMessage upstreamRequest, IReadOnlyDictionary<string, string> headers)
    {
        foreach (var header in headers)
        {
            if (!upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value) && upstreamRequest.Content is not null)
            {
                upstreamRequest.Content.Headers.Remove(header.Key);
                upstreamRequest.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                continue;
            }

            if (upstreamRequest.Headers.Contains(header.Key))
            {
                upstreamRequest.Headers.Remove(header.Key);
                upstreamRequest.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }
    }

    private static void AddOrReplaceHeader(HttpRequestHeaders headers, string name, string value)
    {
        headers.Remove(name);
        headers.TryAddWithoutValidation(name, value);
    }

    private static void CopyResponseHeaders(HttpResponseMessage upstreamResponse, HttpResponse downstreamResponse)
    {
        foreach (var header in upstreamResponse.Headers)
        {
            if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            downstreamResponse.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstreamResponse.Content.Headers)
        {
            if (string.Equals(header.Key, "Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            downstreamResponse.Headers[header.Key] = header.Value.ToArray();
        }

        downstreamResponse.Headers.Remove("transfer-encoding");
    }
}
