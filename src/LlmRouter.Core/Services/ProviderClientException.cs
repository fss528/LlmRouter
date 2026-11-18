using System.Net;
using LlmRouter.Core.Models;

namespace LlmRouter.Core.Services;

/// <summary>
/// Represents a non-retriable client request error rejected by a downstream provider
/// (for example HTTP 400 invalid prompt or context length exceeded).
/// </summary>
public sealed class ProviderClientException : Exception
{
    /// <summary>
    /// Creates a non-retriable client exception without tripping provider circuit breakers.
    /// </summary>
    public ProviderClientException(string providerName, HttpStatusCode statusCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ProviderName = providerName;
        StatusCode = statusCode;
        ErrorKind = ProviderErrorKind.ClientError;
    }

    /// <summary>Provider that rejected the request payload.</summary>
    public string ProviderName { get; }

    /// <summary>HTTP status code returned by the provider.</summary>
    public HttpStatusCode StatusCode { get; }

    /// <summary>Error category.</summary>
    public ProviderErrorKind ErrorKind { get; }
}
