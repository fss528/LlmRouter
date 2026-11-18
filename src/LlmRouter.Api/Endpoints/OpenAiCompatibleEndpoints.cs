using System.Text.Json;
using LlmRouter.Api.Configuration;
using LlmRouter.Api.Dtos;
using LlmRouter.Api.Middleware;
using LlmRouter.Core.Models;
using LlmRouter.Core.Services;

namespace LlmRouter.Api.Endpoints;

/// <summary>
/// OpenAI-compatible POST /v1/chat/completions endpoint allowing standard OpenAI SDKs and tools
/// to route through LlmRouter with zero client code changes beyond BaseUrl.
/// </summary>
public static class OpenAiCompatibleEndpoints
{
    private const string RoutingStrategyHeader = "X-Routing-Strategy";
    private const string ServedProviderHeader = "X-LlmRouter-Provider";
    private const string LatencyHeader = "X-LlmRouter-Latency-Ms";
    private const string EstimatedCostHeader = "X-LlmRouter-Cost-Usd";

    /// <summary>Maps OpenAI-compatible endpoints under /v1.</summary>
    public static IEndpointRouteBuilder MapOpenAiCompatibleEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder v1 = endpoints
            .MapGroup(ApiRoutes.V1Prefix)
            .WithTags("OpenAI Compatible")
            .RequireRateLimiting("GatewayRateLimit");

        v1.MapPost(ApiRoutes.OpenAiChatCompletions, CompleteOpenAiChatAsync)
            .WithName("OpenAiChatCompletions")
            .Produces(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        return endpoints;
    }

    private static async Task<IResult> CompleteOpenAiChatAsync(
        OpenAiChatRequestDto requestDto,
        HttpContext httpContext,
        ChatService chatService,
        CancellationToken ct)
    {
        IDictionary<string, string[]> validationErrors = requestDto.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        RoutingStrategy strategy = ParseStrategyHeader(httpContext.Request);
        ChatRequest domainRequest = requestDto.ToDomain(strategy);

        if (!requestDto.Stream)
        {
            try
            {
                ChatResponse response = await chatService.CompleteAsync(domainRequest, ct).ConfigureAwait(false);
                httpContext.Response.Headers[ServedProviderHeader] = response.Provider;
                httpContext.Response.Headers[LatencyHeader] = response.LatencyMs.ToString(System.Globalization.CultureInfo.InvariantCulture);
                if (response.EstimatedCostUsd is decimal cost)
                {
                    httpContext.Response.Headers[EstimatedCostHeader] = cost.ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                return Results.Ok(ToOpenAiCompletionResponse(response));
            }
            catch (Exception ex) when (ex is UnsupportedModelException or ProviderClientException or AllProvidersFailedException)
            {
                return ApiExceptionHandlingExtensions.MapDomainFailure(ex);
            }
        }

        IAsyncEnumerator<ChatStreamChunk> enumerator = chatService.CompleteStreamAsync(domainRequest, ct).GetAsyncEnumerator(ct);
        try
        {
            bool hasFirst = await enumerator.MoveNextAsync().ConfigureAwait(false);
            httpContext.Response.ContentType = "text/event-stream; charset=utf-8";
            httpContext.Response.Headers.CacheControl = "no-cache";

            long created = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (hasFirst)
            {
                httpContext.Response.Headers[ServedProviderHeader] = enumerator.Current.Provider;
                await WriteOpenAiChunkAsync(httpContext.Response, enumerator.Current, created, ct).ConfigureAwait(false);

                while (!ct.IsCancellationRequested && await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    await WriteOpenAiChunkAsync(httpContext.Response, enumerator.Current, created, ct).ConfigureAwait(false);
                }
            }

            await httpContext.Response.WriteAsync("data: [DONE]\n\n", ct).ConfigureAwait(false);
            await httpContext.Response.Body.FlushAsync(ct).ConfigureAwait(false);
            return Results.Empty;
        }
        catch (Exception ex) when (ex is UnsupportedModelException or ProviderClientException or AllProvidersFailedException)
        {
            return ApiExceptionHandlingExtensions.MapDomainFailure(ex);
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static RoutingStrategy ParseStrategyHeader(HttpRequest request)
    {
        string? raw = request.Headers[RoutingStrategyHeader].FirstOrDefault();
        return Enum.TryParse<RoutingStrategy>(raw, ignoreCase: true, out RoutingStrategy parsed)
            ? parsed
            : RoutingStrategy.LeastLatency;
    }

    private static object ToOpenAiCompletionResponse(ChatResponse response)
    {
        return new
        {
            id = $"chatcmpl-{response.Id}",
            @object = "chat.completion",
            created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            model = response.Model,
            provider = response.Provider,
            estimated_cost_usd = response.EstimatedCostUsd,
            choices = new[]
            {
                new
                {
                    index = 0,
                    message = new
                    {
                        role = "assistant",
                        content = response.Content
                    },
                    finish_reason = "stop"
                }
            },
            usage = new
            {
                prompt_tokens = response.Usage.PromptTokens,
                completion_tokens = response.Usage.CompletionTokens,
                total_tokens = response.Usage.TotalTokens
            }
        };
    }

    private static async Task WriteOpenAiChunkAsync(HttpResponse response, ChatStreamChunk chunk, long created, CancellationToken ct)
    {
        object payload = chunk.IsFinal
            ? new
            {
                id = $"chatcmpl-{chunk.Id}",
                @object = "chat.completion.chunk",
                created,
                model = chunk.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { },
                        finish_reason = "stop"
                    }
                },
                usage = chunk.Usage is null
                    ? null
                    : new
                    {
                        prompt_tokens = chunk.Usage.PromptTokens,
                        completion_tokens = chunk.Usage.CompletionTokens,
                        total_tokens = chunk.Usage.TotalTokens
                    }
            }
            : new
            {
                id = $"chatcmpl-{chunk.Id}",
                @object = "chat.completion.chunk",
                created,
                model = chunk.Model,
                choices = new[]
                {
                    new
                    {
                        index = 0,
                        delta = new { content = chunk.Delta },
                        finish_reason = (string?)null
                    }
                }
            };

        string json = JsonSerializer.Serialize(payload);
        await response.WriteAsync($"data: {json}\n\n", ct).ConfigureAwait(false);
        await response.Body.FlushAsync(ct).ConfigureAwait(false);
    }
}
