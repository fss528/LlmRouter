using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

/// <summary>
/// Thread-safe in-memory health tracker with sliding-window error rates and a simple circuit breaker.
/// </summary>
/// <remarks>
/// This implementation is appropriate for a single process. Multi-instance deployments should replace it with
/// a Redis-backed implementation so circuit state and metrics are shared across replicas.
/// </remarks>
public sealed class InMemoryHealthTracker : IHealthTracker
{
    private static readonly TimeSpan ErrorWindow = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OpenToHalfOpenAfter = TimeSpan.FromSeconds(30);
    private const int MinimumRequestsBeforeOpening = 5;
    private const int MaxLatencySamples = 100;
    private const double ErrorRateOpenThreshold = 0.50;

    private readonly Dictionary<string, ProviderState> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _statesLock = new();
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates an in-memory health tracker.
    /// </summary>
    public InMemoryHealthTracker(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ProviderHealth GetHealth(string providerName)
    {
        ProviderState state = GetOrCreateState(providerName);

        lock (state.Sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PurgeOldSamples(state, now);
            MoveOpenCircuitToHalfOpenWhenReady(state, now);
            state.LastChecked = now;
            return CreateHealthSnapshot(providerName, state, now);
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderHealth> GetAllHealth()
    {
        List<(string ProviderName, ProviderState State)> states;

        lock (_statesLock)
        {
            states = _states.Select(entry => (entry.Key, entry.Value)).ToList();
        }

        List<ProviderHealth> health = new(capacity: states.Count);
        foreach ((string providerName, ProviderState state) in states)
        {
            lock (state.Sync)
            {
                DateTimeOffset now = _timeProvider.GetUtcNow();
                PurgeOldSamples(state, now);
                MoveOpenCircuitToHalfOpenWhenReady(state, now);
                state.LastChecked = now;
                health.Add(CreateHealthSnapshot(providerName, state, now));
            }
        }

        return health.OrderBy(item => item.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public void RecordSuccess(string providerName, long latencyMs)
    {
        ProviderState state = GetOrCreateState(providerName);

        lock (state.Sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PurgeOldSamples(state, now);
            state.Requests.Enqueue(now);
            state.LatencySamples.Enqueue(Math.Max(0, latencyMs));
            while (state.LatencySamples.Count > MaxLatencySamples)
            {
                state.LatencySamples.Dequeue();
            }

            if (state.CircuitState == CircuitState.HalfOpen)
            {
                state.CircuitState = CircuitState.Closed;
                state.OpenedAt = null;
            }

            state.IsHealthy = true;
            state.LastChecked = now;
        }
    }

    /// <inheritdoc />
    public void RecordFailure(string providerName)
    {
        ProviderState state = GetOrCreateState(providerName);

        lock (state.Sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PurgeOldSamples(state, now);
            MoveOpenCircuitToHalfOpenWhenReady(state, now);
            state.Requests.Enqueue(now);
            state.Failures.Enqueue(now);
            state.IsHealthy = false;

            if (state.CircuitState == CircuitState.HalfOpen)
            {
                OpenCircuit(state, now);
            }
            else
            {
                EvaluateClosedCircuit(state, now);
            }

            state.LastChecked = now;
        }
    }

    /// <inheritdoc />
    public void UpdateHealth(string providerName, bool isHealthy)
    {
        ProviderState state = GetOrCreateState(providerName);

        lock (state.Sync)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            PurgeOldSamples(state, now);
            MoveOpenCircuitToHalfOpenWhenReady(state, now);
            state.IsHealthy = isHealthy;
            state.LastChecked = now;
        }
    }

    private ProviderState GetOrCreateState(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        lock (_statesLock)
        {
            if (!_states.TryGetValue(providerName, out ProviderState? state))
            {
                state = new ProviderState(_timeProvider.GetUtcNow());
                _states[providerName] = state;
            }

            return state;
        }
    }

    private static void PurgeOldSamples(ProviderState state, DateTimeOffset now)
    {
        DateTimeOffset cutoff = now.Subtract(ErrorWindow);
        while (state.Requests.TryPeek(out DateTimeOffset requestTime) && requestTime < cutoff)
        {
            state.Requests.Dequeue();
        }

        while (state.Failures.TryPeek(out DateTimeOffset failureTime) && failureTime < cutoff)
        {
            state.Failures.Dequeue();
        }
    }

    private static void MoveOpenCircuitToHalfOpenWhenReady(ProviderState state, DateTimeOffset now)
    {
        if (state.CircuitState == CircuitState.Open && state.OpenedAt is DateTimeOffset openedAt)
        {
            if (now - openedAt >= OpenToHalfOpenAfter)
            {
                state.CircuitState = CircuitState.HalfOpen;
            }
        }
    }

    private static void EvaluateClosedCircuit(ProviderState state, DateTimeOffset now)
    {
        if (state.CircuitState != CircuitState.Closed)
        {
            return;
        }

        int requestCount = state.Requests.Count;
        if (requestCount < MinimumRequestsBeforeOpening)
        {
            return;
        }

        double errorRate = (double)state.Failures.Count / requestCount;
        if (errorRate > ErrorRateOpenThreshold)
        {
            OpenCircuit(state, now);
        }
    }

    private static void OpenCircuit(ProviderState state, DateTimeOffset now)
    {
        state.CircuitState = CircuitState.Open;
        state.OpenedAt = now;
        state.IsHealthy = false;
    }

    private static ProviderHealth CreateHealthSnapshot(string providerName, ProviderState state, DateTimeOffset now)
    {
        double errorRate = state.Requests.Count == 0 ? 0 : (double)state.Failures.Count / state.Requests.Count;
        double p95Latency = CalculateP95Latency(state.LatencySamples);
        return new ProviderHealth(providerName, state.IsHealthy, p95Latency, errorRate, now, state.CircuitState);
    }

    private static double CalculateP95Latency(IEnumerable<long> samples)
    {
        List<long> orderedSamples = samples.Order().ToList();
        if (orderedSamples.Count == 0)
        {
            return 0;
        }

        int index = (int)Math.Ceiling(orderedSamples.Count * 0.95) - 1;
        return orderedSamples[Math.Clamp(index, 0, orderedSamples.Count - 1)];
    }

    private sealed class ProviderState
    {
        public ProviderState(DateTimeOffset createdAt)
        {
            LastChecked = createdAt;
        }

        public Lock Sync { get; } = new();
        public Queue<DateTimeOffset> Requests { get; } = new();
        public Queue<DateTimeOffset> Failures { get; } = new();
        public Queue<long> LatencySamples { get; } = new();
        public bool IsHealthy { get; set; } = true;
        public DateTimeOffset LastChecked { get; set; }
        public CircuitState CircuitState { get; set; } = CircuitState.Closed;
        public DateTimeOffset? OpenedAt { get; set; }
    }
}
