using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

/// <summary>
/// Applies model compatibility, circuit-breaker filtering, and request-selected provider ordering.
/// </summary>
public sealed class ProviderRouter : IProviderRouter
{
    private readonly IHealthTracker _healthTracker;
    private readonly IReadOnlyList<ILlmProvider> _providers;
    private readonly IReadOnlyDictionary<RoutingStrategy, IRoutingStrategy> _strategies;

    /// <summary>
    /// Creates a provider router over the providers registered in dependency-injection order.
    /// </summary>
    public ProviderRouter(IEnumerable<ILlmProvider> providers, IHealthTracker healthTracker)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(healthTracker);

        _providers = providers.ToList();
        _healthTracker = healthTracker;
        _strategies = new Dictionary<RoutingStrategy, IRoutingStrategy>
        {
            [RoutingStrategy.LeastLatency] = new LeastLatencyRoutingStrategy(_healthTracker),
            [RoutingStrategy.RoundRobin] = new RoundRobinRoutingStrategy(),
            [RoutingStrategy.PriorityWithFallback] = new PriorityWithFallbackRoutingStrategy()
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<ILlmProvider> Resolve(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ILlmProvider> modelCompatibleProviders = _providers
            .Where(provider => provider.SupportedModels.Contains(request.Model))
            .ToList();

        if (modelCompatibleProviders.Count == 0)
        {
            throw new InvalidOperationException($"No provider supports model {request.Model}.");
        }

        List<ILlmProvider> closedCircuitCandidates = modelCompatibleProviders
            .Where(provider => _healthTracker.GetHealth(provider.Name).CircuitState != CircuitState.Open)
            .ToList();

        if (closedCircuitCandidates.Count == 0)
        {
            return modelCompatibleProviders;
        }

        IRoutingStrategy strategy = _strategies[request.Strategy];
        return strategy.Order(closedCircuitCandidates, request);
    }
}
