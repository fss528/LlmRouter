using LlmRouter.Api.Configuration;
using LlmRouter.Api.Dtos;
using LlmRouter.Core.Models;
using LlmRouter.Core.Services;

namespace LlmRouter.Api.Endpoints;

/// <summary>
/// Chat endpoint mapping and handlers.
/// </summary>
public static class ChatEndpoints
{
    /// <summary>
    /// Maps versioned chat endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapChatEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder v1 = endpoints.MapGroup(ApiRoutes.V1Prefix).WithTags("Chat");

        v1.MapPost(ApiRoutes.Chat, CompleteChatAsync)
            .WithName("CompleteChat")
            .Produces<ChatResponse>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .Produces(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

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
        catch (InvalidOperationException ex) when (ex.Message.StartsWith("No provider supports model", StringComparison.Ordinal))
        {
            return Results.BadRequest(new { error = ex.Message });
        }
        catch (AllProvidersFailedException ex)
        {
            return Results.Problem(
                title: "All providers failed",
                detail: ex.InnerException?.Message ?? ex.Message,
                statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
