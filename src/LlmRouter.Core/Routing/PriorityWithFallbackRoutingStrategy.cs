using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class PriorityWithFallbackRoutingStrategy : IRoutingStrategy
{
    public IReadOnlyList<ProviderRoute> Order(
        IReadOnlyList<ProviderRoute> routes,
        IReadOnlyDictionary<string, ProviderHealth> healthByProvider,
        ChatRequest request)
    {
        return routes;
    }
}
