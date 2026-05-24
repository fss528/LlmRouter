using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class LeastLatencyRoutingStrategy : IRoutingStrategy
{
    private readonly IHealthTracker _healthTracker;

    public LeastLatencyRoutingStrategy(IHealthTracker healthTracker)
    {
        _healthTracker = healthTracker;
    }

    public IReadOnlyList<ILlmProvider> Order(IReadOnlyList<ILlmProvider> providers, ChatRequest request)
    {
        return providers
            .OrderBy(provider => _healthTracker.GetHealth(provider.Name).P95LatencyMs)
            .ThenBy(provider => provider.Name, StringComparer.Ordinal)
            .ToList();
    }
}
