# ADR 0001: Ordered Route Resolution and Logical Model Aliasing

## Status
Accepted

## Context
Individual LLM vendors expose completely disjoint model identifiers (`gpt-4o`, `claude-sonnet-4-20250514`, `gemini-2.5-flash`). If a router matches only exact concrete model strings, multi-provider routing strategies (`LeastLatency`, `RoundRobin`, `LowestCost`) and fallback chains degenerate into single-provider lists in real deployments.

Additionally, mixing routing policy (which provider/model order to prefer) with fallback execution (timeouts, error classification, retries) inside a single class makes both concerns harder to test and evolve.

## Decision
1. `IProviderRouter.Resolve(ChatRequest)` returns an ordered `IReadOnlyList<ProviderRoute>` where each route pairs an `ILlmProvider` with its resolved concrete model and pricing metadata.
2. `IModelCatalog` (`ModelRoutingCatalog`) maps logical tiers (`fast`, `balanced`, `reasoning`, `chat-default`) and cross-provider fallback chains while allowing concrete vendor models to pass through directly.
3. `ChatService` owns execution orchestration, per-attempt timeouts, error classification (`400` client error vs `429`/`5xx`/timeout transient failure), health recording, and fallback loops.
4. `IRoundRobinState` is registered as a `Singleton` while `ProviderRouter` stays `Scoped`, ensuring round-robin counters rotate across HTTP requests without creating captive dependencies on `Transient` `IHttpClientFactory` typed provider clients.

## Consequences
- Routing strategies (`PriorityWithFallback`, `LeastLatency`, `RoundRobin`, `LowestCost`) remain pure, deterministic, and independently testable.
- Clients can target stable logical tiers (`"fast"`, `"balanced"`, `"reasoning"`) without coupling business code to a single vendor.
- Callers requesting an unmapped concrete model still get direct routing without unwanted silent model substitution unless configured in `Routing:Fallbacks`.
