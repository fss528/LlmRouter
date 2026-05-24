using System.Net.Http.Json;
using System.Text.Json;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using Microsoft.Extensions.Configuration;

namespace LlmRouter.Infrastructure.Providers;

/// <summary>
/// Anthropic Messages API adapter. Provider-specific wire format and headers are intentionally contained here.
/// </summary>
public sealed class AnthropicProvider : ILlmProvider
{
    private const string ProviderName = "Anthropic";
    private const string ApiKeyHeaderName = "x-api-key";
    private const string VersionHeaderName = "anthropic-version";
    private const string AnthropicVersion = "2023-06-01";
    private const string MessagesEndpoint = "https://api.anthropic.com/v1/messages";
    private const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";
    private const string ApiKeyConfigurationKey = "Providers:Anthropic:ApiKey";
    private const string HealthCheckModel = "claude-haiku-4-5-20251001";
    private const int HealthCheckMaxTokens = 1;

    private static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-sonnet-4-20250514",
        "claude-opus-4-20250514",
        HealthCheckModel
    };

    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the Anthropic provider adapter.
    /// </summary>
    public AnthropicProvider(HttpClient httpClient, IConfiguration configuration)
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
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, MessagesEndpoint);
        httpRequest.Headers.Add(ApiKeyHeaderName, apiKey);
        httpRequest.Headers.Add(VersionHeaderName, AnthropicVersion);
        httpRequest.Content = JsonContent.Create(new
        {
            model = request.Model,
            max_tokens = request.MaxTokens ?? 1024,
            messages = request.Messages.Select(message => new
            {
                role = NormalizeRole(message.Role),
                content = message.Content
            })
        });

        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        string content = root.GetProperty("content")[0].GetProperty("text").GetString() ?? string.Empty;
        JsonElement usage = root.GetProperty("usage");
        int promptTokens = usage.GetProperty("input_tokens").GetInt32();
        int completionTokens = usage.GetProperty("output_tokens").GetInt32();

        return new ChatResponse(id, request.Model, Name, content, new TokenUsage(promptTokens, completionTokens), 0);
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(GetConfiguredApiKey()))
        {
            return false;
        }

        ChatRequest request = new(
            HealthCheckModel,
            [new Message("user", "ping")],
            MaxTokens: HealthCheckMaxTokens,
            Temperature: 0,
            Strategy: RoutingStrategy.PriorityWithFallback);

        ChatResponse response = await CompleteAsync(request, ct).ConfigureAwait(false);
        return !string.IsNullOrWhiteSpace(response.Content) || response.Usage.TotalTokens >= 0;
    }

    private string GetRequiredApiKey()
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Missing Anthropic API key. Set {ApiKeyEnvironmentVariable} or {ApiKeyConfigurationKey}.");
        }

        return apiKey;
    }

    private string? GetConfiguredApiKey()
    {
        return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) ?? _configuration[ApiKeyConfigurationKey];
    }

    private static string NormalizeRole(string role)
    {
        return role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
    }
}
