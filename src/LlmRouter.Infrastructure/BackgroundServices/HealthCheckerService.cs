using LlmRouter.Core.Abstractions;
using LlmRouter.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LlmRouter.Infrastructure.BackgroundServices;

/// <summary>
/// Periodically probes configured providers with staggered startup and updates the shared health tracker.
/// </summary>
public sealed class HealthCheckerService : BackgroundService
{
    private static readonly TimeSpan StartupStagger = TimeSpan.FromSeconds(2);

    private readonly bool _enabled;
    private readonly IHealthTracker _healthTracker;
    private readonly ILogger<HealthCheckerService> _logger;
    private readonly TimeSpan _pollInterval;
    private readonly IReadOnlyList<ILlmProvider> _providers;

    /// <summary>
    /// Creates the health checker background service.
    /// </summary>
    public HealthCheckerService(
        IEnumerable<ILlmProvider> providers,
        IHealthTracker healthTracker,
        ILogger<HealthCheckerService> logger,
        IOptions<ProvidersOptions>? options = null)
    {
        _providers = providers.ToList();
        _healthTracker = healthTracker;
        _logger = logger;
        ProvidersOptions opts = options?.Value ?? new ProvidersOptions();
        _enabled = opts.HealthCheckEnabled;
        _pollInterval = TimeSpan.FromSeconds(Math.Clamp(opts.HealthCheckIntervalSeconds, 5, 3600));
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_enabled || _providers.Count == 0)
        {
            return;
        }

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
            if (initialDelay > TimeSpan.Zero)
            {
                await Task.Delay(initialDelay, stoppingToken).ConfigureAwait(false);
            }
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
                _logger.LogWarning(ex, "Health check failed for provider {ProviderName}.", provider.Name);
                _healthTracker.UpdateHealth(provider.Name, false);
            }

            try
            {
                await Task.Delay(_pollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
