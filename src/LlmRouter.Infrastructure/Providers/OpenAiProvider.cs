using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace LlmRouter.Infrastructure.Providers;

/// <summary>
/// OpenAI Chat Completions API adapter.
/// </summary>
public sealed class OpenAiProvider : ILlmProvider
{
    private const string ProviderName = "OpenAI";
    private const string DefaultBaseUrl = "https://api.openai.com";
    private const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    private const string ApiKeyConfigurationKey = "Providers:OpenAI:ApiKey";

    private static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal)
    {
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4-turbo"
    };

    private readonly string _baseUrl;
    private readonly IConfiguration? _configuration;
    private readonly string? _configuredApiKey;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the OpenAI provider adapter from strongly-typed options.
    /// </summary>
    [ActivatorUtilitiesConstructor]
    public OpenAiProvider(
        HttpClient httpClient,
        IOptions<ProvidersOptions> options,
        IConfiguration? configuration = null)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        ProviderEndpointOptions endpoint = options.Value.OpenAI;
        _configuredApiKey = endpoint.ApiKey;
        _baseUrl = string.IsNullOrWhiteSpace(endpoint.BaseUrl)
            ? DefaultBaseUrl
            : endpoint.BaseUrl.TrimEnd('/');
    }

    /// <summary>
    /// Creates the OpenAI provider adapter directly from <see cref="IConfiguration"/>.
    /// </summary>
    public OpenAiProvider(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _configuredApiKey = configuration[ApiKeyConfigurationKey];
        string? configuredBaseUrl = configuration["Providers:OpenAI:BaseUrl"];
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
        using HttpRequestMessage httpRequest = BuildChatRequest(request, apiKey, stream: false);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        await ProviderHttpHelper.EnsureSuccessOrThrowAsync(httpResponse, Name, ct).ConfigureAwait(false);

        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string id = root.TryGetProperty("id", out JsonElement idElement)
            ? idElement.GetString() ?? Guid.NewGuid().ToString("N")
            : Guid.NewGuid().ToString("N");
        string model = root.TryGetProperty("model", out JsonElement modelElement)
            ? modelElement.GetString() ?? request.Model
            : request.Model;
        string content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;

        int promptTokens = 0;
        int completionTokens = 0;
        if (root.TryGetProperty("usage", out JsonElement usage))
        {
            promptTokens = usage.TryGetProperty("prompt_tokens", out JsonElement promptEl) ? promptEl.GetInt32() : 0;
            completionTokens = usage.TryGetProperty("completion_tokens", out JsonElement compEl) ? compEl.GetInt32() : 0;
        }

        return new ChatResponse(id, model, Name, content, new TokenUsage(promptTokens, completionTokens), 0);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        string apiKey = GetRequiredApiKey();
        using HttpRequestMessage httpRequest = BuildChatRequest(request, apiKey, stream: true);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(
            httpRequest,
            HttpCompletionOption.ResponseHeadersRead,
            ct).ConfigureAwait(false);

        await ProviderHttpHelper.EnsureSuccessOrThrowAsync(httpResponse, Name, ct).ConfigureAwait(false);
        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        string streamId = Guid.NewGuid().ToString("N");
        string resolvedModel = request.Model;
        TokenUsage? finalUsage = null;

        await foreach ((string? _, string data) in ProviderHttpHelper.ReadSseEventsAsync(responseStream, ct).ConfigureAwait(false))
        {
            if (string.IsNullOrWhiteSpace(data) || data == "[DONE]")
            {
                continue;
            }

            using JsonDocument doc = JsonDocument.Parse(data);
            JsonElement root = doc.RootElement;

            if (root.TryGetProperty("id", out JsonElement idEl) && idEl.GetString() is string id && !string.IsNullOrEmpty(id))
            {
                streamId = id;
            }

            if (root.TryGetProperty("model", out JsonElement modelEl) && modelEl.GetString() is string model && !string.IsNullOrEmpty(model))
            {
                resolvedModel = model;
            }

            if (root.TryGetProperty("usage", out JsonElement usageEl) && usageEl.ValueKind == JsonValueKind.Object)
            {
                int prompt = usageEl.TryGetProperty("prompt_tokens", out JsonElement pEl) ? pEl.GetInt32() : 0;
                int completion = usageEl.TryGetProperty("completion_tokens", out JsonElement cEl) ? cEl.GetInt32() : 0;
                finalUsage = new TokenUsage(prompt, completion);
            }

            if (root.TryGetProperty("choices", out JsonElement choices) &&
                choices.ValueKind == JsonValueKind.Array &&
                choices.GetArrayLength() > 0 &&
                choices[0].TryGetProperty("delta", out JsonElement deltaEl) &&
                deltaEl.TryGetProperty("content", out JsonElement contentEl) &&
                contentEl.GetString() is string delta &&
                delta.Length > 0)
            {
                yield return new ChatStreamChunk(streamId, resolvedModel, Name, delta);
            }
        }

        yield return new ChatStreamChunk(
            streamId,
            resolvedModel,
            Name,
            string.Empty,
            IsFinal: true,
            Usage: finalUsage ?? new TokenUsage(0, 0));
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, $"{_baseUrl}/v1/models");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        return httpResponse.IsSuccessStatusCode;
    }

    private HttpRequestMessage BuildChatRequest(ChatRequest request, string apiKey, bool stream)
    {
        HttpRequestMessage httpRequest = new(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions");
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(
            new
            {
                model = request.Model,
                max_tokens = request.MaxTokens,
                temperature = request.Temperature,
                stream = stream ? true : (bool?)null,
                stream_options = stream ? new { include_usage = true } : null,
                messages = request.Messages.Select(message => new
                {
                    role = message.Role,
                    content = message.Content
                })
            },
            options: ProviderHttpHelper.JsonOptions);

        return httpRequest;
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
}
