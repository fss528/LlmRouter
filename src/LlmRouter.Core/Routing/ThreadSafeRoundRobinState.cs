using System.Collections.Concurrent;
using LlmRouter.Core.Abstractions;

namespace LlmRouter.Core.Routing;

/// <summary>
/// Singleton-safe round-robin counter store using lock-free atomic increments per route key.
/// </summary>
public sealed class ThreadSafeRoundRobinState : IRoundRobinState
{
    private readonly ConcurrentDictionary<string, CounterBox> _counters = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public long Next(string routeKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(routeKey);
        CounterBox box = _counters.GetOrAdd(routeKey, static _ => new CounterBox());
        return Interlocked.Increment(ref box.Value);
    }

    private sealed class CounterBox
    {
        public long Value = -1;
    }
}
