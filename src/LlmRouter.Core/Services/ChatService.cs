using System.Diagnostics;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Services;

/// <summary>
/// Orchestrates routing, provider execution, health tracking, and ordered fallback.
/// </summary>
public sealed class ChatService
{
    private readonly IHealthTracker _healthTracker;
    private readonly Action<string, Exception>? _logWarning;
    private readonly IProviderRouter _router;

    /// <summary>
    /// Creates a chat service. The optional warning delegate keeps Core free of logging package dependencies.
    /// </summary>
    public ChatService(IProviderRouter router, IHealthTracker healthTracker, Action<string, Exception>? logWarning = null)
    {
        _router = router;
        _healthTracker = healthTracker;
        _logWarning = logWarning;
    }

    /// <summary>
    /// Executes a chat completion against the ordered provider list until one succeeds.
    /// </summary>
    /// <exception cref="AllProvidersFailedException">Thrown when all resolved providers fail.</exception>
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        IReadOnlyList<ILlmProvider> providers = _router.Resolve(request);
        Exception? lastException = null;

        foreach (ILlmProvider provider in providers)
        {
            Stopwatch stopwatch = Stopwatch.StartNew();
            try
            {
                ChatResponse response = await provider.CompleteAsync(request, ct).ConfigureAwait(false);
                stopwatch.Stop();
                _healthTracker.RecordSuccess(provider.Name, stopwatch.ElapsedMilliseconds);
                return response with { LatencyMs = response.LatencyMs > 0 ? response.LatencyMs : stopwatch.ElapsedMilliseconds };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                lastException = new ProviderCallFailedException(provider.Name, ex);
                _healthTracker.RecordFailure(provider.Name);
                _logWarning?.Invoke($"Provider {provider.Name} failed; trying next provider.", lastException);
            }
        }

        throw new AllProvidersFailedException("All providers failed to complete the chat request.", lastException);
    }
}
