using System.ComponentModel.DataAnnotations;

namespace LlmRouter.Api.Configuration;

/// <summary>
/// Gateway-level authentication and rate-limiting configuration.
/// </summary>
public sealed class GatewayOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "Gateway";

    /// <summary>
    /// Optional list of accepted gateway API keys. When empty, anonymous access is allowed (ideal for local dev/tests).
    /// </summary>
    public List<string> ApiKeys { get; set; } = [];

    /// <summary>Fixed-window rate limiting policy configuration.</summary>
    public RateLimitOptions RateLimit { get; set; } = new();
}

/// <summary>
/// Rate limiter settings applied to /v1/* endpoints.
/// </summary>
public sealed class RateLimitOptions
{
    /// <summary>Enables ASP.NET Core rate limiting middleware.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Maximum requests permitted per client partition per window.</summary>
    [Range(1, 1_000_000)]
    public int PermitLimit { get; set; } = 120;

    /// <summary>Window duration in seconds.</summary>
    [Range(1, 3600)]
    public int WindowSeconds { get; set; } = 60;
}
