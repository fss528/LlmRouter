using LlmRouter.Core.Models;

namespace LlmRouter.Core.Services;

/// <summary>
/// Indicates that every resolved provider failed to serve a chat request.
/// </summary>
public sealed class AllProvidersFailedException : Exception
{
    /// <summary>
    /// Creates an exception with the last provider exception and full attempt trace.
    /// </summary>
    public AllProvidersFailedException(
        string message,
        Exception? innerException,
        IReadOnlyList<ProviderAttempt>? attempts = null)
        : base(message, innerException)
    {
        Attempts = attempts ?? [];
    }

    /// <summary>Ordered record of every failed attempt made before giving up.</summary>
    public IReadOnlyList<ProviderAttempt> Attempts { get; }
}
