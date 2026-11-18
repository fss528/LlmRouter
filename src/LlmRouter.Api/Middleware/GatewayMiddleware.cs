using System.Net;
using System.Security.Cryptography;
using System.Text;
using LlmRouter.Api.Configuration;
using LlmRouter.Infrastructure.Configuration;
using LlmRouter.Infrastructure.Providers;
using Microsoft.Extensions.Options;

namespace LlmRouter.Api.Middleware;

/// <summary>
/// Enforces optional gateway API key authentication on /v1/* endpoints and applies
/// DemoMode fault-injection headers when DemoMode is explicitly enabled.
/// </summary>
public sealed class GatewayMiddleware
{
    private const string ApiKeyHeader = "X-Api-Key";
    private const string SimulateFailureHeader = "X-Simulate-Failure";
    private const string SimulateFailureStatusHeader = "X-Simulate-Failure-Status";

    private readonly RequestDelegate _next;

    /// <summary>Creates the gateway middleware.</summary>
    public GatewayMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    /// <summary>Executes authentication and demo fault-context propagation per request.</summary>
    public async Task InvokeAsync(
        HttpContext context,
        IOptionsMonitor<GatewayOptions> gatewayOptions,
        IOptionsMonitor<DemoModeOptions> demoOptions,
        ISimulatedFaultContext faultContext)
    {
        if (context.Request.Path.StartsWithSegments(ApiRoutes.V1Prefix, StringComparison.OrdinalIgnoreCase))
        {
            List<string> configuredKeys = gatewayOptions.CurrentValue.ApiKeys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();

            if (configuredKeys.Count > 0 && !IsAuthorized(context.Request, configuredKeys))
            {
                await Results.Problem(
                        title: "Unauthorized",
                        detail: "Missing or invalid gateway API key.",
                        statusCode: StatusCodes.Status401Unauthorized)
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
                return;
            }
        }

        if (demoOptions.CurrentValue.Enabled && faultContext is not null)
        {
            string? failProvider = context.Request.Headers[SimulateFailureHeader].FirstOrDefault();
            faultContext.FailProvider = string.IsNullOrWhiteSpace(failProvider) ? null : failProvider.Trim();

            if (int.TryParse(context.Request.Headers[SimulateFailureStatusHeader].FirstOrDefault(), out int statusCode) &&
                Enum.IsDefined(typeof(HttpStatusCode), statusCode))
            {
                faultContext.FailStatusCode = (HttpStatusCode)statusCode;
            }
            else
            {
                faultContext.FailStatusCode = HttpStatusCode.TooManyRequests;
            }
        }

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            if (faultContext is not null)
            {
                faultContext.FailProvider = null;
            }
        }
    }

    private static bool IsAuthorized(HttpRequest request, IReadOnlyList<string> validKeys)
    {
        string? providedKey = request.Headers[ApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(providedKey))
        {
            string? authHeader = request.Headers.Authorization.FirstOrDefault();
            if (authHeader is not null && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                providedKey = authHeader["Bearer ".Length..].Trim();
            }
        }

        if (string.IsNullOrWhiteSpace(providedKey))
        {
            return false;
        }

        byte[] providedBytes = Encoding.UTF8.GetBytes(providedKey);
        bool matched = false;
        foreach (string validKey in validKeys)
        {
            byte[] validBytes = Encoding.UTF8.GetBytes(validKey);
            if (providedBytes.Length == validBytes.Length &&
                CryptographicOperations.FixedTimeEquals(providedBytes, validBytes))
            {
                matched = true;
            }
        }

        return matched;
    }
}
