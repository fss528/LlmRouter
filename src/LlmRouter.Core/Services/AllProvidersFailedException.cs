namespace LlmRouter.Core.Services;

/// <summary>
/// Indicates that every resolved provider failed to serve a chat request.
/// </summary>
public sealed class AllProvidersFailedException : Exception
{
    /// <summary>
    /// Creates an exception with the last provider exception as the inner exception.
    /// </summary>
    public AllProvidersFailedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
