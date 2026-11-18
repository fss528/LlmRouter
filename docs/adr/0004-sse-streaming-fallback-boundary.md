# ADR 0004: SSE Streaming Fallback Boundary and TTFT Instrumentation

## Status
Accepted

## Context
Interactive LLM applications rely on Server-Sent Events (SSE) streaming to minimize perceived latency via Time-To-First-Token (TTFT). However, unlike buffered completions, once an HTTP 200 SSE stream has emitted tokens from Provider A to the client, switching mid-stream to Provider B would corrupt the response with mixed prose and inconsistent conversation state.

## Decision
1. In `ChatEndpoints.StreamChatAsync`, advance the `IAsyncEnumerator<ChatStreamChunk>` before committing HTTP `200 text/event-stream` so validation errors, unsupported models, or total provider outages still return proper `400`/`503` ProblemDetails responses.
2. In `ChatService.CompleteStreamAsync`, transparently fail over to the next candidate provider **only if the failure occurs before the first chunk is yielded**.
3. If an upstream failure occurs after the first chunk has been streamed, record the failure on `IHealthTracker` and terminate the stream instead of splicing a second provider's output.
4. Measure and emit both `TimeToFirstTokenMs` and total completion latency in the final `done` SSE event and OpenTelemetry histogram `llm_router.provider.ttft.ms`.

## Consequences
- Clients benefit from automatic failover on connection failures, rate limits (`429`), and pre-first-token timeouts.
- Streamed responses never mix outputs from two different models or vendors.
