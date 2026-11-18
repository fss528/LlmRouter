using LlmRouter.Core.Models;

namespace LlmRouter.Core.Abstractions;

/// <summary>
/// Represents a concrete provider and resolved target model selected by the router.
/// </summary>
/// <param name="Provider">Provider adapter instance.</param>
/// <param name="Model">Concrete model identifier to send to <paramref name="Provider"/>.</param>
/// <param name="Pricing">Optional pricing metadata for cost-aware routing and accounting.</param>
/// <param name="IsHalfOpenProbe">Indicates whether this candidate claimed a single-flight half-open probe lease.</param>
public sealed record ProviderRoute(
    ILlmProvider Provider,
    string Model,
    ModelPricing? Pricing = null,
    bool IsHalfOpenProbe = false);

/// <summary>
/// Resolves an ordered provider route list for a request. The caller owns fallback execution.
/// </summary>
public interface IProviderRouter
{
    /// <summary>
    /// Returns an ordered list of routes where the first candidate should be attempted first.
    /// </summary>
    IReadOnlyList<ProviderRoute> Resolve(ChatRequest request);
}
