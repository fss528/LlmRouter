# ADR 0002: Custom Circuit Breaker with Single-Flight Half-Open Probing

## Status
Accepted

## Context
LLM upstream failures exhibit long tail latencies and sustained 429/5xx outages. Standard HTTP retry libraries do not share sliding-window error rates and P95 latencies across routing strategies, and naive `HalfOpen` transitions allow concurrent requests (a thundering herd) to flood a recovering provider simultaneously.

Furthermore, a naive `LeastLatency` sort on `P95LatencyMs` treats unmeasured or failing providers (`P95LatencyMs == 0`) as faster than healthy providers with real traffic (`P95LatencyMs > 0`), and `400 Bad Request` errors caused by caller payloads should not trip circuit breakers for healthy providers.

## Decision
1. Implement an explicit sliding-window state machine (`Closed` -> `Open` -> `HalfOpen`) in `IHealthTracker` (`> 50%` error rate over a 5-minute window with at least 5 requests, 30s open cooldown).
2. Enforce single-flight `HalfOpen` probing via `TryAcquireHalfOpenProbe` / `ReleaseHalfOpenProbe`: only one concurrent request probes a recovering provider while parallel traffic routes around it.
3. Separate `IsHealthy` from `CircuitState` in `ProviderRouter`, placing unhealthy or unmeasured cold-start providers behind healthy measured providers in `LeastLatency`.
4. Classify HTTP `400`/`422` upstream rejections as non-retriable `ProviderClientException` so bad client prompts do not penalize provider health.

## Consequences
- Recovering providers receive controlled single-request probes rather than traffic spikes.
- Unconfigured or degraded providers with `0ms` latency history never hijack `LeastLatency` traffic from healthy measured providers.
- Circuit state transitions remain deterministic in unit tests via injected `TimeProvider`.
