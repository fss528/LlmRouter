namespace LlmRouter.Core.Models;

/// <summary>
/// Provider-agnostic chat completion request accepted by the router.
/// </summary>
/// <param name="Model">Concrete model name or logical tier alias (for example, fast, balanced, reasoning).</param>
/// <param name="Messages">Conversation messages in provider-neutral format.</param>
/// <param name="MaxTokens">Optional maximum number of completion tokens.</param>
/// <param name="Temperature">Optional sampling temperature.</param>
/// <param name="Strategy">Routing strategy to apply before fallback.</param>
public sealed record ChatRequest(
    string Model,
    IReadOnlyList<Message> Messages,
    int? MaxTokens = null,
    double? Temperature = null,
    RoutingStrategy Strategy = RoutingStrategy.LeastLatency);

/// <summary>
/// Provider-neutral chat message.
/// </summary>
/// <param name="Role">Role such as user, assistant, or system.</param>
/// <param name="Content">Message content.</param>
public sealed record Message(string Role, string Content);

/// <summary>
/// Provider-agnostic response returned to API clients.
/// </summary>
/// <param name="Id">Provider response identifier or generated fallback identifier.</param>
/// <param name="Model">Concrete model that served the request.</param>
/// <param name="Provider">Provider that actually served the request.</param>
/// <param name="Content">Completion content.</param>
/// <param name="Usage">Token usage reported by the provider.</param>
/// <param name="LatencyMs">End-to-end provider call latency in milliseconds.</param>
/// <param name="RequestedModel">Original model or logical alias requested by the caller.</param>
/// <param name="EstimatedCostUsd">Estimated request cost in USD calculated from token usage and model pricing.</param>
/// <param name="TimeToFirstTokenMs">Time to first token in milliseconds when available.</param>
/// <param name="Attempts">Ordered execution trace of provider attempts for this request.</param>
public sealed record ChatResponse(
    string Id,
    string Model,
    string Provider,
    string Content,
    TokenUsage Usage,
    long LatencyMs,
    string? RequestedModel = null,
    decimal? EstimatedCostUsd = null,
    long? TimeToFirstTokenMs = null,
    IReadOnlyList<ProviderAttempt>? Attempts = null);

/// <summary>
/// Records a single provider execution attempt during request orchestration.
/// </summary>
/// <param name="Provider">Provider name attempted.</param>
/// <param name="Model">Concrete model requested from that provider.</param>
/// <param name="Succeeded">Whether the attempt succeeded.</param>
/// <param name="LatencyMs">Attempt latency in milliseconds.</param>
/// <param name="FailureReason">Failure summary when the attempt failed.</param>
/// <param name="StatusCode">HTTP status code when returned by the provider.</param>
public sealed record ProviderAttempt(
    string Provider,
    string Model,
    bool Succeeded,
    long LatencyMs,
    string? FailureReason = null,
    int? StatusCode = null);

/// <summary>
/// Incremental streaming chunk emitted by a provider and enriched by the router.
/// </summary>
/// <param name="Id">Response or stream identifier.</param>
/// <param name="Model">Concrete model serving the stream.</param>
/// <param name="Provider">Provider serving the stream.</param>
/// <param name="Delta">Incremental text delta.</param>
/// <param name="IsFinal">Indicates the final terminal chunk with final usage and timing metrics.</param>
/// <param name="Usage">Token usage when reported by the provider.</param>
/// <param name="TimeToFirstTokenMs">Time to first non-empty token in milliseconds.</param>
/// <param name="LatencyMs">Total elapsed stream duration in milliseconds.</param>
/// <param name="EstimatedCostUsd">Estimated USD cost when token usage is available.</param>
public sealed record ChatStreamChunk(
    string Id,
    string Model,
    string Provider,
    string Delta,
    bool IsFinal = false,
    TokenUsage? Usage = null,
    long? TimeToFirstTokenMs = null,
    long? LatencyMs = null,
    decimal? EstimatedCostUsd = null);

/// <summary>
/// Prompt and completion token counts.
/// </summary>
/// <param name="PromptTokens">Prompt tokens consumed.</param>
/// <param name="CompletionTokens">Completion tokens generated.</param>
public sealed record TokenUsage(int PromptTokens, int CompletionTokens)
{
    /// <summary>
    /// Total token count.
    /// </summary>
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>
/// Routing policy selected by the caller.
/// </summary>
public enum RoutingStrategy
{
    /// <summary>Prefer the healthy provider with the lowest observed P95 latency.</summary>
    LeastLatency,

    /// <summary>Rotate across candidates using a thread-safe shared counter.</summary>
    RoundRobin,

    /// <summary>Preserve configured priority / catalog order as explicit preference.</summary>
    PriorityWithFallback,

    /// <summary>Prefer the candidate with the lowest estimated cost per token, breaking ties by latency.</summary>
    LowestCost
}

/// <summary>
/// Current health and circuit state for a provider.
/// </summary>
/// <param name="ProviderName">Provider name.</param>
/// <param name="IsHealthy">Last externally observed health state.</param>
/// <param name="P95LatencyMs">P95 latency calculated from recent samples.</param>
/// <param name="ErrorRateLast5Min">Sliding-window error rate between 0 and 1.</param>
/// <param name="LastChecked">Last time health was read or updated.</param>
/// <param name="CircuitState">Circuit-breaker state.</param>
/// <param name="LatencySampleCount">Number of recent latency samples included in P95 computation.</param>
public sealed record ProviderHealth(
    string ProviderName,
    bool IsHealthy,
    double P95LatencyMs,
    double ErrorRateLast5Min,
    DateTimeOffset LastChecked,
    CircuitState CircuitState,
    int LatencySampleCount = 0);

/// <summary>
/// Circuit-breaker state for a provider.
/// </summary>
public enum CircuitState
{
    /// <summary>Provider is eligible for normal traffic.</summary>
    Closed,

    /// <summary>Provider is temporarily excluded from normal traffic.</summary>
    Open,

    /// <summary>Provider is eligible for a single probe request after the open interval elapsed.</summary>
    HalfOpen
}

/// <summary>
/// Per-million-token pricing for a provider model in USD.
/// </summary>
/// <param name="InputPerMillionTokensUsd">Cost per 1M input/prompt tokens in USD.</param>
/// <param name="OutputPerMillionTokensUsd">Cost per 1M output/completion tokens in USD.</param>
public sealed record ModelPricing(
    decimal InputPerMillionTokensUsd,
    decimal OutputPerMillionTokensUsd);

/// <summary>
/// Concrete provider/model target mapped from a logical alias or fallback chain.
/// </summary>
/// <param name="ProviderName">Target provider identifier.</param>
/// <param name="Model">Concrete provider model identifier.</param>
public sealed record ModelTarget(string ProviderName, string Model);

/// <summary>
/// Publicly discoverable alias or routing target metadata.
/// </summary>
/// <param name="Alias">Logical model name or alias.</param>
/// <param name="Targets">Ordered fallback targets for this alias.</param>
public sealed record ModelAliasInfo(string Alias, IReadOnlyList<ModelTarget> Targets);

/// <summary>
/// Categorized provider error used to separate client payload mistakes from provider faults.
/// </summary>
public enum ProviderErrorKind
{
    /// <summary>Caller payload issue such as HTTP 400 or 422; does not trip provider circuit breakers.</summary>
    ClientError,

    /// <summary>Provider credentials or permission error (HTTP 401/403); eligible for cross-provider fallback.</summary>
    Authentication,

    /// <summary>Provider rate limit or quota exhaustion (HTTP 429).</summary>
    RateLimited,

    /// <summary>Provider 5xx server failure.</summary>
    ServerError,

    /// <summary>Provider attempt exceeded configured timeout.</summary>
    Timeout,

    /// <summary>Network or unexpected transport/parsing failure.</summary>
    NetworkOrUnexpected
}
