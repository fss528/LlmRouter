using System.Net.ServerSentEvents;
using System.Runtime.CompilerServices;
using LlmRouter.Api.Configuration;
using LlmRouter.Api.Dtos;
using LlmRouter.Api.Middleware;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Services;

namespace LlmRouter.Api.Endpoints;

/// <summary>
/// Chat and model discovery endpoint mapping and handlers.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>
    /// Maps versioned chat, streaming, and model catalog endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder v1 = endpoints
            .MapGroup(ApiRoutes.V1Prefix)
            .WithTags("Chat")
            .RequireRateLimiting("GatewayRateLimit");

        v1.MapPost(ApiRoutes.Chat, CompleteChatAsync)
            .WithName("CompleteChat")
            .Produces<ChatResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status401Unauthorized)
            .ProducesProblem(StatusCodes.Status429TooManyRequests)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        v1.MapPost(ApiRoutes.ChatStream, StreamChatAsync)
            .WithName("StreamChat")
            .Produces(StatusCodes.Status200OK, contentType: "text/event-stream")
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        v1.MapGet(ApiRoutes.Models, GetModels)
            .WithName("GetModels")
            .WithTags("Catalog")
            .Produces<ModelsCatalogResponse>(StatusCodes.Status200OK);

        return endpoints;
    }

    private static async Task<IResult> CompleteChatAsync(ChatRequestDto requestDto, ChatService chatService, CancellationToken ct)
    {
        IDictionary<string, string[]> validationErrors = requestDto.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        try
        {
            ChatRequest request = requestDto.ToDomain();
            ChatResponse response = await chatService.CompleteAsync(request, ct).ConfigureAwait(false);
            return Results.Ok(response);
        }
        catch (Exception ex) when (ex is UnsupportedModelException or ProviderClientException or AllProvidersFailedException)
        {
            return ApiExceptionHandlingExtensions.MapDomainFailure(ex);
        }
    }

    private static async Task<IResult> StreamChatAsync(ChatRequestDto requestDto, ChatService chatService, CancellationToken ct)
    {
        IDictionary<string, string[]> validationErrors = requestDto.Validate();
        if (validationErrors.Count > 0)
        {
            return Results.ValidationProblem(validationErrors);
        }

        ChatRequest request = requestDto.ToDomain();
        IAsyncEnumerator<ChatStreamChunk> enumerator = chatService.CompleteStreamAsync(request, ct).GetAsyncEnumerator(ct);

        try
        {
            // Advance before sending HTTP 200 SSE headers so model/provider validation or total outage returns proper 4xx/503.
            bool hasFirstChunk = await enumerator.MoveNextAsync().ConfigureAwait(false);
            return Results.ServerSentEvents(StreamChunksAsync(enumerator, hasFirstChunk, ct));
        }
        catch (Exception ex) when (ex is UnsupportedModelException or ProviderClientException or AllProvidersFailedException)
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            return ApiExceptionHandlingExtensions.MapDomainFailure(ex);
        }
        catch
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static async IAsyncEnumerable<SseItem<ChatStreamChunk>> StreamChunksAsync(
        IAsyncEnumerator<ChatStreamChunk> enumerator,
        bool hasFirstChunk,
        [EnumeratorCancellation] CancellationToken ct)
    {
        try
        {
            if (hasFirstChunk)
            {
                ChatStreamChunk first = enumerator.Current;
                yield return new SseItem<ChatStreamChunk>(first, first.IsFinal ? "done" : "chunk");

                while (!ct.IsCancellationRequested && await enumerator.MoveNextAsync().ConfigureAwait(false))
                {
                    ChatStreamChunk current = enumerator.Current;
                    yield return new SseItem<ChatStreamChunk>(current, current.IsFinal ? "done" : "chunk");
                }
            }
        }
        finally
        {
            await enumerator.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static IResult GetModels(IEnumerable<ILlmProvider> providers, IModelCatalog modelCatalog)
    {
        List<ProviderModelsInfo> providerInfos = providers
            .Select(provider => new ProviderModelsInfo(
                provider.Name,
                provider.SupportedModels
                    .OrderBy(model => model, StringComparer.Ordinal)
                    .Select(model => new ModelPricingEntry(model, modelCatalog.GetPricing(provider.Name, model)))
                    .ToList()))
            .ToList();

        return Results.Ok(new ModelsCatalogResponse(modelCatalog.GetAliases(), providerInfos));
    }
}
