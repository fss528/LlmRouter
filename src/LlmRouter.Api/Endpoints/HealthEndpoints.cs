using LlmRouter.Api.Configuration;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Api.Endpoints;

/// <summary>
/// Health endpoint mapping and handlers.
/// </summary>
public static class HealthEndpoints
{
    /// <summary>
    /// Maps provider and platform health endpoints.
    /// </summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet(ApiRoutes.ProviderHealth, GetProviderHealth)
            .WithName("GetProviderHealth")
            .WithTags("Health")
            .Produces<IReadOnlyList<ProviderHealth>>(StatusCodes.Status200OK);

        endpoints.MapHealthChecks(ApiRoutes.Health);

        return endpoints;
    }

    private static IResult GetProviderHealth(IHealthTracker healthTracker)
    {
        return Results.Ok(healthTracker.GetAllHealth());
    }
}
