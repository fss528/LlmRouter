using LlmRouter.Core.Abstractions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LlmRouter.Infrastructure.BackgroundServices;

/// <summary>
/// Periodically probes configured providers and updates the shared health tracker.
/// </summary>
public sealed class HealthCheckerService : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StartupStagger = TimeSpan.FromSeconds(2);

    private readonly IHealthTracker _healthTracker;
    private readonly ILogger<HealthCheckerService> _logger;
    private readonly IReadOnlyList<ILlmProvider> _providers;

    /// <summary>
    /// Creates the health checker background service.
    /// </summary>
    public HealthCheckerService(IEnumerable<ILlmProvider> providers, IHealthTracker healthTracker, ILogger<HealthCheckerService> logger)
    {
        _providers = providers.ToList();
        _healthTracker = healthTracker;
        _logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        List<Task> providerLoops = new(capacity: _providers.Count);
        for (int index = 0; index < _providers.Count; index++)
        {
            ILlmProvider provider = _providers[index];
            providerLoops.Add(PollProviderAsync(provider, index, stoppingToken));
        }

        await Task.WhenAll(providerLoops).ConfigureAwait(false);
    }

    private async Task PollProviderAsync(ILlmProvider provider, int providerIndex, CancellationToken stoppingToken)
    {
        try
        {
            TimeSpan initialDelay = TimeSpan.FromMilliseconds(providerIndex * StartupStagger.TotalMilliseconds);
            await Task.Delay(initialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                bool isHealthy = await provider.IsHealthyAsync(stoppingToken).ConfigureAwait(false);
                _healthTracker.UpdateHealth(provider.Name, isHealthy);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Health check failed for provider {ProviderName}.", provider.Name);
                _healthTracker.UpdateHealth(provider.Name, false);
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
