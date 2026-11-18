using System.Net;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Services;

/// <summary>
/// Wraps a provider execution failure with provider identity, error classification, and HTTP metadata.
/// </summary>
public sealed class ProviderCallFailedException : Exception
{
    /// <summary>
    /// Creates a provider-aware exception for diagnostics and API error details.
    /// </summary>
    public ProviderCallFailedException(
        string providerName,
        Exception innerException,
        ProviderErrorKind errorKind = ProviderErrorKind.NetworkOrUnexpected,
        TimeSpan? retryAfter = null)
        : base(CreateMessage(providerName, innerException), innerException)
    {
        ProviderName = providerName;
        StatusCode = innerException is HttpRequestException httpRequestException
            ? httpRequestException.StatusCode
            : null;
        ErrorKind = errorKind;
        RetryAfter = retryAfter;
    }

    /// <summary>Provider that failed.</summary>
    public string ProviderName { get; }

    /// <summary>HTTP status code returned by the provider, when available.</summary>
    public HttpStatusCode? StatusCode { get; }

    /// <summary>Classified failure category.</summary>
    public ProviderErrorKind ErrorKind { get; }

    /// <summary>Optional Retry-After hint when rate-limited.</summary>
    public TimeSpan? RetryAfter { get; }

    /// <summary>Whether the router should continue falling back to other candidates.</summary>
    public bool IsTransient => ErrorKind != ProviderErrorKind.ClientError;

    private static string CreateMessage(string providerName, Exception innerException)
    {
        if (innerException is HttpRequestException { StatusCode: HttpStatusCode statusCode })
        {
            return $"{providerName} failed with HTTP {(int)statusCode} {statusCode}.";
        }

        return $"{providerName} failed: {innerException.Message}";
    }
}
