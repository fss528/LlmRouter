using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class LeastLatencyRoutingStrategy : IRoutingStrategy
{
    public IReadOnlyList<ProviderRoute> Order(
        IReadOnlyList<ProviderRoute> routes,
        IReadOnlyDictionary<string, ProviderHealth> healthByProvider,
        ChatRequest request)
    {
        return routes
            .Select((route, index) => (Route: route, Index: index, Key: GetSortKey(route, healthByProvider)))
            .OrderBy(item => item.Key.UnmeasuredRank)
            .ThenBy(item => item.Key.P95LatencyMs)
            .ThenBy(item => item.Index)
            .Select(item => item.Route)
            .ToList();
    }

    private static (int UnmeasuredRank, double P95LatencyMs) GetSortKey(
        ProviderRoute route,
        IReadOnlyDictionary<string, ProviderHealth> healthByProvider)
    {
        ProviderHealth health = healthByProvider[route.Provider.Name];
        bool hasMeasuredLatency = health.LatencySampleCount > 0 || health.P95LatencyMs > 0;
        return hasMeasuredLatency ? (0, health.P95LatencyMs) : (1, double.MaxValue);
    }
}
