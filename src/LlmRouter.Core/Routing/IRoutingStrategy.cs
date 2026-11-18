using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

internal interface IRoutingStrategy
{
    IReadOnlyList<ProviderRoute> Order(
        IReadOnlyList<ProviderRoute> routes,
        IReadOnlyDictionary<string, ProviderHealth> healthByProvider,
        ChatRequest request);
}
