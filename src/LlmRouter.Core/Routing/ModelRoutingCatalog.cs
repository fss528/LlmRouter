using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Routing;

/// <summary>
/// Configuration object for <see cref="ModelRoutingCatalog"/>.
/// </summary>
public sealed class ModelRoutingCatalogOptions
{
    /// <summary>Logical aliases and ordered fallback targets.</summary>
    public IDictionary<string, IReadOnlyList<ModelTarget>> Aliases { get; init; } =
        new Dictionary<string, IReadOnlyList<ModelTarget>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Fallback chains keyed by concrete model name.</summary>
    public IDictionary<string, IReadOnlyList<ModelTarget>> Fallbacks { get; init; } =
        new Dictionary<string, IReadOnlyList<ModelTarget>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Token pricing table keyed by "Provider:Model" or "Model".</summary>
    public IDictionary<string, ModelPricing> Pricing { get; init; } =
        new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

    /// <summary>When true, built-in default aliases, fallbacks, and pricing are merged with custom overrides.</summary>
    public bool IncludeDefaults { get; init; } = true;
}

/// <summary>
/// Default in-memory catalog providing logical model tiers, cross-provider fallback rules, and token pricing.
/// </summary>
public sealed class ModelRoutingCatalog : IModelCatalog
{
    private readonly Dictionary<string, IReadOnlyList<ModelTarget>> _aliases;
    private readonly Dictionary<string, IReadOnlyList<ModelTarget>> _fallbacks;
    private readonly Dictionary<string, ModelPricing> _pricing;

    /// <summary>
    /// Creates a catalog with default aliases, cross-provider fallback chains, and reference model pricing.
    /// </summary>
    public ModelRoutingCatalog(ModelRoutingCatalogOptions? options = null)
    {
        options ??= new ModelRoutingCatalogOptions();

        _aliases = options.IncludeDefaults
            ? CreateDefaultAliases()
            : new Dictionary<string, IReadOnlyList<ModelTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, IReadOnlyList<ModelTarget> targets) in options.Aliases)
        {
            _aliases[key] = targets;
        }

        _fallbacks = options.IncludeDefaults
            ? CreateDefaultFallbacks()
            : new Dictionary<string, IReadOnlyList<ModelTarget>>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, IReadOnlyList<ModelTarget> targets) in options.Fallbacks)
        {
            _fallbacks[key] = targets;
        }

        _pricing = options.IncludeDefaults
            ? CreateDefaultPricing()
            : new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase);

        foreach ((string key, ModelPricing price) in options.Pricing)
        {
            _pricing[key] = price;
        }
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelTarget> GetTargets(string requestedModel)
    {
        if (string.IsNullOrWhiteSpace(requestedModel))
        {
            return [];
        }

        if (_aliases.TryGetValue(requestedModel, out IReadOnlyList<ModelTarget>? aliasTargets))
        {
            return aliasTargets;
        }

        if (_fallbacks.TryGetValue(requestedModel, out IReadOnlyList<ModelTarget>? fallbackTargets))
        {
            return fallbackTargets;
        }

        return [];
    }

    /// <inheritdoc />
    public ModelPricing? GetPricing(string providerName, string model)
    {
        if (_pricing.TryGetValue($"{providerName}:{model}", out ModelPricing? scoped))
        {
            return scoped;
        }

        return _pricing.GetValueOrDefault(model);
    }

    /// <inheritdoc />
    public decimal? EstimateCost(string providerName, string model, TokenUsage usage)
    {
        ModelPricing? pricing = GetPricing(providerName, model);
        if (pricing is null)
        {
            return null;
        }

        decimal inputCost = (usage.PromptTokens / 1_000_000m) * pricing.InputPerMillionTokensUsd;
        decimal outputCost = (usage.CompletionTokens / 1_000_000m) * pricing.OutputPerMillionTokensUsd;
        return decimal.Round(inputCost + outputCost, 8, MidpointRounding.AwayFromZero);
    }

    /// <inheritdoc />
    public IReadOnlyList<ModelAliasInfo> GetAliases()
    {
        return _aliases
            .OrderBy(entry => entry.Key, StringComparer.OrdinalIgnoreCase)
            .Select(entry => new ModelAliasInfo(entry.Key, entry.Value))
            .ToList();
    }

    private static Dictionary<string, IReadOnlyList<ModelTarget>> CreateDefaultAliases()
    {
        return new Dictionary<string, IReadOnlyList<ModelTarget>>(StringComparer.OrdinalIgnoreCase)
        {
            ["fast"] =
            [
                new ModelTarget("Gemini", "gemini-2.5-flash"),
                new ModelTarget("OpenAI", "gpt-4o-mini"),
                new ModelTarget("Anthropic", "claude-haiku-4-5-20251001")
            ],
            ["balanced"] =
            [
                new ModelTarget("OpenAI", "gpt-4o"),
                new ModelTarget("Anthropic", "claude-sonnet-4-20250514"),
                new ModelTarget("Gemini", "gemini-2.5-flash")
            ],
            ["reasoning"] =
            [
                new ModelTarget("Anthropic", "claude-opus-4-20250514"),
                new ModelTarget("Gemini", "gemini-2.5-pro"),
                new ModelTarget("OpenAI", "gpt-4o")
            ],
            ["chat-default"] =
            [
                new ModelTarget("Gemini", "gemini-2.5-flash"),
                new ModelTarget("OpenAI", "gpt-4o-mini"),
                new ModelTarget("Anthropic", "claude-haiku-4-5-20251001")
            ]
        };
    }

    private static Dictionary<string, IReadOnlyList<ModelTarget>> CreateDefaultFallbacks()
    {
        return new Dictionary<string, IReadOnlyList<ModelTarget>>(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-4o"] =
            [
                new ModelTarget("OpenAI", "gpt-4o"),
                new ModelTarget("Anthropic", "claude-sonnet-4-20250514"),
                new ModelTarget("Gemini", "gemini-2.5-pro")
            ],
            ["gpt-4o-mini"] =
            [
                new ModelTarget("OpenAI", "gpt-4o-mini"),
                new ModelTarget("Gemini", "gemini-2.5-flash"),
                new ModelTarget("Anthropic", "claude-haiku-4-5-20251001")
            ],
            ["claude-sonnet-4-20250514"] =
            [
                new ModelTarget("Anthropic", "claude-sonnet-4-20250514"),
                new ModelTarget("OpenAI", "gpt-4o"),
                new ModelTarget("Gemini", "gemini-2.5-pro")
            ],
            ["claude-haiku-4-5-20251001"] =
            [
                new ModelTarget("Anthropic", "claude-haiku-4-5-20251001"),
                new ModelTarget("Gemini", "gemini-2.5-flash"),
                new ModelTarget("OpenAI", "gpt-4o-mini")
            ],
            ["gemini-2.5-flash"] =
            [
                new ModelTarget("Gemini", "gemini-2.5-flash"),
                new ModelTarget("OpenAI", "gpt-4o-mini"),
                new ModelTarget("Anthropic", "claude-haiku-4-5-20251001")
            ],
            ["gemini-2.5-pro"] =
            [
                new ModelTarget("Gemini", "gemini-2.5-pro"),
                new ModelTarget("Anthropic", "claude-sonnet-4-20250514"),
                new ModelTarget("OpenAI", "gpt-4o")
            ]
        };
    }

    private static Dictionary<string, ModelPricing> CreateDefaultPricing()
    {
        return new Dictionary<string, ModelPricing>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI reference pricing (USD per 1M tokens)
            ["gpt-4o"] = new ModelPricing(2.50m, 10.00m),
            ["gpt-4o-mini"] = new ModelPricing(0.15m, 0.60m),
            ["gpt-4-turbo"] = new ModelPricing(10.00m, 30.00m),

            // Anthropic reference pricing (USD per 1M tokens)
            ["claude-sonnet-4-20250514"] = new ModelPricing(3.00m, 15.00m),
            ["claude-opus-4-20250514"] = new ModelPricing(15.00m, 75.00m),
            ["claude-haiku-4-5-20251001"] = new ModelPricing(0.80m, 4.00m),

            // Gemini reference pricing (USD per 1M tokens)
            ["gemini-2.5-flash"] = new ModelPricing(0.15m, 0.60m),
            ["gemini-2.5-pro"] = new ModelPricing(1.25m, 10.00m),
            ["gemini-2.0-flash"] = new ModelPricing(0.10m, 0.40m),
            ["gemini-2.0-flash-001"] = new ModelPricing(0.10m, 0.40m),
            ["gemini-2.0-flash-lite"] = new ModelPricing(0.075m, 0.30m),
            ["gemini-2.0-flash-lite-001"] = new ModelPricing(0.075m, 0.30m),
            ["gemini-flash-latest"] = new ModelPricing(0.15m, 0.60m),
            ["gemini-flash-lite-latest"] = new ModelPricing(0.075m, 0.30m),
            ["gemini-pro-latest"] = new ModelPricing(1.25m, 10.00m)
        };
    }
}
