using LlmRouter.Core.Models;

namespace LlmRouter.Core.Abstractions;

/// <summary>
/// Resolves logical model aliases, cross-provider fallback chains, and token pricing metadata.
/// </summary>
public interface IModelCatalog
{
    /// <summary>
    /// Returns ordered provider/model targets configured for <paramref name="requestedModel"/>,
    /// or an empty list if <paramref name="requestedModel"/> should match concrete provider models directly.
    /// </summary>
    IReadOnlyList<ModelTarget> GetTargets(string requestedModel);

    /// <summary>
    /// Returns token pricing for a provider/model pair when known.
    /// </summary>
    ModelPricing? GetPricing(string providerName, string model);

    /// <summary>
    /// Estimates total USD cost for a request given observed token usage.
    /// </summary>
    decimal? EstimateCost(string providerName, string model, TokenUsage usage);

    /// <summary>
    /// Lists configured model aliases and their fallback target chains.
    /// </summary>
    IReadOnlyList<ModelAliasInfo> GetAliases();
}
