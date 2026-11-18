using System.ComponentModel.DataAnnotations;
using LlmRouter.Core.Models;
using LlmRouter.Core.Routing;

namespace LlmRouter.Infrastructure.Configuration;

/// <summary>
/// Configuration for external LLM provider adapters and health probing.
/// </summary>
public sealed class ProvidersOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Providers";

    /// <summary>Per-provider attempt timeout in seconds.</summary>
    [Range(1, 300)]
    public int RequestTimeoutSeconds { get; set; } = 30;

    /// <summary>Enables background health probing.</summary>
    public bool HealthCheckEnabled { get; set; } = true;

    /// <summary>Interval in seconds between background health checks per provider.</summary>
    [Range(5, 3600)]
    public int HealthCheckIntervalSeconds { get; set; } = 30;

    /// <summary>Anthropic adapter settings.</summary>
    public ProviderEndpointOptions Anthropic { get; set; } = new() { BaseUrl = "https://api.anthropic.com" };

    /// <summary>OpenAI adapter settings.</summary>
    public ProviderEndpointOptions OpenAI { get; set; } = new() { BaseUrl = "https://api.openai.com" };

    /// <summary>Google Gemini adapter settings.</summary>
    public ProviderEndpointOptions Gemini { get; set; } = new() { BaseUrl = "https://generativelanguage.googleapis.com" };
}

/// <summary>
/// Endpoint and credential settings for a single LLM provider.
/// </summary>
public sealed class ProviderEndpointOptions
{
    /// <summary>Base API URL without trailing slash.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Provider API key (prefer environment variables or user secrets).</summary>
    public string ApiKey { get; set; } = string.Empty;
}

/// <summary>
/// Configuration for model aliases, cross-provider fallback chains, and pricing.
/// </summary>
public sealed class RoutingOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Routing";

    /// <summary>Whether built-in default aliases, fallbacks, and pricing should be merged.</summary>
    public bool IncludeDefaults { get; set; } = true;

    /// <summary>Custom logical aliases mapped to ordered provider/model targets.</summary>
    public Dictionary<string, List<ModelTargetConfig>> Aliases { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom fallback chains mapped from concrete model names.</summary>
    public Dictionary<string, List<ModelTargetConfig>> Fallbacks { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Custom token pricing overrides keyed by "Model" or "Provider:Model".</summary>
    public Dictionary<string, ModelPricingConfig> Pricing { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Converts configuration POCO into Core catalog options.</summary>
    public ModelRoutingCatalogOptions ToCatalogOptions()
    {
        return new ModelRoutingCatalogOptions
        {
            IncludeDefaults = IncludeDefaults,
            Aliases = Aliases.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<ModelTarget>)kvp.Value.Select(t => new ModelTarget(t.Provider, t.Model)).ToList(),
                StringComparer.OrdinalIgnoreCase),
            Fallbacks = Fallbacks.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<ModelTarget>)kvp.Value.Select(t => new ModelTarget(t.Provider, t.Model)).ToList(),
                StringComparer.OrdinalIgnoreCase),
            Pricing = Pricing.ToDictionary(
                kvp => kvp.Key,
                kvp => new ModelPricing(kvp.Value.InputPerMillionTokensUsd, kvp.Value.OutputPerMillionTokensUsd),
                StringComparer.OrdinalIgnoreCase)
        };
    }
}

/// <summary>Configuration POCO for a single provider/model target.</summary>
public sealed class ModelTargetConfig
{
    /// <summary>Provider name.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Concrete model identifier.</summary>
    public string Model { get; set; } = string.Empty;
}

/// <summary>Configuration POCO for per-million-token pricing.</summary>
public sealed class ModelPricingConfig
{
    /// <summary>USD cost per 1M prompt tokens.</summary>
    public decimal InputPerMillionTokensUsd { get; set; }

    /// <summary>USD cost per 1M completion tokens.</summary>
    public decimal OutputPerMillionTokensUsd { get; set; }
}

/// <summary>
/// Configuration for health state persistence backend.
/// </summary>
public sealed class StateStoreOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "StateStore";

    /// <summary>Backend provider: InMemory or Redis.</summary>
    public string Provider { get; set; } = "InMemory";

    /// <summary>Key prefix used in Redis.</summary>
    public string KeyPrefix { get; set; } = "llmrouter:health";
}

/// <summary>
/// Enables deterministic simulated providers for local/Docker demos without paid API keys.
/// </summary>
public sealed class DemoModeOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "DemoMode";

    /// <summary>When true, simulated Anthropic, OpenAI, and Gemini providers are registered.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base latency in ms for simulated Anthropic responses.</summary>
    public int AnthropicLatencyMs { get; set; } = 210;

    /// <summary>Base latency in ms for simulated OpenAI responses.</summary>
    public int OpenAiLatencyMs { get; set; } = 135;

    /// <summary>Base latency in ms for simulated Gemini responses.</summary>
    public int GeminiLatencyMs { get; set; } = 85;
}
