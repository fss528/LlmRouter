using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal sealed class RoundRobinRoutingStrategy : IRoutingStrategy
{
    private long _counter = -1;

    public IReadOnlyList<ILlmProvider> Order(IReadOnlyList<ILlmProvider> providers, ChatRequest request)
    {
        if (providers.Count <= 1)
        {
            return providers;
        }

        long next = Interlocked.Increment(ref _counter);
        int offset = (int)(next % providers.Count);
        return providers.Skip(offset).Concat(providers.Take(offset)).ToList();
    }
}
