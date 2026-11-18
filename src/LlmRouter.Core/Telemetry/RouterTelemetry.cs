using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace LlmRouter.Core.Telemetry;

/// <summary>
/// Built-in OpenTelemetry-compatible diagnostics using BCL <see cref="ActivitySource"/> and <see cref="Meter"/>
/// so Core remains free of external telemetry dependencies.
/// </summary>
public static class RouterTelemetry
{
    /// <summary>ActivitySource name subscribed by OpenTelemetry tracing.</summary>
    public const string ActivitySourceName = "LlmRouter";

    /// <summary>Meter name subscribed by OpenTelemetry metrics.</summary>
    public const string MeterName = "LlmRouter";

    /// <summary>Activity source for chat routing and provider attempt spans.</summary>
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName, "1.0.0");

    /// <summary>Meter for router request, latency, fallback, token, and cost metrics.</summary>
    public static readonly Meter Meter = new(MeterName, "1.0.0");

    /// <summary>Total routed chat requests.</summary>
    public static readonly Counter<long> RequestsTotal = Meter.CreateCounter<long>(
        "llm_router.requests.total",
        unit: "{request}",
        description: "Total number of chat completion requests processed by LlmRouter.");

    /// <summary>Total provider execution attempts.</summary>
    public static readonly Counter<long> ProviderAttemptsTotal = Meter.CreateCounter<long>(
        "llm_router.provider.attempts.total",
        unit: "{attempt}",
        description: "Total number of individual provider call attempts.");

    /// <summary>Total cross-provider or multi-attempt fallbacks triggered.</summary>
    public static readonly Counter<long> FallbacksTotal = Meter.CreateCounter<long>(
        "llm_router.provider.fallbacks.total",
        unit: "{fallback}",
        description: "Number of times execution fell back from a failed provider to a subsequent candidate.");

    /// <summary>End-to-end duration of a provider attempt in milliseconds.</summary>
    public static readonly Histogram<double> ProviderDurationMs = Meter.CreateHistogram<double>(
        "llm_router.provider.duration.ms",
        unit: "ms",
        description: "Duration of provider completion calls in milliseconds.");

    /// <summary>Time to first token for streaming completions in milliseconds.</summary>
    public static readonly Histogram<double> TimeToFirstTokenMs = Meter.CreateHistogram<double>(
        "llm_router.provider.ttft.ms",
        unit: "ms",
        description: "Time to first non-empty streaming token in milliseconds.");

    /// <summary>Total tokens consumed across prompt and completion.</summary>
    public static readonly Counter<long> TokensTotal = Meter.CreateCounter<long>(
        "llm_router.tokens.total",
        unit: "{token}",
        description: "Total prompt and completion tokens consumed.");

    /// <summary>Total estimated USD cost across completed requests.</summary>
    public static readonly Counter<double> EstimatedCostUsdTotal = Meter.CreateCounter<double>(
        "llm_router.cost.usd.total",
        unit: "USD",
        description: "Cumulative estimated USD cost across served completions.");
}
