using LlmRouter.Core.Models;

namespace LlmRouter.Core.Abstractions;

/// <summary>
/// Provider adapter contract. Implementations own provider-specific wire formats, auth, and model mappings.
/// </summary>
public interface ILlmProvider
{
    /// <summary>
    /// Stable provider name used for health tracking and responses.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Logical model names supported by this provider.
    /// </summary>
    IReadOnlySet<string> SupportedModels { get; }

    /// <summary>
    /// Executes a provider-specific completion request.
    /// </summary>
    Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default);

    /// <summary>
    /// Executes a lightweight provider health check.
    /// </summary>
    Task<bool> IsHealthyAsync(CancellationToken ct = default);
}
