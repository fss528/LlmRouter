using LlmRouter.Core.Models;
using LlmRouter.Core.Routing;

namespace LlmRouter.Tests;

public sealed class InMemoryHealthTrackerTests
{
    [Fact]
    public void RecordFailure_OpensCircuitAfterMoreThanFiftyPercentErrorRateWithAtLeastFiveRequests()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);

        tracker.RecordSuccess("provider", 10);
        tracker.RecordSuccess("provider", 20);
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");

        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(CircuitState.Open, health.CircuitState);
        Assert.Equal(0.6, health.ErrorRateLast5Min, precision: 3);
    }

    [Fact]
    public void GetHealth_MovesCircuitToHalfOpenAfterThirtySeconds()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);
        OpenCircuit(tracker);

        timeProvider.Advance(TimeSpan.FromSeconds(31));
        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(CircuitState.HalfOpen, health.CircuitState);
    }

    [Fact]
    public void RecordSuccess_ClosesCircuitWhenHalfOpen()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);
        OpenCircuit(tracker);
        timeProvider.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(CircuitState.HalfOpen, tracker.GetHealth("provider").CircuitState);

        tracker.RecordSuccess("provider", 12);
        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(CircuitState.Closed, health.CircuitState);
        Assert.True(health.IsHealthy);
    }

    [Fact]
    public void GetHealth_CalculatesP95LatencyFromSamples()
    {
        InMemoryHealthTracker tracker = new();
        for (int latency = 1; latency <= 100; latency++)
        {
            tracker.RecordSuccess("provider", latency);
        }

        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(95, health.P95LatencyMs);
    }

    private static void OpenCircuit(InMemoryHealthTracker tracker)
    {
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");
        Assert.Equal(CircuitState.Open, tracker.GetHealth("provider").CircuitState);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow;

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }
}
