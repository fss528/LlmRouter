using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal interface IRoutingStrategy
{
    IReadOnlyList<ILlmProvider> Order(IReadOnlyList<ILlmProvider> providers, ChatRequest request);
}
