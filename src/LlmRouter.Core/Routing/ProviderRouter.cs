using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Services;

namespace LlmRouter.Core.Routing;

/// <summary>
/// Applies logical model resolution, health/circuit filtering, single-flight half-open probing,
/// and request-selected routing strategies.
/// </summary>
public sealed class ProviderRouter : IProviderRouter
{
    private readonly IHealthTracker _healthTracker;
    private readonly IModelCatalog _modelCatalog;
    private readonly IReadOnlyList<ILlmProvider> _providers;
    private readonly IReadOnlyDictionary<RoutingStrategy, IRoutingStrategy> _strategies;

    /// <summary>
    /// Creates a provider router over registered providers and shared health/routing state.
    /// </summary>
    public ProviderRouter(
        IEnumerable<ILlmProvider> providers,
        IHealthTracker healthTracker,
        IModelCatalog? modelCatalog = null,
        IRoundRobinState? roundRobinState = null)
    {
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(healthTracker);

        _providers = providers.ToList();
        _healthTracker = healthTracker;
        _modelCatalog = modelCatalog ?? new ModelRoutingCatalog();
        IRoundRobinState rrState = roundRobinState ?? new ThreadSafeRoundRobinState();

        _strategies = new Dictionary<RoutingStrategy, IRoutingStrategy>
        {
            [RoutingStrategy.LeastLatency] = new LeastLatencyRoutingStrategy(),
            [RoutingStrategy.RoundRobin] = new RoundRobinRoutingStrategy(rrState),
            [RoutingStrategy.PriorityWithFallback] = new PriorityWithFallbackRoutingStrategy(),
            [RoutingStrategy.LowestCost] = new LowestCostRoutingStrategy()
        };
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderRoute> Resolve(ChatRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        List<ProviderRoute> modelCompatible = ResolveModelRoutes(request.Model);
        if (modelCompatible.Count == 0)
        {
            throw new UnsupportedModelException(request.Model);
        }

        Dictionary<string, ProviderHealth> healthByProvider = new(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, bool> halfOpenAcquired = new(StringComparer.OrdinalIgnoreCase);
        List<ProviderRoute> eligibleRoutes = new(modelCompatible.Count);

        foreach (ProviderRoute route in modelCompatible)
        {
            string providerName = route.Provider.Name;
            if (!healthByProvider.TryGetValue(providerName, out ProviderHealth? health))
            {
                health = _healthTracker.GetHealth(providerName);
                healthByProvider[providerName] = health;
            }

            if (health.CircuitState == CircuitState.Open)
            {
                continue;
            }

            if (health.CircuitState == CircuitState.HalfOpen)
            {
                if (!halfOpenAcquired.TryGetValue(providerName, out bool acquired))
                {
                    acquired = _healthTracker.TryAcquireHalfOpenProbe(providerName);
                    halfOpenAcquired[providerName] = acquired;
                }

                if (!acquired)
                {
                    continue;
                }

                eligibleRoutes.Add(route with { IsHalfOpenProbe = true });
                continue;
            }

            eligibleRoutes.Add(route);
        }

        // Graceful degradation: when every compatible circuit is open or busy probing, return all compatible routes.
        if (eligibleRoutes.Count == 0)
        {
            return modelCompatible;
        }

        List<ProviderRoute> healthyRoutes = eligibleRoutes
            .Where(route => healthByProvider[route.Provider.Name].IsHealthy || route.IsHalfOpenProbe)
            .ToList();

        List<ProviderRoute> unhealthyRoutes = eligibleRoutes
            .Where(route => !healthByProvider[route.Provider.Name].IsHealthy && !route.IsHalfOpenProbe)
            .ToList();

        IRoutingStrategy strategy = _strategies.TryGetValue(request.Strategy, out IRoutingStrategy? resolvedStrategy)
            ? resolvedStrategy
            : _strategies[RoutingStrategy.LeastLatency];

        IReadOnlyList<ProviderRoute> orderedHealthy = healthyRoutes.Count > 0
            ? strategy.Order(healthyRoutes, healthByProvider, request)
            : [];

        if (unhealthyRoutes.Count == 0)
        {
            return orderedHealthy;
        }

        IReadOnlyList<ProviderRoute> orderedUnhealthy = strategy.Order(unhealthyRoutes, healthByProvider, request);
        if (orderedHealthy.Count == 0)
        {
            return orderedUnhealthy;
        }

        return orderedHealthy.Concat(orderedUnhealthy).ToList();
    }

    private List<ProviderRoute> ResolveModelRoutes(string requestedModel)
    {
        List<ProviderRoute> routes = [];
        HashSet<(string Provider, string Model)> seen = [];

        // 1. Direct exact-model matches in DI registration order always stay primary.
        foreach (ILlmProvider provider in _providers)
        {
            if (provider.SupportedModels.Contains(requestedModel) &&
                seen.Add((provider.Name, requestedModel)))
            {
                routes.Add(new ProviderRoute(
                    provider,
                    requestedModel,
                    _modelCatalog.GetPricing(provider.Name, requestedModel)));
            }
        }

        // 2. Logical aliases and cross-provider fallback chains from the catalog.
        IReadOnlyList<ModelTarget> catalogTargets = _modelCatalog.GetTargets(requestedModel);
        foreach (ModelTarget target in catalogTargets)
        {
            foreach (ILlmProvider provider in _providers)
            {
                if (!provider.Name.Equals(target.ProviderName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!provider.SupportedModels.Contains(target.Model))
                {
                    continue;
                }

                if (seen.Add((provider.Name, target.Model)))
                {
                    routes.Add(new ProviderRoute(
                        provider,
                        target.Model,
                        _modelCatalog.GetPricing(provider.Name, target.Model)));
                }
            }
        }

        return routes;
    }
}
