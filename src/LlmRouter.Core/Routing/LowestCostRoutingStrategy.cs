using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class LowestCostRoutingStrategy : IRoutingStrategy
{
    public IReadOnlyList<ProviderRoute> Order(
        IReadOnlyList<ProviderRoute> routes,
        IReadOnlyDictionary<string, ProviderHealth> healthByProvider,
        ChatRequest request)
    {
        return routes
            .Select((route, index) => (
                Route: route,
                Index: index,
                BlendedCost: GetBlendedCostPerMillion(route.Pricing),
                P95Latency: GetEffectiveP95(healthByProvider[route.Provider.Name])))
            .OrderBy(item => item.BlendedCost)
            .ThenBy(item => item.P95Latency)
            .ThenBy(item => item.Index)
            .Select(item => item.Route)
            .ToList();
    }

    private static decimal GetBlendedCostPerMillion(ModelPricing? pricing)
    {
        if (pricing is null)
        {
            return decimal.MaxValue;
        }

        // Output tokens typically dominate LLM request cost; weight 1:3 input:output for pre-flight comparison.
        return pricing.InputPerMillionTokensUsd + (pricing.OutputPerMillionTokensUsd * 3m);
    }

    private static double GetEffectiveP95(ProviderHealth health)
    {
        return (health.LatencySampleCount > 0 || health.P95LatencyMs > 0)
            ? health.P95LatencyMs
            : double.MaxValue;
    }
}
