using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LlmRouter.Infrastructure.Providers;

/// <summary>
/// Anthropic Messages API adapter. Owns Anthropic wire format, headers, system prompt mapping, and SSE parsing.
/// </summary>
public sealed class AnthropicProvider : ILlmProvider
{
    private const string ProviderName = "Anthropic";
    private const string ApiKeyHeaderName = "x-api-key";
    private const string VersionHeaderName = "anthropic-version";
    private const string AnthropicVersion = "2023-06-01";
    private const string DefaultBaseUrl = "https://api.anthropic.com";
    private const string ApiKeyEnvironmentVariable = "ANTHROPIC_API_KEY";
    private const string ApiKeyConfigurationKey = "Providers:Anthropic:ApiKey";

    private static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-sonnet-4-20250514",
        "claude-opus-4-20250514",
        "claude-haiku-4-5-20251001"
    };

    private readonly string _baseUrl;
    private readonly IConfiguration? _configuration;
    private readonly string? _configuredApiKey;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the Anthropic provider adapter from strongly-typed options.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public AnthropicProvider(
        HttpClient httpClient,
        IOptions<ProvidersOptions> options,
        IConfiguration? configuration = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        ProviderEndpointOptions endpoint = options.Value.Anthropic;
        _configuredApiKey = endpoint.ApiKey;
        _baseUrl = string.IsNullOrWhiteSpace(endpoint.BaseUrl)
            ? DefaultBaseUrl
            : endpoint.BaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Creates the Anthropic provider adapter directly from <see cref="IConfiguration"/>.
    /// </summary>
    public AnthropicProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _configuredApiKey = configuration[ApiKeyConfigurationKey];
        string? configuredBaseUrl = configuration["Providers:Anthropic:BaseUrl"];
        _baseUrl = string.IsNullOrWhiteSpace(configuredBaseUrl)
            ? DefaultBaseUrl
            : configuredBaseUrl.TrimEnd('/');
    }

    /// <inheritdoc />
    public string Name => ProviderName;

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedModels => Models;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        string apiKey = GetRequiredApiKey();
        using HttpRequestMessage httpRequest = BuildMessagesRequest(request, apiKey, stream: false);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await ProviderHttpHelper.EnsureSuccessOrThrowAsync(httpResponse, Name, ct).ConfigureAwait(false);

        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string id = root.TryGetProperty("id", out JsonElement idElement)
            ? idElement.GetString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");

        string content = ExtractTextContent(root);

        int promptTokens = 0;
        int completionTokens = 0;
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            promptTokens = usage.TryGetProperty("input_tokens", out JsonElement inputEl) ? inputEl.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("output_tokens", out JsonElement outputEl) ? outputEl.GetInt32() : 0;
        }

        return new ChatResponse(id, request.Model, Name, content, new TokenUsage(promptTokens, completionTokens), 0);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string apiKey = GetRequiredApiKey();
        using HttpRequestMessage httpRequest = BuildMessagesRequest(request, apiKey, stream: true);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        await ProviderHttpHelper.EnsureSuccessOrThrowAsync(httpResponse, Name, ct).ConfigureAwait(false);
        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        string messageId = Guid.NewGuid().ToString("N");
        int inputTokens = 0;
        int outputTokens = 0;

        await foreach ((string? eventName, string data) in ProviderHttpHelper.ReadSseEventsAsync(responseStream, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;
            string effectiveEvent = eventName
                ?? (root.TryGetProperty("type", out JsonElement typeEl) ? typeEl.GetString() ?? string.Empty : string.Empty);

            if (effectiveEvent == "message_start" && root.TryGetProperty("message", out JsonElement msgEl))
            {
                if (msgEl.TryGetProperty("id", out JsonElement idEl) && idEl.GetString() is string parsedId)
                {
                    messageId = parsedId;
                }

                if (msgEl.TryGetProperty("usage", out JsonElement startUsage) &&
                    startUsage.TryGetProperty("input_tokens", out JsonElement inEl))
                {
                    inputTokens = inEl.GetInt32();
                }
            }
            else if (effectiveEvent == "content_block_delta" &&
                     root.TryGetProperty("delta", out JsonElement deltaEl) &&
                     deltaEl.TryGetProperty("text", out JsonElement textEl))
            {
                string? delta = textEl.GetString();
                if (!string.IsNullOrEmpty(delta))
                {
                    yield return new ChatStreamChunk(messageId, request.Model, Name, delta);
                }
            }
            else if (effectiveEvent == "message_delta" &&
                     root.TryGetProperty("usage", out JsonElement deltaUsage) &&
                     deltaUsage.TryGetProperty("output_tokens", out JsonElement outEl))
            {
                outputTokens = outEl.GetInt32();
            }
        }

        yield return new ChatStreamChunk(
            messageId,
            request.Model,
            Name,
            string.Empty,
            IsFinal: true,
            Usage: new TokenUsage(inputTokens, outputTokens));
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        // Probe the zero-cost /v1/models endpoint instead of burning inference tokens on a polling interval.
        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"{_baseUrl}/v1/models");
        httpRequest.Headers.Add(ApiKeyHeaderName, apiKey);
        httpRequest.Headers.Add(VersionHeaderName, AnthropicVersion);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        return httpResponse.IsSuccessStatusCode;
    }

    private HttpRequestMessage BuildMessagesRequest(ChatRequest request, string apiKey, bool stream)
    {
        (string? systemPrompt, IReadOnlyList<Message> conversation) = ProviderHttpHelper.SplitSystemMessages(request.Messages);

        HttpRequestMessage httpRequest = new(HttpMethod.Post, $"{_baseUrl}/v1/messages");
        httpRequest.Headers.Add(ApiKeyHeaderName, apiKey);
        httpRequest.Headers.Add(VersionHeaderName, AnthropicVersion);
        httpRequest.Content = JsonContent.Create(
            new
            {
                model = request.Model,
                max_tokens = request.MaxTokens ?? 1024,
                temperature = request.Temperature,
                system = systemPrompt,
                stream = stream ? true : (bool?)null,
                messages = conversation.Select(message => new
                {
                    role = NormalizeRole(message.Role),
                    content = message.Content
                })
            },
            options: ProviderHttpHelper.JsonOptions);

        return httpRequest;
    }

    private static string ExtractTextContent(JsonElement root)
    {
        if (!root.TryGetProperty("content", out JsonElement contentArray) ||
            contentArray.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (JsonElement block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("text", out JsonElement textEl) && textEl.GetString() is string text)
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private string GetRequiredApiKey()
    {
        return ProviderHttpHelper.GetRequiredApiKey(
            _configuredApiKey,
            _configuration,
            ApiKeyConfigurationKey,
            ApiKeyEnvironmentVariable,
            Name);
    }

    private string? GetConfiguredApiKey()
    {
        return ProviderHttpHelper.ResolveApiKey(
            _configuredApiKey,
            _configuration,
            ApiKeyConfigurationKey,
            ApiKeyEnvironmentVariable);
    }

    private static string NormalizeRole(string role)
    {
        return role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "assistant" : "user";
    }
}
