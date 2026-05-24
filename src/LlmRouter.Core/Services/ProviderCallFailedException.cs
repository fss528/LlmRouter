using System.Net;

namespace LlmRouter.Core.Services;

/// <summary>
/// Wraps a provider execution failure with provider identity and, when available, HTTP status code.
/// </summary>
public sealed class ProviderCallFailedException : Exception
{
    /// <summary>
    /// Creates a provider-aware exception for diagnostics and API error details.
    /// </summary>
    public ProviderCallFailedException(string providerName, Exception innerException)
        : base(CreateMessage(providerName, innerException), innerException)
    {
        ProviderName = providerName;
        StatusCode = innerException is HttpRequestException httpRequestException
            ? httpRequestException.StatusCode
            : null;
    }

    /// <summary>
    /// Provider that failed.
    /// </summary>
    public string ProviderName { get; }

    /// <summary>
    /// HTTP status code returned by the provider, when available.
    /// </summary>
    public HttpStatusCode? StatusCode { get; }

    private static string CreateMessage(string providerName, Exception innerException)
    {
        if (innerException is HttpRequestException { StatusCode: HttpStatusCode statusCode })
        {
            return $"{providerName} failed with HTTP {(int)statusCode} {statusCode}.";
        }

        return $"{providerName} failed: {innerException.Message}";
    }
}
