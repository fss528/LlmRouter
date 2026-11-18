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
        Assert.False(health.IsHealthy);
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
    public void RecordFailure_ReopensCircuitImmediatelyWhenHalfOpen()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);
        OpenCircuit(tracker);
        timeProvider.Advance(TimeSpan.FromSeconds(31));
        Assert.True(tracker.TryAcquireHalfOpenProbe("provider"));

        tracker.RecordFailure("provider");
        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(CircuitState.Open, health.CircuitState);
        Assert.False(tracker.TryAcquireHalfOpenProbe("provider"));
    }

    [Fact]
    public void TryAcquireHalfOpenProbe_AllowsSingleConcurrentCallerAndReleasesCleanly()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);
        OpenCircuit(tracker);
        timeProvider.Advance(TimeSpan.FromSeconds(31));

        bool firstAcquire = tracker.TryAcquireHalfOpenProbe("provider");
        bool secondAcquire = tracker.TryAcquireHalfOpenProbe("provider");

        Assert.True(firstAcquire);
        Assert.False(secondAcquire);

        tracker.ReleaseHalfOpenProbe("provider");
        Assert.True(tracker.TryAcquireHalfOpenProbe("provider"));
    }

    [Fact]
    public void UpdateHealth_DoesNotMaskOpenCircuitAsHealthy()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);
        OpenCircuit(tracker);

        tracker.UpdateHealth("provider", isHealthy: true);
        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(CircuitState.Open, health.CircuitState);
        Assert.False(health.IsHealthy);
    }

    [Fact]
    public void SlidingWindow_PurgesSamplesOlderThanFiveMinutes()
    {
        ManualTimeProvider timeProvider = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker tracker = new(timeProvider);

        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");
        tracker.RecordFailure("provider");

        timeProvider.Advance(TimeSpan.FromMinutes(6));
        tracker.RecordSuccess("provider", 15);
        tracker.RecordSuccess("provider", 25);

        ProviderHealth health = tracker.GetHealth("provider");

        Assert.Equal(0, health.ErrorRateLast5Min);
        Assert.Equal(CircuitState.Closed, health.CircuitState);
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
        Assert.Equal(100, health.LatencySampleCount);
    }

    [Fact]
    public async Task ConcurrentUpdates_AreThreadSafe()
    {
        InMemoryHealthTracker tracker = new();

        Task[] tasks = Enumerable.Range(0, 20)
            .Select(i => Task.Run(() =>
            {
                for (int j = 0; j < 50; j++)
                {
                    if ((i + j) % 5 == 0)
                    {
                        tracker.RecordFailure("provider");
                    }
                    else
                    {
                        tracker.RecordSuccess("provider", 10 + j);
                    }

                    _ = tracker.GetAllHealth();
                }
            }))
            .ToArray();

        await Task.WhenAll(tasks);

        ProviderHealth health = tracker.GetHealth("provider");
        Assert.True(health.LatencySampleCount > 0);
    }

    private static void OpenCircuit(InMemoryHealthTracker tracker)
    {
        for (int i = 0; i < 5; i++)
        {
            tracker.RecordFailure("provider");
        }

        Assert.Equal(CircuitState.Open, tracker.GetHealth("provider").CircuitState);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
