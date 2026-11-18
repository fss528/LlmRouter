using System.Net;
using System.Text;
using System.Text.Json;
using LlmRouter.Core.Models;
using LlmRouter.Infrastructure.Providers;
using Microsoft.Extensions.Configuration;

namespace LlmRouter.Tests;

public sealed class ProviderAdapterTests
{
    [Fact]
    public async Task AnthropicProvider_SendsExpectedHeadersSystemPromptAndTemperature()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
        {
          "id": "msg_123",
          "content": [{"type": "text", "text": "Claude answer"}],
          "usage": {"input_tokens": 12, "output_tokens": 7}
        }
        """));

        AnthropicProvider provider = new(new HttpClient(handler), BuildConfig("Providers:Anthropic:ApiKey", "anth-secret"));
        ChatRequest request = new(
            "claude-sonnet-4-20250514",
            [new Message("system", "Be terse"), new Message("user", "Hi")],
            MaxTokens: 150,
            Temperature: 0.3);

        ChatResponse response = await provider.CompleteAsync(request);

        Assert.Equal("msg_123", response.Id);
        Assert.Equal("Claude answer", response.Content);
        Assert.Equal(12, response.Usage.PromptTokens);
        Assert.Equal(7, response.Usage.CompletionTokens);

        Assert.NotNull(handler.LastRequest);
        Assert.Equal("anth-secret", handler.LastRequest!.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.LastRequest.Headers.GetValues("anthropic-version").Single());

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("Be terse", sent.RootElement.GetProperty("system").GetString());
        Assert.Equal(0.3, sent.RootElement.GetProperty("temperature").GetDouble(), precision: 3);
        Assert.Equal(150, sent.RootElement.GetProperty("max_tokens").GetInt32());
        Assert.Single(sent.RootElement.GetProperty("messages").EnumerateArray());
    }

    [Fact]
    public async Task AnthropicProvider_StreamsSseChunksAndIsHealthyUsesModelsEndpoint()
    {
        string sse =
            "event: message_start\n" +
            "data: {\"type\":\"message_start\",\"message\":{\"id\":\"msg_s1\",\"usage\":{\"input_tokens\":10}}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Hello \"}}\n\n" +
            "event: content_block_delta\n" +
            "data: {\"type\":\"content_block_delta\",\"delta\":{\"type\":\"text_delta\",\"text\":\"Claude\"}}\n\n" +
            "event: message_delta\n" +
            "data: {\"type\":\"message_delta\",\"usage\":{\"output_tokens\":4}}\n\n";

        RecordingHandler handler = new(req =>
            req.Method == HttpMethod.Get
                ? JsonResponse("{\"data\":[]}")
                : SseResponse(sse));

        AnthropicProvider provider = new(new HttpClient(handler), BuildConfig("Providers:Anthropic:ApiKey", "anth-secret"));

        List<ChatStreamChunk> chunks = [];
        await foreach (ChatStreamChunk chunk in provider.CompleteStreamAsync(new ChatRequest("claude-sonnet-4-20250514", [new Message("user", "hi")])))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hello ", chunks[0].Delta);
        Assert.Equal("Claude", chunks[1].Delta);
        Assert.True(chunks[2].IsFinal);
        Assert.Equal(10, chunks[2].Usage?.PromptTokens);
        Assert.Equal(4, chunks[2].Usage?.CompletionTokens);

        bool healthy = await provider.IsHealthyAsync();
        Assert.True(healthy);
        Assert.EndsWith("/v1/models", handler.LastRequest!.RequestUri!.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenAiProvider_OmitsNullOptionalFieldsAndStreamsSse()
    {
        string sse =
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{\"content\":\"Hi \"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"gpt-4o\",\"choices\":[{\"delta\":{\"content\":\"OpenAI\"}}]}\n\n" +
            "data: {\"id\":\"chatcmpl-1\",\"model\":\"gpt-4o\",\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":3}}\n\n" +
            "data: [DONE]\n\n";

        RecordingHandler handler = new(req =>
            req.RequestUri!.AbsolutePath.EndsWith("/chat/completions", StringComparison.Ordinal)
                ? SseResponse(sse)
                : JsonResponse("{}"));

        OpenAiProvider provider = new(new HttpClient(handler), BuildConfig("Providers:OpenAI:ApiKey", "sk-test"));
        ChatRequest request = new("gpt-4o", [new Message("user", "hi")]);

        List<ChatStreamChunk> chunks = [];
        await foreach (ChatStreamChunk chunk in provider.CompleteStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hi ", chunks[0].Delta);
        Assert.Equal("OpenAI", chunks[1].Delta);
        Assert.True(chunks[2].IsFinal);
        Assert.Equal(9, chunks[2].Usage?.PromptTokens);
        Assert.Equal(3, chunks[2].Usage?.CompletionTokens);

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.False(sent.RootElement.TryGetProperty("max_tokens", out _));
        Assert.False(sent.RootElement.TryGetProperty("temperature", out _));
        Assert.Equal("Bearer sk-test", handler.LastRequest!.Headers.Authorization?.ToString());
    }

    [Fact]
    public async Task GeminiProvider_SendsApiKeyInHeaderNotUrlAndMapsGenerationConfig()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
        {
          "candidates": [
            {
              "content": {
                "parts": [{"text": "Gemini output"}]
              }
            }
          ],
          "usageMetadata": {"promptTokenCount": 5, "candidatesTokenCount": 8}
        }
        """));

        GeminiProvider provider = new(new HttpClient(handler), BuildConfig("Providers:Gemini:ApiKey", "gem-secret"));
        ChatRequest request = new(
            "gemini-2.5-flash",
            [new Message("system", "Sys rule"), new Message("user", "Hello"), new Message("assistant", "Prev")],
            MaxTokens: 120,
            Temperature: 0.4);

        ChatResponse response = await provider.CompleteAsync(request);

        Assert.Equal("Gemini output", response.Content);
        Assert.Equal(5, response.Usage.PromptTokens);
        Assert.Equal(8, response.Usage.CompletionTokens);

        Assert.NotNull(handler.LastRequest);
        Assert.DoesNotContain("gem-secret", handler.LastRequest!.RequestUri!.ToString(), StringComparison.Ordinal);
        Assert.Equal("gem-secret", handler.LastRequest.Headers.GetValues("x-goog-api-key").Single());

        using JsonDocument sent = JsonDocument.Parse(handler.LastRequestBody!);
        Assert.Equal("Sys rule", sent.RootElement.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString());
        Assert.Equal(120, sent.RootElement.GetProperty("generationConfig").GetProperty("maxOutputTokens").GetInt32());
        Assert.Equal(0.4, sent.RootElement.GetProperty("generationConfig").GetProperty("temperature").GetDouble(), precision: 3);
        Assert.Equal("user", sent.RootElement.GetProperty("contents")[0].GetProperty("role").GetString());
        Assert.Equal("model", sent.RootElement.GetProperty("contents")[1].GetProperty("role").GetString());
    }

    [Fact]
    public async Task GeminiProvider_HandlesSafetyBlockedEmptyPartsWithoutThrowing()
    {
        RecordingHandler handler = new(_ => JsonResponse("""
        {
          "candidates": [{"finishReason": "SAFETY"}],
          "usageMetadata": {"promptTokenCount": 4}
        }
        """));

        GeminiProvider provider = new(new HttpClient(handler), BuildConfig("Providers:Gemini:ApiKey", "gem-secret"));
        ChatResponse response = await provider.CompleteAsync(new ChatRequest("gemini-2.5-flash", [new Message("user", "test")]));

        Assert.Equal(string.Empty, response.Content);
        Assert.Equal(4, response.Usage.PromptTokens);
        Assert.Equal(0, response.Usage.CompletionTokens);
    }

    [Fact]
    public async Task SimulatedProvider_HonorsFaultInjectionContext()
    {
        AsyncLocalSimulatedFaultContext faultContext = new()
        {
            FailProvider = "OpenAI",
            FailStatusCode = HttpStatusCode.TooManyRequests
        };
        SimulatedProvider provider = new("OpenAI", ["gpt-4o"], 10, faultContext);

        HttpRequestException ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            provider.CompleteAsync(new ChatRequest("gpt-4o", [new Message("user", "ping")])));

        Assert.Equal(HttpStatusCode.TooManyRequests, ex.StatusCode);
        Assert.False(await provider.IsHealthyAsync());
    }

    private static IConfiguration BuildConfig(string key, string value)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [key] = value })
            .Build();
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static HttpResponseMessage SseResponse(string sse)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream")
        };
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            }

            return responder(request);
        }
    }
}
