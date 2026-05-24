using LlmRouter.Core.Models;

namespace LlmRouter.Core.Abstractions;

/// <summary>
/// Resolves an ordered provider list for a request. The caller owns fallback execution.
/// </summary>
public interface IProviderRouter
{
    /// <summary>
    /// Returns an ordered list where the first available provider should be attempted first.
    /// </summary>
    /// <exception cref="InvalidOperationException">Thrown when no provider supports the requested model.</exception>
    IReadOnlyList<ILlmProvider> Resolve(ChatRequest request);
}
