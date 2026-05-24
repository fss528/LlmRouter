using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using Microsoft.Extensions.Configuration;

namespace LlmRouter.Infrastructure.Providers;

/// <summary>
/// OpenAI Chat Completions API adapter.
/// </summary>
public sealed class OpenAiProvider : ILlmProvider
{
    private const string ProviderName = "OpenAI";
    private const string ChatCompletionsEndpoint = "https://api.openai.com/v1/chat/completions";
    private const string ModelsEndpoint = "https://api.openai.com/v1/models";
    private const string ApiKeyEnvironmentVariable = "OPENAI_API_KEY";
    private const string ApiKeyConfigurationKey = "Providers:OpenAI:ApiKey";

    private static readonly IReadOnlySet<string> Models = new HashSet<string>(StringComparer.Ordinal)
    {
        "gpt-4o",
        "gpt-4o-mini",
        "gpt-4-turbo"
    };

    private readonly IConfiguration _configuration;
    private readonly HttpClient _httpClient;

    /// <summary>
    /// Creates the OpenAI provider adapter.
    /// </summary>
    public OpenAiProvider(HttpClient httpClient, IConfiguration configuration)
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
        using HttpRequestMessage httpRequest = new(HttpMethod.Post, ChatCompletionsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        httpRequest.Content = JsonContent.Create(new
        {
            model = request.Model,
            max_tokens = request.MaxTokens,
            temperature = request.Temperature,
            messages = request.Messages.Select(message => new
            {
                role = message.Role,
                content = message.Content
            })
        });

        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        httpResponse.EnsureSuccessStatusCode();
        await using Stream responseStream = await httpResponse.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using JsonDocument document = await JsonDocument.ParseAsync(responseStream, cancellationToken: ct).ConfigureAwait(false);
        JsonElement root = document.RootElement;

        string id = root.TryGetProperty("id", out JsonElement idElement) ? idElement.GetString() ?? Guid.NewGuid().ToString("N") : Guid.NewGuid().ToString("N");
        string model = root.TryGetProperty("model", out JsonElement modelElement) ? modelElement.GetString() ?? request.Model : request.Model;
        string content = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? string.Empty;
        JsonElement usage = root.GetProperty("usage");
        int promptTokens = usage.GetProperty("prompt_tokens").GetInt32();
        int completionTokens = usage.GetProperty("completion_tokens").GetInt32();

        return new ChatResponse(id, model, Name, content, new TokenUsage(promptTokens, completionTokens), 0);
    }

    /// <inheritdoc />
    public async Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return false;
        }

        using HttpRequestMessage httpRequest = new(HttpMethod.Get, ModelsEndpoint);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        using HttpResponseMessage httpResponse = await _httpClient.SendAsync(httpRequest, ct).ConfigureAwait(false);
        return httpResponse.IsSuccessStatusCode;
    }

    private string GetRequiredApiKey()
    {
        string? apiKey = GetConfiguredApiKey();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException($"Missing OpenAI API key. Set {ApiKeyEnvironmentVariable} or {ApiKeyConfigurationKey}.");
        }

        return apiKey;
    }

    private string? GetConfiguredApiKey()
    {
        return Environment.GetEnvironmentVariable(ApiKeyEnvironmentVariable) ?? _configuration[ApiKeyConfigurationKey];
    }
}
