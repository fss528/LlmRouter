using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class RoundRobinRoutingStrategy : IRoutingStrategy
{
    private readonly IRoundRobinState _state;

    public RoundRobinRoutingStrategy(IRoundRobinState state)
    {
        _state = state;
    }

    public IReadOnlyList<ProviderRoute> Order(
        IReadOnlyList<ProviderRoute> routes,
        IReadOnlyDictionary<string, ProviderHealth> healthByProvider,
        ChatRequest request)
    {
        if (routes.Count <= 1)
        {
            return routes;
        }

        string routeKey = string.Join('|', routes.Select(r => $"{r.Provider.Name}:{r.Model}"));
        long next = _state.Next(routeKey);
        int offset = (int)((ulong)next % (uint)routes.Count);
        return routes.Skip(offset).Concat(routes.Take(offset)).ToList();
    }
}
