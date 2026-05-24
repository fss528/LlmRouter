# LlmRouter

[![CI](https://github.com/fss528/LlmRouter/actions/workflows/ci.yml/badge.svg)](https://github.com/fss528/LlmRouter/actions/workflows/ci.yml)
![.NET](https://img.shields.io/badge/.NET-10-512BD4)
![License](https://img.shields.io/badge/license-MIT-green)
![Architecture](https://img.shields.io/badge/architecture-clean--architecture-blue)A production-oriented **multi-provider LLM request router** built with **C# / .NET 10**.

This project is intentionally designed as a portfolio-grade backend system: it favors clean architecture, reliability patterns, testability, and explicit tradeoff documentation over feature breadth.

## Why this exists

Modern AI products should not be tightly coupled to a single model vendor. Providers can fail, throttle, change pricing, deprecate model names, or behave differently across regions and accounts.

`LlmRouter` exposes one provider-agnostic chat API and routes each request across Anthropic, OpenAI, and Google Gemini adapters using health, latency, circuit state, and caller-selected strategy.

In practical terms, a multi-provider LLM router helps with:

- **Reliability:** if one provider is unhealthy or rate-limited, traffic can fall back to another compatible provider.
- **Cost and performance control:** routing strategies can prefer lower latency, explicit priority, or round-robin distribution.
- **Vendor independence:** provider-specific auth, payloads, model names, and response parsing stay behind adapter boundaries.

## What this demonstrates

- Clean Architecture boundaries with a dependency-free Core project
- Provider adapter pattern for external LLM APIs
- Strategy pattern for request routing
- Ordered fallback orchestration
- Circuit breaker design
- Sliding-window error-rate tracking
- P95 latency tracking
- Thread-safe in-memory state with C# `Lock`
- ASP.NET Core Minimal APIs
- `IHttpClientFactory` typed clients
- Background health checking with staggered startup
- OpenAPI and Scalar API reference UI
- Unit tests with xUnit + NSubstitute
- Integration tests with `WebApplicationFactory`
- Operational tradeoff documentation

## Architecture

```text
Client / Scalar UI / .http file
  |
  | POST /v1/chat
  v
+-------------------+
| LlmRouter.Api     |
| Minimal API DTOs  |
+---------+---------+
          |
          | ChatRequest domain model
          v
+-------------------+        +---------------------------+
| ChatService       |------->| ProviderRouter            |
| orchestration     |        | model filter              |
| ordered fallback  |        | circuit filter            |
+---------+---------+        | strategy ordering         |
          |                  +-------------+-------------+
          |                                |
          | success/failure                | health snapshots
          v                                v
+-------------------+        +---------------------------+
| IHealthTracker    |<-------| InMemoryHealthTracker     |
| RecordSuccess     |        | 5-min sliding windows     |
| RecordFailure     |        | P95 latency, circuit      |
+---------+---------+        +---------------------------+
          ^
          |
+---------+---------+
| HealthChecker     |
| staggered polling |
+---------+---------+
          |
          v
+--------------------+    +----------------+    +----------------+
| AnthropicProvider  |    | OpenAiProvider |    | GeminiProvider |
| wire/auth mapping  |    | wire/auth map  |    | parts mapping  |
+--------------------+    +----------------+    +----------------+
```

## Tech stack

- .NET 10 / ASP.NET Core Minimal APIs
- C# with nullable reference types enabled
- Native ASP.NET Core OpenAPI
- Scalar API Reference UI
- `IHttpClientFactory`
- ASP.NET Core health checks
- xUnit + NSubstitute
- `WebApplicationFactory` integration tests

## Project structure

```text
LlmRouter/
├── LlmRouter.sln
├── NuGet.config
├── README.md
├── LICENSE
├── .env.example
├── src/
│   ├── LlmRouter.Core/            # Pure domain; no external NuGet dependencies
│   ├── LlmRouter.Infrastructure/  # Provider adapters + background health checks
│   └── LlmRouter.Api/             # Minimal API, DI, DTOs, OpenAPI/Scalar
└── tests/
    └── LlmRouter.Tests/           # Unit + integration tests
```

## Key design decisions and tradeoffs

### Strategy pattern for routing

`ProviderRouter` delegates ordering to internal routing strategy implementations:

- `LeastLatency` orders by observed P95 latency.
- `RoundRobin` rotates with `Interlocked.Increment`.
- `PriorityWithFallback` preserves DI registration order.

**Tradeoff:** more classes than a single switch statement, but strategies are isolated and testable.

### Router returns an ordered list, not one provider

`IProviderRouter.Resolve` returns `IReadOnlyList<ILlmProvider>`. `ChatService` owns fallback execution.

**Tradeoff:** the caller has slightly more responsibility, but routing policy and retry/fallback behavior remain separate and easy to test.

### Circuit breaker thresholds

The in-memory breaker opens when error rate is greater than 50% with at least 5 requests in the 5-minute window. It moves to `HalfOpen` after 30 seconds and closes after one successful half-open request.

**Tradeoff:** simple and explainable. A production platform would likely tune thresholds per provider/model and include quota, cost, and tenant signals.

### Thread-safety and multi-instance limitation

`InMemoryHealthTracker` uses C# `Lock` objects around per-provider critical sections.

**Tradeoff:** fast and dependency-free for a single process. Multi-instance deployments should replace it with Redis or another shared state store.

### Staggered health check startup

`HealthCheckerService` delays provider `i` by `i * 2000ms` before first polling.

**Tradeoff:** avoids a startup thundering herd, but the final provider's first signal is delayed by a few seconds.

## Running locally

### Prerequisites

- .NET SDK 10+
- At least one provider API key

### Configuration

Prefer environment variables or user secrets for local development. Never commit real API keys.

```powershell
$env:GEMINI_API_KEY = "your-gemini-key"
$env:OPENAI_API_KEY = "your-openai-key"
$env:ANTHROPIC_API_KEY = "your-anthropic-key"
```

There is also an `.env.example` file documenting the expected names.

### Build and test

```powershell
dotnet restore .\LlmRouter.sln
dotnet build .\LlmRouter.sln --no-restore
dotnet test .\LlmRouter.sln --no-build
```

### Run the API

```powershell
dotnet run --project .\src\LlmRouter.Api\LlmRouter.Api.csproj
```

Visual Studio launch profiles use:

- `https://localhost:7225`
- `http://localhost:5136`

## Trying the API

### Chat request body reference

`POST /v1/chat` accepts a provider-neutral JSON body. Clients never send provider-specific payloads; each adapter translates this contract to the target provider.

| Property | Required | Example | Meaning |
|---|---:|---|---|
| `model` | Yes | `"gemini-2.5-flash"` | Logical model requested by the client. The router uses it to filter providers whose `SupportedModels` contain that value. |
| `messages` | Yes | `[{"role":"user","content":"Hello"}]` | Conversation messages in a provider-neutral format. The adapter converts them to each provider's wire format. |
| `messages[].role` | Yes | `"user"` | Message role. Common values are `user`, `assistant`, and `system`. Gemini maps assistant-style responses to `model` internally. |
| `messages[].content` | Yes | `"Explain this in 3 bullets"` | Text content sent to the model. |
| `maxTokens` | No | `200` | Upper bound for generated completion tokens when the provider supports it. If omitted, providers use their adapter default. |
| `temperature` | No | `0.2` | Sampling randomness. Lower values are more deterministic; higher values are more creative. The API validates `0 <= temperature <= 2`. |
| `strategy` | No | `"PriorityWithFallback"` | Routing strategy. Defaults to `LeastLatency` when omitted. Supported values: `LeastLatency`, `RoundRobin`, `PriorityWithFallback`. |

Example:

```json
{
  "model": "gemini-2.5-flash",
  "messages": [
    {
      "role": "user",
      "content": "Explain in three concise bullets what problem a multi-provider LLM router solves."
    }
  ],
  "maxTokens": 200,
  "temperature": 0.2,
  "strategy": "PriorityWithFallback"
}
```

Strategy behavior:

- `LeastLatency`: tries providers ordered by observed P95 latency.
- `RoundRobin`: rotates the first provider across compatible candidates.
- `PriorityWithFallback`: preserves DI registration order: Anthropic, OpenAI, Gemini.

### Option 1: Scalar UI

Run the API and open:

```text
https://localhost:7225/scalar/v1
```

The raw OpenAPI document is available at:

```text
https://localhost:7225/openapi/v1.json
```

### Option 2: Visual Studio `.http` file

Open and run requests from:

```text
src/LlmRouter.Api/LlmRouter.Api.http
```

### Option 3: PowerShell smoke test with Gemini

Windows PowerShell 5.1 may send non-UTF-8 request bodies when text contains accents. This example explicitly sends UTF-8 bytes:

```powershell
$body = @{ model = "gemini-2.5-flash"; messages = @( @{ role = "user"; content = "Explain in three concise bullets what problem a multi-provider LLM router solves." } ); maxTokens = 200; temperature = 0.2; strategy = "PriorityWithFallback" } | ConvertTo-Json -Depth 5; $utf8Body = [System.Text.Encoding]::UTF8.GetBytes($body); Invoke-RestMethod -Uri https://localhost:7225/v1/chat -Method Post -ContentType "application/json; charset=utf-8" -Body $utf8Body
```

Sample response shape:

```json
{
  "id": "c75008500d284c2285a6533354153369",
  "model": "gemini-2.5-flash",
  "provider": "Gemini",
  "content": "A multi-provider LLM router solves...",
  "usage": {
    "promptTokens": 18,
    "completionTokens": 254,
    "totalTokens": 272
  },
  "latencyMs": 8233
}
```

### Provider health

```powershell
Invoke-RestMethod https://localhost:7225/health/providers | Format-Table
```

Example after a few real Gemini requests:

```text
providerName isHealthy p95LatencyMs errorRateLast5Min circuitState
------------ --------- ------------ ----------------- ------------
Anthropic        False            0                 0 Closed
Gemini            True          948              0.14 Closed
OpenAI           False            0                 0 Closed
```

`Anthropic` and `OpenAI` are expected to be unhealthy if their keys are not configured.

## API behavior

- `POST /v1/chat`
  - `200` with `ChatResponse` when a provider succeeds.
  - `400` for validation failures, malformed JSON, or unsupported model.
  - `503` ProblemDetails when every resolved provider fails.
- `GET /health/providers`
  - `200` with `ProviderHealth[]`.
- `GET /health`
  - ASP.NET Core built-in health checks.
- `GET /openapi/v1.json`
  - OpenAPI document.
- `GET /scalar/v1`
  - Interactive API reference UI.

## Provider priority

DI registration order is the explicit priority for `PriorityWithFallback`:

1. AnthropicProvider
2. OpenAiProvider
3. GeminiProvider

Current Gemini chat model aliases supported by the adapter include:

- `gemini-2.5-flash`
- `gemini-2.5-pro`
- `gemini-2.0-flash`
- `gemini-2.0-flash-lite`
- `gemini-flash-latest`
- `gemini-pro-latest`

If Google returns `429 Too Many Requests`, the router is working and the provider is rejecting the call because of quota/rate limits. If Google returns `404 Not Found`, the requested model is not available for the API key/project/version.

## Known limitations

- Health state is in-memory and not shared across instances.
- No authentication or per-client authorization yet.
- No request queueing or retry budget.
- No streaming responses yet.
- No persistent metrics/tracing store.
- No cost accounting per provider/model.
- Provider adapters implement only the chat-style surface needed by this project.

## Publication and security notes

This repository is intended to be safe for public GitHub publication:

- Real provider API keys must be supplied through environment variables, user secrets, or a local-only configuration file.
- `.env`, local appsettings variants, build output, logs, and IDE state are ignored by `.gitignore`.
- `.env.example` documents required variable names without storing secrets.
- The CI pipeline restores, builds, and tests the solution without requiring any real provider credentials.

Before pushing publicly, run a final secret check:

```powershell
git grep -n "AIza\|sk-\|ANTHROPIC_API_KEY\|OPENAI_API_KEY\|GEMINI_API_KEY" -- . ':!README.md' ':!.env.example'
```

The command may show variable names in source or documentation; it should never show real key values.
## Tradeoffs and future work

| Decision | Tradeoff | Future improvement |
|---|---|---|
| In-memory health state | Lost on restart and not shared across replicas | Redis-backed state for multi-instance deployments |
| Fixed 30s poll interval | May be stale or too chatty | Adaptive interval based on error rate and traffic |
| Health check = real provider call | Consumes API quota | Dedicated probes or low-cost model checks |
| No request queueing | Simple and low latency | Async queue + retry budget for bursty traffic |
| No authentication/authorization | Easy local demo | API keys, OAuth/JWT, per-client quotas |
| No streaming responses | Simpler abstraction | SSE/chunked streaming adapters |
| No cost tracking | Keeps domain focused | Token-cost accounting by provider/model |
| No admin UI | API-only surface | Operator dashboard for health and routing controls |

## Test coverage

The test suite covers:

- Provider filtering by model support
- Circuit-open provider exclusion
- Least-latency ordering
- Round-robin rotation
- Unsupported model failures
- All-open circuit fallback behavior
- Circuit open/half-open/closed transitions
- P95 latency calculation
- ChatService success, fallback, all-fail, cancellation, and provider-aware diagnostics
- Gemini model support
- Minimal API integration behavior for chat, provider health, OpenAPI, and invalid JSON handling

## License

MIT. See [LICENSE](LICENSE).


