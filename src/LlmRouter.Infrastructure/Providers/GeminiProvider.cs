using System.Globalization;
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
/// Google Gemini generateContent adapter. Translates provider-neutral messages, system instructions,
/// and generation parameters while keeping API keys in request headers rather than query strings.
/// </summary>
public sealed class GeminiProvider : ILlmProvider
{
    private const string ProviderName = "Gemini";
    private const string ApiKeyHeaderName = "x-goog-api-key";
    private const string DefaultBaseUrl = "https://generativelanguage.googleapis.com";
    private const string ApiKeyEnvironmentVariable = "GEMINI_API_KEY";
    private const string ApiKeyConfigurationKey = "Providers:Gemini:ApiKey";

    private static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal)
    {
        "gemini-2.5-flash",
        "gemini-2.5-pro",
        "gemini-2.0-flash",
        "gemini-2.0-flash-001",
        "gemini-2.0-flash-lite",
        "gemini-2.0-flash-lite-001",
        "gemini-flash-latest",
        "gemini-flash-lite-latest",
        "gemini-pro-latest"
    };

    private readonly string _baseUrl;
    private readonly IConfiguration? _configuration;
    private readonly string? _configuredApiKey;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the Gemini provider adapter from strongly-typed options.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public GeminiProvider(
        HttpClient httpClient,
        IOptions<ProvidersOptions> options,
        IConfiguration? configuration = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        ProviderEndpointOptions endpoint = options.Value.Gemini;
        _configuredApiKey = endpoint.ApiKey;
        _baseUrl = string.IsNullOrWhiteSpace(endpoint.BaseUrl)
            ? DefaultBaseUrl
            : endpoint.BaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Creates the Gemini provider adapter directly from <see cref="IConfiguration"/>.
    /// </summary>
    public GeminiProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _configuredApiKey = configuration[ApiKeyConfigurationKey];
        string? configuredBaseUrl = configuration["Providers:Gemini:BaseUrl"];
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
        string endpoint = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/v1beta/models/{1}:generateContent",
            _baseUrl,
            Uri.EscapeDataString(request.Model));

        using HttpRequestMessage httpRequest = BuildGenerateRequest(request, endpoint, apiKey);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await ProviderHttpHelper.EnsureSuccessOrThrowAsync(httpResponse, Name, ct).ConfigureAwait(false);

        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string content = ExtractCandidateText(root);
        TokenUsage usage = ExtractUsage(root);

        return new ChatResponse(Guid.NewGuid().ToString("N"), request.Model, Name, content, usage, 0);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string apiKey = GetRequiredApiKey();
        string endpoint = string.Format(
            CultureInfo.InvariantCulture,
            "{0}/v1beta/models/{1}:streamGenerateContent?alt=sse",
            _baseUrl,
            Uri.EscapeDataString(request.Model));

        using HttpRequestMessage httpRequest = BuildGenerateRequest(request, endpoint, apiKey);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        await ProviderHttpHelper.EnsureSuccessOrThrowAsync(httpResponse, Name, ct).ConfigureAwait(false);
        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        string streamId = Guid.NewGuid().ToString("N");
        TokenUsage? latestUsage = null;

        await foreach ((string? _, string data) in ProviderHttpHelper.ReadSseEventsAsync(responseStream, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("usageMetadata", out _))
            {
                latestUsage = ExtractUsage(root);
            }

            string delta = ExtractCandidateText(root);
            if (!string.IsNullOrEmpty(delta))
            {
                yield return new ChatStreamChunk(streamId, request.Model, Name, delta);
            }
        }

        yield return new ChatStreamChunk(
            streamId,
            request.Model,
            Name,
            string.Empty,
            IsFinal: true,
            Usage: latestUsage ?? new TokenUsage(0, 0));
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"{_baseUrl}/v1beta/models");
        httpRequest.Headers.Add(ApiKeyHeaderName, apiKey);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        return httpResponse.IsSuccessStatusCode;
    }

    private static HttpRequestMessage BuildGenerateRequest(ChatRequest request, string endpoint, string apiKey)
    {
        (string? systemPrompt, IReadOnlyList<Message> conversation) = ProviderHttpHelper.SplitSystemMessages(request.Messages);

        object? systemInstruction = systemPrompt is null
            ? null
            : new
            {
                parts = new[] { new { text = systemPrompt } }
            };

        object? generationConfig = (request.MaxTokens is not null || request.Temperature is not null)
            ? new
            {
                maxOutputTokens = request.MaxTokens,
                temperature = request.Temperature
            }
            : null;

        HttpRequestMessage httpRequest = new(HttpMethod.Post, endpoint);
        // Send the Gemini key via header so HttpClient logging never records credentials in URLs.
        httpRequest.Headers.Add(ApiKeyHeaderName, apiKey);
        httpRequest.Content = JsonContent.Create(
            new
            {
                system_instruction = systemInstruction,
                generationConfig,
                contents = conversation.Select(message => new
                {
                    role = NormalizeRole(message.Role),
                    parts = new[]
                    {
                        new { text = message.Content }
                    }
                })
            },
            options: ProviderHttpHelper.JsonOptions);

        return httpRequest;
    }

    private static string ExtractCandidateText(JsonElement root)
    {
        if (!root.TryGetProperty("candidates", out JsonElement candidates) ||
            candidates.ValueKind != JsonValueKind.Array ||
            candidates.GetArrayLength() == 0)
        {
            return string.Empty;
        }

        JsonElement firstCandidate = candidates[0];
        if (!firstCandidate.TryGetProperty("content", out JsonElement contentEl) ||
            !contentEl.TryGetProperty("parts", out JsonElement parts) ||
            parts.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        StringBuilder builder = new();
        foreach (JsonElement part in parts.EnumerateArray())
        {
            if (part.TryGetProperty("text", out JsonElement textEl) && textEl.GetString() is string text)
            {
                builder.Append(text);
            }
        }

        return builder.ToString();
    }

    private static TokenUsage ExtractUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usageMetadata", out JsonElement usage))
        {
            return new TokenUsage(0, 0);
        }

        int promptTokens = usage.TryGetProperty("promptTokenCount", out JsonElement promptEl) ? promptEl.GetInt32() : 0;
        int completionTokens = usage.TryGetProperty("candidatesTokenCount", out JsonElement completionEl) ? completionEl.GetInt32() : 0;
        return new TokenUsage(promptTokens, completionTokens);
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
        return role.Equals("assistant", StringComparison.OrdinalIgnoreCase) ? "model" : "user";
    }
}
