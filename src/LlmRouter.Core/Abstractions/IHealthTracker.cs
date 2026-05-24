using LlmRouter.Core.Models;

namespace LlmRouter.Core.Abstractions;

/// <summary>
/// Tracks provider health, latency, error rate, and circuit state.
/// </summary>
public interface IHealthTracker
{
    /// <summary>
    /// Gets current health for a provider.
    /// </summary>
    ProviderHealth GetHealth(string providerName);

    /// <summary>
    /// Gets health for all known providers.
    /// </summary>
    IReadOnlyList<ProviderHealth> GetAllHealth();

    /// <summary>
    /// Records a successful provider call and its latency.
    /// </summary>
    void RecordSuccess(string providerName, long latencyMs);

    /// <summary>
    /// Records a failed provider call.
    /// </summary>
    void RecordFailure(string providerName);

    /// <summary>
    /// Updates the externally observed health state for a provider.
    /// </summary>
    void UpdateHealth(string providerName, bool isHealthy);
}
