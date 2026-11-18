# ADR 0003: Dual In-Memory and Redis Atomic Lua Health State Store

## Status
Accepted

## Context
An in-memory `IHealthTracker` using `System.Threading.Lock` offers sub-microsecond overhead and zero external infrastructure for single-node deployments and unit tests, but circuit breaker state and sliding-window metrics are lost on restarts and cannot be shared across horizontally scaled gateway replicas.

## Decision
Keep `IHealthTracker` as the sole domain abstraction in `LlmRouter.Core` (zero NuGet dependencies) and provide two interchangeable implementations:
1. `InMemoryHealthTracker` (default): per-provider `Lock` objects and `TimeProvider` for single-node speed and testing.
2. `RedisHealthTracker` (enabled via `StateStore:Provider = "Redis"`): executes atomic Redis Lua scripts combining `ZREMRANGEBYSCORE` sliding-window purges, `LPUSH`/`LTRIM` latency sampling, circuit transitions, and `halfOpenInFlight` single-flight locks under `{provider}` cluster hash tags.

## Consequences
- Horizontal replicas share consistent circuit-breaker state and latency telemetry without distributed lock round-trips.
- Single-node runs and CI require no external Redis server.
- Clean Architecture boundaries are preserved: `LlmRouter.Core` remains completely unaware of `StackExchange.Redis`.
