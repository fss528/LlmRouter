using LlmRouter.Core.Models;
using LlmRouter.Infrastructure.Configuration;
using LlmRouter.Infrastructure.Health;
using Microsoft.Extensions.Options;
using NSubstitute;
using StackExchange.Redis;

namespace LlmRouter.Tests;

public sealed class RedisHealthTrackerTests
{
    [Fact]
    public void GetHealth_ParsesRedisLuaSnapshotIntoProviderHealth()
    {
        DateTimeOffset fixedNow = new(2026, 11, 18, 12, 0, 0, TimeSpan.Zero);
        ManualTimeProvider time = new(fixedNow);

        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        RedisResult[] luaResult =
        [
            RedisResult.Create((RedisValue)"1"),
            RedisResult.Create((RedisValue)"2"), // HalfOpen
            RedisResult.Create(10L),
            RedisResult.Create(2L),
            RedisResult.Create((RedisValue)fixedNow.ToUnixTimeMilliseconds().ToString()),
            RedisResult.Create((RedisValue)"120"),
            RedisResult.Create((RedisValue)"40"),
            RedisResult.Create((RedisValue)"80")
        ];

        db.ScriptEvaluate(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(luaResult));

        RedisHealthTracker tracker = new(
            mux,
            Options.Create(new StateStoreOptions { Provider = "Redis", KeyPrefix = "test:health" }),
            time);

        ProviderHealth health = tracker.GetHealth("OpenAI");

        Assert.Equal("OpenAI", health.ProviderName);
        Assert.True(health.IsHealthy);
        Assert.Equal(CircuitState.HalfOpen, health.CircuitState);
        Assert.Equal(0.2, health.ErrorRateLast5Min, precision: 3);
        Assert.Equal(120, health.P95LatencyMs);
        Assert.Equal(3, health.LatencySampleCount);
    }

    [Fact]
    public void TryAcquireAndReleaseHalfOpenProbe_DelegatesAtomicallyToRedis()
    {
        IConnectionMultiplexer mux = Substitute.For<IConnectionMultiplexer>();
        IDatabase db = Substitute.For<IDatabase>();
        mux.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(db);

        db.ScriptEvaluate(
                Arg.Any<string>(),
                Arg.Any<RedisKey[]?>(),
                Arg.Any<RedisValue[]?>(),
                Arg.Any<CommandFlags>())
            .Returns(RedisResult.Create(1), RedisResult.Create(0));

        RedisHealthTracker tracker = new(
            mux,
            Options.Create(new StateStoreOptions { Provider = "Redis", KeyPrefix = "test:health" }));

        Assert.True(tracker.TryAcquireHalfOpenProbe("Gemini"));
        Assert.False(tracker.TryAcquireHalfOpenProbe("Gemini"));

        tracker.ReleaseHalfOpenProbe("Gemini");
        db.Received(1).HashSet(
            Arg.Is<RedisKey>(k => k.ToString().Contains("gemini", StringComparison.Ordinal)),
            Arg.Is<RedisValue>("halfOpenInFlight"),
            Arg.Is<RedisValue>("0"),
            Arg.Any<When>(),
            Arg.Any<CommandFlags>());
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
