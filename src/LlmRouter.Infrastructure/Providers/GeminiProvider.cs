using System.Net.Http.Json;
using System.Text.Json;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using Microsoft.Extensions.Configuration;

namespace LlmRouter.Infrastructure.Providers;

/// <summary>
/// Google Gemini generateContent adapter. Translates provider-neutral messages to Gemini contents/parts.
/// </summary>
public sealed class GeminiProvider : ILlmProvider
{
    private const string ProviderName = "Gemini";
    private const string GenerateContentEndpointTemplate = "https://generativelanguage.googleapis.com/v1beta/models/{0}:generateContent?key={1}";
    private const string ModelsEndpointTemplate = "https://generativelanguage.googleapis.com/v1beta/models?key={0}";
    private const string ApiKeyEnvironmentVariable = "GEMINI_API_KEY";
    private const string ApiKeyConfigurationKey = "Providers:Gemini:ApiKey";
    private const string Gemini25Flash = "gemini-2.5-flash";
    private const string Gemini25Pro = "gemini-2.5-pro";
    private const string Gemini20Flash = "gemini-2.0-flash";
    private const string Gemini20Flash001 = "gemini-2.0-flash-001";
    private const string Gemini20FlashLite = "gemini-2.0-flash-lite";
    private const string Gemini20FlashLite001 = "gemini-2.0-flash-lite-001";
    private const string GeminiFlashLatest = "gemini-flash-latest";
    private const string GeminiFlashLiteLatest = "gemini-flash-lite-latest";
    private const string GeminiProLatest = "gemini-pro-latest";

    private static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal)
    {
        Gemini25Flash,
        Gemini25Pro,
        Gemini20Flash,
        Gemini20Flash001,
        Gemini20FlashLite,
        Gemini20FlashLite001,
        GeminiFlashLatest,
        GeminiFlashLiteLatest,
        GeminiProLatest
    };

    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the Gemini provider adapter.
    /// </summary>
    public GeminiProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedModels => Models;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        string apiKey = GetRequiredApiKey();
        string endpoint = string.Format(System.Globalization.CultureInfo.InvariantCulture, GenerateContentEndpointTemplate, request.Model, Uri.EscapeDataString(apiKey));
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint);
        httpRequest.Content = JsonContent.Create(new
        {
            contents = request.Messages.Select(message => new
            {
                role = NormalizeRole(message.Role),
                parts = new[]
                {
                    new { text = message.Content }
                }
            })
        });

        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string content = root.GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? string.Empty;

        JsonElement usage = root.GetProperty("usageMetadata");
        int promptTokens = usage.TryGetProperty("promptTokenCount", out JsonElement promptTokensElement) ? promptTokensElement.GetInt32() : 0;
        int completionTokens = usage.TryGetProperty("candidatesTokenCount", out JsonElement completionTokensElement) ? completionTokensElement.GetInt32() : 0;

        return new ChatResponse(Guid.NewGuid().ToString("N"), request.Model, Name, content, new TokenUsage(promptTokens, completionTokens), 0);
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        string endpoint = string.Format(System.Globalization.CultureInfo.InvariantCulture, ModelsEndpointTemplate, Uri.EscapeDataString(apiKey));
        using HttpResponseMessage httpResponse = await _httpClient.GetAsync(endpoint, ct).ConfigureAwait(false);
        return httpResponse.IsSuccessStatusCode;
    }

    private string GetRequiredApiKey()
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Missing Gemini API key. Set {ApiKeyEnvironmentVariable} or {ApiKeyConfigurationKey}.");
        }

        return apiKey;
    }

    private string? GetConfiguredApiKey()
    {
        return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) ?? _configuration[ApiKeyConfigurationKey];
    }

    private static string NormalizeRole(string role)
    {
        return role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
    }
}
