using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class PriorityWithFallbackRoutingStrategy : IRoutingStrategy
{
    public IReadOnlyList<ILlmProvider> Order(IReadOnlyList<ILlmProvider> providers, ChatRequest request)
    {
        return providers;
    }
}
