namespace LlmRouter.Core.Models;

/// <summary>
/// Provider-agnostic chat completion request accepted by the router.
/// </summary>
/// <param name="Model">Logical model name requested by the client.</param>
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
/// <param name="Model">Model that served the request.</param>
/// <param name="Provider">Provider that actually served the request.</param>
/// <param name="Content">Completion content.</param>
/// <param name="Usage">Token usage reported by the provider.</param>
/// <param name="LatencyMs">End-to-end provider call latency in milliseconds.</param>
public sealed record ChatResponse(
    string Id,
    string Model,
    string Provider,
    string Content,
    TokenUsage Usage,
    long LatencyMs);

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
    /// <summary>Prefer the provider with the lowest observed P95 latency.</summary>
    LeastLatency,

    /// <summary>Rotate across candidates using a thread-safe counter.</summary>
    RoundRobin,

    /// <summary>Preserve dependency-injection registration order as explicit priority.</summary>
    PriorityWithFallback
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
public sealed record ProviderHealth(
    string ProviderName,
    bool IsHealthy,
    double P95LatencyMs,
    double ErrorRateLast5Min,
    DateTimeOffset LastChecked,
    CircuitState CircuitState);

/// <summary>
/// Circuit-breaker state for a provider.
/// </summary>
public enum CircuitState
{
    /// <summary>Provider is eligible for normal traffic.</summary>
    Closed,

    /// <summary>Provider is temporarily excluded from normal traffic.</summary>
    Open,

    /// <summary>Provider is eligible for a probe request after the open interval elapsed.</summary>
    HalfOpen
}
