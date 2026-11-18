namespace LlmRouter.Core.Abstractions;

/// <summary>
/// Thread-safe shared counter store for round-robin routing across request scopes.
/// </summary>
public interface IRoundRobinState
{
    /// <summary>
    /// Atomically advances and returns the next sequence index for a routing key.
    /// </summary>
    long Next(string routeKey);
}
