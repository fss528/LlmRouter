namespace LlmRouter.Api.Configuration;

/// <summary>
/// Centralized API route constants to keep endpoint paths reviewable and avoid scattered literals.
/// </summary>
public static class ApiRoutes
{
    /// <summary>Version 1 API route prefix.</summary>
    public const string V1Prefix = "/v1";

    /// <summary>Provider-neutral chat completion endpoint route.</summary>
    public const string Chat = "/chat";

    /// <summary>Server-Sent Events streaming chat endpoint route.</summary>
    public const string ChatStream = "/chat/stream";

    /// <summary>OpenAI-compatible chat completions endpoint route.</summary>
    public const string OpenAiChatCompletions = "/chat/completions";

    /// <summary>Model catalog, aliases, and pricing discovery route.</summary>
    public const string Models = "/models";

    /// <summary>Provider health endpoint route.</summary>
    public const string ProviderHealth = "/health/providers";

    /// <summary>Built-in ASP.NET Core health endpoint route.</summary>
    public const string Health = "/health";

    /// <summary>OpenAPI document endpoint route.</summary>
    public const string OpenApi = "/openapi/{documentName}.json";
}
