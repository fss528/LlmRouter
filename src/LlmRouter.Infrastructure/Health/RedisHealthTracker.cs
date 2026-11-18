using System.Globalization;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace LlmRouter.Infrastructure.Health;

/// <summary>
/// Distributed <see cref="IHealthTracker"/> backed by Redis using atomic Lua scripts for
/// sliding-window error rates, P95 latency sampling, circuit-breaker transitions, and single-flight half-open probes.
/// </summary>
public sealed class RedisHealthTracker : IHealthTracker
{
    private const long ErrorWindowMs = 5 * 60 * 1000;
    private const long OpenToHalfOpenMs = 30 * 1000;
    private const int MinimumRequestsBeforeOpening = 5;
    private const int MaxLatencySamples = 100;
    private const double ErrorRateOpenThreshold = 0.50;

    private readonly IConnectionMultiplexer _redis;
    private readonly string _keyPrefix;
    private readonly TimeProvider _timeProvider;

    /// <summary>
    /// Creates a Redis-backed distributed health tracker.
    /// </summary>
    public RedisHealthTracker(
        IConnectionMultiplexer redis,
        IOptions<StateStoreOptions> options,
        TimeProvider? timeProvider = null)
    {
        _redis = redis;
        _keyPrefix = string.IsNullOrWhiteSpace(options.Value.KeyPrefix) ? "llmrouter:health" : options.Value.KeyPrefix;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public ProviderHealth GetHealth(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IDatabase db = _redis.GetDatabase();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        RedisResult result = db.ScriptEvaluate(
            GetHealthScript,
            BuildKeys(providerName),
            [providerName, nowMs, ErrorWindowMs, OpenToHalfOpenMs]);

        return ParseHealthSnapshot(providerName, (RedisResult[]?)result, nowMs);
    }

    /// <inheritdoc />
    public IReadOnlyList<ProviderHealth> GetAllHealth()
    {
        IDatabase db = _redis.GetDatabase();
        RedisValue[] members = db.SetMembers($"{_keyPrefix}:providers");
        List<ProviderHealth> results = new(members.Length);

        foreach (RedisValue member in members)
        {
            string? name = member.ToString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                results.Add(GetHealth(name));
            }
        }

        return results.OrderBy(h => h.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <inheritdoc />
    public void RecordSuccess(string providerName, long latencyMs)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IDatabase db = _redis.GetDatabase();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        db.ScriptEvaluate(
            RecordSuccessScript,
            BuildKeys(providerName),
            [providerName, nowMs, ErrorWindowMs, Math.Max(0, latencyMs), MaxLatencySamples - 1]);
    }

    /// <inheritdoc />
    public void RecordFailure(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IDatabase db = _redis.GetDatabase();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        db.ScriptEvaluate(
            RecordFailureScript,
            BuildKeys(providerName),
            [providerName, nowMs, ErrorWindowMs, OpenToHalfOpenMs, MinimumRequestsBeforeOpening, ErrorRateOpenThreshold]);
    }

    /// <inheritdoc />
    public void UpdateHealth(string providerName, bool isHealthy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IDatabase db = _redis.GetDatabase();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        db.ScriptEvaluate(
            UpdateHealthScript,
            BuildKeys(providerName),
            [providerName, nowMs, ErrorWindowMs, OpenToHalfOpenMs, isHealthy ? "1" : "0"]);
    }

    /// <inheritdoc />
    public bool TryAcquireHalfOpenProbe(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IDatabase db = _redis.GetDatabase();
        long nowMs = _timeProvider.GetUtcNow().ToUnixTimeMilliseconds();

        RedisResult result = db.ScriptEvaluate(
            TryAcquireHalfOpenScript,
            BuildKeys(providerName),
            [providerName, nowMs, ErrorWindowMs, OpenToHalfOpenMs]);

        return (int)result == 1;
    }

    /// <inheritdoc />
    public void ReleaseHalfOpenProbe(string providerName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);
        IDatabase db = _redis.GetDatabase();
        db.HashSet(MetaKey(providerName), "halfOpenInFlight", "0");
    }

    private string HashTag(string providerName) => $"{{{_keyPrefix}:{providerName.ToLowerInvariant()}}}";
    private string MetaKey(string providerName) => $"{HashTag(providerName)}:meta";

    private RedisKey[] BuildKeys(string providerName)
    {
        string tag = HashTag(providerName);
        return
        [
            $"{tag}:meta",
            $"{tag}:reqs",
            $"{tag}:fails",
            $"{tag}:lat",
            $"{tag}:seq",
            $"{_keyPrefix}:providers"
        ];
    }

    private static ProviderHealth ParseHealthSnapshot(string providerName, RedisResult[]? values, long fallbackNowMs)
    {
        if (values is null || values.Length < 5)
        {
            return new ProviderHealth(providerName, true, 0, 0, DateTimeOffset.FromUnixTimeMilliseconds(fallbackNowMs), CircuitState.Closed);
        }

        bool isHealthy = (string?)values[0] != "0";
        CircuitState circuitState = int.TryParse((string?)values[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int stateVal)
            ? (CircuitState)stateVal
            : CircuitState.Closed;

        long reqCount = (long)values[2];
        long failCount = (long)values[3];
        long lastCheckedMs = long.TryParse((string?)values[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ts)
            ? ts
            : fallbackNowMs;

        List<long> latencies = new(Math.Max(0, values.Length - 5));
        for (int i = 5; i < values.Length; i++)
        {
            if (long.TryParse((string?)values[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out long sample))
            {
                latencies.Add(sample);
            }
        }

        double errorRate = reqCount == 0 ? 0 : (double)failCount / reqCount;
        double p95 = CalculateP95Latency(latencies);
        return new ProviderHealth(
            providerName,
            isHealthy,
            p95,
            errorRate,
            DateTimeOffset.FromUnixTimeMilliseconds(lastCheckedMs),
            circuitState,
            latencies.Count);
    }

    private static double CalculateP95Latency(List<long> samples)
    {
        if (samples.Count == 0)
        {
            return 0;
        }

        samples.Sort();
        int index = (int)Math.Ceiling(samples.Count * 0.95) - 1;
        return samples[Math.Clamp(index, 0, samples.Count - 1)];
    }

    private const string LuaPurgeAndTransition = """
        local metaKey = KEYS[1]
        local reqsKey = KEYS[2]
        local failsKey = KEYS[3]
        local nowMs = tonumber(ARGV[2])
        local windowMs = tonumber(ARGV[3])
        local openWaitMs = tonumber(ARGV[4])
        local cutoff = nowMs - windowMs
        redis.call('ZREMRANGEBYSCORE', reqsKey, '-inf', '(' .. cutoff)
        redis.call('ZREMRANGEBYSCORE', failsKey, '-inf', '(' .. cutoff)
        redis.call('HSETNX', metaKey, 'isHealthy', '1')
        redis.call('HSETNX', metaKey, 'circuitState', '0')
        local circuit = redis.call('HGET', metaKey, 'circuitState')
        if circuit == '1' and openWaitMs ~= nil then
            local openedAt = tonumber(redis.call('HGET', metaKey, 'openedAtMs') or '')
            if openedAt ~= nil and (nowMs - openedAt) >= openWaitMs then
                redis.call('HSET', metaKey, 'circuitState', '2', 'halfOpenInFlight', '0')
            end
        end
        """;

    private const string GetHealthScript = LuaPurgeAndTransition + """
        redis.call('SADD', KEYS[6], ARGV[1])
        redis.call('HSET', KEYS[1], 'lastCheckedMs', ARGV[2])
        local isHealthy = redis.call('HGET', KEYS[1], 'isHealthy')
        local circuit = redis.call('HGET', KEYS[1], 'circuitState')
        local reqCount = redis.call('ZCARD', KEYS[2])
        local failCount = redis.call('ZCARD', KEYS[3])
        local samples = redis.call('LRANGE', KEYS[4], 0, -1)
        local res = { isHealthy, circuit, reqCount, failCount, ARGV[2] }
        for i = 1, #samples do
            res[#res + 1] = samples[i]
        end
        return res
        """;

    private const string RecordSuccessScript = """
        local metaKey = KEYS[1]
        local reqsKey = KEYS[2]
        local failsKey = KEYS[3]
        local latKey = KEYS[4]
        local seqKey = KEYS[5]
        local nowMs = tonumber(ARGV[2])
        local windowMs = tonumber(ARGV[3])
        local cutoff = nowMs - windowMs
        redis.call('SADD', KEYS[6], ARGV[1])
        redis.call('ZREMRANGEBYSCORE', reqsKey, '-inf', '(' .. cutoff)
        redis.call('ZREMRANGEBYSCORE', failsKey, '-inf', '(' .. cutoff)
        local seq = redis.call('INCR', seqKey)
        redis.call('ZADD', reqsKey, nowMs, ARGV[2] .. ':' .. seq)
        redis.call('LPUSH', latKey, ARGV[4])
        redis.call('LTRIM', latKey, 0, tonumber(ARGV[5]))
        local circuit = redis.call('HGET', metaKey, 'circuitState')
        if circuit == '2' then
            redis.call('HSET', metaKey, 'circuitState', '0', 'openedAtMs', '')
        else
            redis.call('HSETNX', metaKey, 'circuitState', '0')
        end
        redis.call('HSET', metaKey, 'halfOpenInFlight', '0', 'isHealthy', '1', 'lastCheckedMs', ARGV[2])
        return 1
        """;

    private const string RecordFailureScript = LuaPurgeAndTransition + """
        redis.call('SADD', KEYS[6], ARGV[1])
        local seq = redis.call('INCR', KEYS[5])
        local member = ARGV[2] .. ':' .. seq
        redis.call('ZADD', KEYS[2], nowMs, member)
        redis.call('ZADD', KEYS[3], nowMs, member)
        redis.call('HSET', KEYS[1], 'isHealthy', '0', 'halfOpenInFlight', '0', 'lastCheckedMs', ARGV[2])
        local circuit = redis.call('HGET', KEYS[1], 'circuitState')
        if circuit == '2' then
            redis.call('HSET', KEYS[1], 'circuitState', '1', 'openedAtMs', ARGV[2])
        elseif circuit == '0' then
            local reqCount = redis.call('ZCARD', KEYS[2])
            local minReq = tonumber(ARGV[5])
            local threshold = tonumber(ARGV[6])
            if reqCount >= minReq then
                local failCount = redis.call('ZCARD', KEYS[3])
                if (failCount / reqCount) > threshold then
                    redis.call('HSET', KEYS[1], 'circuitState', '1', 'openedAtMs', ARGV[2], 'isHealthy', '0')
                end
            end
        end
        return 1
        """;

    private const string UpdateHealthScript = LuaPurgeAndTransition + """
        redis.call('SADD', KEYS[6], ARGV[1])
        local circuit = redis.call('HGET', KEYS[1], 'circuitState')
        if circuit == '1' then
            redis.call('HSET', KEYS[1], 'isHealthy', '0', 'lastCheckedMs', ARGV[2])
        else
            redis.call('HSET', KEYS[1], 'isHealthy', ARGV[5], 'lastCheckedMs', ARGV[2])
        end
        return 1
        """;

    private const string TryAcquireHalfOpenScript = LuaPurgeAndTransition + """
        redis.call('SADD', KEYS[6], ARGV[1])
        local circuit = redis.call('HGET', KEYS[1], 'circuitState')
        local inFlight = redis.call('HGET', KEYS[1], 'halfOpenInFlight')
        if circuit == '2' and inFlight ~= '1' then
            redis.call('HSET', KEYS[1], 'halfOpenInFlight', '1')
            return 1
        end
        return 0
        """;
}
