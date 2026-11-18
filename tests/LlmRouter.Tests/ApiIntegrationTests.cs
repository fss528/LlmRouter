using System.Net;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmRouter.Api.Dtos;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace LlmRouter.Tests;

public sealed class ApiIntegrationTests : IClassFixture<LlmRouterApiFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly LlmRouterApiFactory _factory;

    public ApiIntegrationTests(LlmRouterApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task PostChat_ReturnsProviderResponse()
    {
        using HttpClient client = _factory.CreateClient();
        object request = new
        {
            model = FakeProvider.SupportedModelName,
            messages = new[]
            {
                new { role = "user", content = "hello" }
            },
            strategy = "PriorityWithFallback"
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ChatResponse? body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.NotNull(body);
        Assert.Equal(FakeProvider.ProviderName, body.Provider);
        Assert.Equal("integration-test-response", body.Content);
    }

    [Fact]
    public async Task PostChat_ReturnsValidationProblemWhenMessagesEmpty()
    {
        using HttpClient client = _factory.CreateClient();
        object request = new
        {
            model = FakeProvider.SupportedModelName,
            messages = Array.Empty<object>()
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Messages", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task PostChat_ReturnsBadRequestWhenModelIsUnsupported()
    {
        using HttpClient client = _factory.CreateClient();
        object request = new
        {
            model = "unsupported-model",
            messages = new[]
            {
                new { role = "user", content = "hello" }
            }
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("No provider supports model unsupported-model", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostChatStream_ReturnsBadRequestWhenModelIsUnsupported()
    {
        using HttpClient client = _factory.CreateClient();
        object request = new
        {
            model = "unsupported-model",
            messages = new[]
            {
                new { role = "user", content = "hello" }
            }
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat/stream", request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostChatStream_StreamsServerSentEvents()
    {
        using HttpClient client = _factory.CreateClient();
        object request = new
        {
            model = FakeProvider.SupportedModelName,
            messages = new[]
            {
                new { role = "user", content = "stream please" }
            }
        };

        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat/stream", request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        string payload = await response.Content.ReadAsStringAsync();
        Assert.Contains("event: chunk", payload, StringComparison.Ordinal);
        Assert.Contains("event: done", payload, StringComparison.Ordinal);
        Assert.Contains("integration-stream-chunk", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostOpenAiChatCompletions_ReturnsOpenAiCompatibleJsonAndSse()
    {
        using HttpClient client = _factory.CreateClient();
        object jsonReq = new
        {
            model = FakeProvider.SupportedModelName,
            messages = new[] { new { role = "user", content = "hello" } },
            stream = false
        };

        using HttpResponseMessage jsonResp = await client.PostAsJsonAsync("/v1/chat/completions", jsonReq);
        Assert.Equal(HttpStatusCode.OK, jsonResp.StatusCode);
        Assert.Equal(FakeProvider.ProviderName, jsonResp.Headers.GetValues("X-LlmRouter-Provider").Single());

        using JsonDocument doc = JsonDocument.Parse(await jsonResp.Content.ReadAsStringAsync());
        Assert.Equal("chat.completion", doc.RootElement.GetProperty("object").GetString());
        Assert.Equal("integration-test-response", doc.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString());

        object streamReq = new
        {
            model = FakeProvider.SupportedModelName,
            messages = new[] { new { role = "user", content = "hello" } },
            stream = true
        };

        using HttpResponseMessage streamResp = await client.PostAsJsonAsync("/v1/chat/completions", streamReq);
        Assert.Equal(HttpStatusCode.OK, streamResp.StatusCode);
        string sse = await streamResp.Content.ReadAsStringAsync();
        Assert.Contains("chat.completion.chunk", sse, StringComparison.Ordinal);
        Assert.Contains("data: [DONE]", sse, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetModels_ReturnsAliasesAndProviderCatalog()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/v1/models");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ModelsCatalogResponse? catalog = await response.Content.ReadFromJsonAsync<ModelsCatalogResponse>(JsonOptions);
        Assert.NotNull(catalog);
        Assert.Contains(catalog.Aliases, a => a.Alias.Equals("fast", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(catalog.Providers, p => p.Provider == FakeProvider.ProviderName);
    }

    [Fact]
    public async Task PostChat_ReturnsBadRequestWhenJsonIsNotUtf8()
    {
        using HttpClient client = _factory.CreateClient();
        byte[] invalidUtf8Json =
        [
            0x7B, 0x22, 0x6D, 0x6F, 0x64, 0x65, 0x6C, 0x22, 0x3A, 0x22,
            0x74, 0x65, 0x73, 0x74, 0x2D, 0x6D, 0x6F, 0x64, 0x65, 0x6C,
            0x22, 0x2C, 0x22, 0x6D, 0x65, 0x73, 0x73, 0x61, 0x67, 0x65,
            0x73, 0x22, 0x3A, 0x5B, 0x7B, 0x22, 0x72, 0x6F, 0x6C, 0x65,
            0x22, 0x3A, 0x22, 0x75, 0x73, 0x65, 0x72, 0x22, 0x2C, 0x22,
            0x63, 0x6F, 0x6E, 0x74, 0x65, 0x6E, 0x74, 0x22, 0x3A, 0x22,
            0x71, 0x75, 0xE9, 0x22, 0x7D, 0x5D, 0x7D
        ];
        using ByteArrayContent content = new(invalidUtf8Json);
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");

        using HttpResponseMessage response = await client.PostAsync("/v1/chat", content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task PostChat_ReturnsServiceUnavailableWhenAllProvidersFail()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmProvider>();
                services.RemoveAll<IHostedService>();
                services.AddSingleton<ILlmProvider>(new AlwaysFailingProvider("BrokenA", "shared-model"));
                services.AddSingleton<ILlmProvider>(new AlwaysFailingProvider("BrokenB", "shared-model"));
            });
        });

        using HttpClient client = factory.CreateClient();
        using HttpResponseMessage response = await client.PostAsJsonAsync("/v1/chat", new
        {
            model = "shared-model",
            messages = new[] { new { role = "user", content = "hi" } }
        });

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("All providers failed", body, StringComparison.Ordinal);
        Assert.Contains("BrokenA", body, StringComparison.Ordinal);
        Assert.Contains("BrokenB", body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PostChat_RoundRobinRotatesAcrossHttpRequests()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ILlmProvider>();
                services.RemoveAll<IHostedService>();
                services.AddSingleton<ILlmProvider>(new NamedFakeProvider("Alpha", "shared-model"));
                services.AddSingleton<ILlmProvider>(new NamedFakeProvider("Beta", "shared-model"));
            });
        });

        using HttpClient client = factory.CreateClient();
        object request = new
        {
            model = "shared-model",
            messages = new[] { new { role = "user", content = "ping" } },
            strategy = "RoundRobin"
        };

        ChatResponse? first = await (await client.PostAsJsonAsync("/v1/chat", request)).Content.ReadFromJsonAsync<ChatResponse>();
        ChatResponse? second = await (await client.PostAsJsonAsync("/v1/chat", request)).Content.ReadFromJsonAsync<ChatResponse>();
        ChatResponse? third = await (await client.PostAsJsonAsync("/v1/chat", request)).Content.ReadFromJsonAsync<ChatResponse>();

        Assert.Equal("Alpha", first?.Provider);
        Assert.Equal("Beta", second?.Provider);
        Assert.Equal("Alpha", third?.Provider);
    }

    [Fact]
    public async Task PostChat_DemoModeCrossProviderFallbackWithFaultInjection()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DemoMode:Enabled"] = "true",
                    ["DemoMode:GeminiLatencyMs"] = "10",
                    ["DemoMode:OpenAiLatencyMs"] = "10",
                    ["DemoMode:AnthropicLatencyMs"] = "10",
                    ["Providers:HealthCheckEnabled"] = "false"
                });
            });
        });

        using HttpClient client = factory.CreateClient();
        using HttpRequestMessage msg = new(HttpMethod.Post, "/v1/chat")
        {
            Content = JsonContent.Create(new
            {
                model = "fast",
                messages = new[] { new { role = "user", content = "Test cross-provider failover" } },
                strategy = "PriorityWithFallback"
            })
        };
        msg.Headers.Add("X-Simulate-Failure", "Gemini");

        using HttpResponseMessage response = await client.SendAsync(msg);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ChatResponse? body = await response.Content.ReadFromJsonAsync<ChatResponse>();
        Assert.NotNull(body);
        Assert.Equal("OpenAI", body.Provider);
        Assert.Equal("gpt-4o-mini", body.Model);
        Assert.Equal("fast", body.RequestedModel);
        Assert.Equal(2, body.Attempts?.Count);
        Assert.False(body.Attempts![0].Succeeded);
        Assert.Equal("Gemini", body.Attempts[0].Provider);
        Assert.True(body.Attempts[1].Succeeded);
    }

    [Fact]
    public async Task GatewayAuthAndRateLimit_EnforceApiKeysAndRequestLimits()
    {
        using WebApplicationFactory<Program> factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["DemoMode:Enabled"] = "true",
                    ["DemoMode:GeminiLatencyMs"] = "10",
                    ["Providers:HealthCheckEnabled"] = "false",
                    ["Gateway:ApiKeys:0"] = "secret-gw-key",
                    ["Gateway:RateLimit:Enabled"] = "true",
                    ["Gateway:RateLimit:PermitLimit"] = "2",
                    ["Gateway:RateLimit:WindowSeconds"] = "60"
                });
            });
        });

        using HttpClient client = factory.CreateClient();

        using HttpResponseMessage unauthorized = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);

        client.DefaultRequestHeaders.Add("X-Api-Key", "secret-gw-key");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/v1/models")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/v1/models")).StatusCode);
    }

    [Fact]
    public async Task GetProviderHealth_ReturnsTrackedProviderHealth()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/health/providers");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        ProviderHealth[]? body = await response.Content.ReadFromJsonAsync<ProviderHealth[]>(JsonOptions);
        Assert.NotNull(body);
        Assert.Contains(body, health => health.ProviderName == FakeProvider.ProviderName);
    }

    [Fact]
    public async Task GetOpenApi_ReturnsOpenApiDocument()
    {
        using HttpClient client = _factory.CreateClient();

        using HttpResponseMessage response = await client.GetAsync("/openapi/v1.json");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        string body = await response.Content.ReadAsStringAsync();
        Assert.Contains("/v1/chat", body, StringComparison.Ordinal);
        Assert.Contains("/v1/chat/stream", body, StringComparison.Ordinal);
        Assert.Contains("/v1/chat/completions", body, StringComparison.Ordinal);
        Assert.Contains("/v1/models", body, StringComparison.Ordinal);
        Assert.Contains("/health/providers", body, StringComparison.Ordinal);
    }
}

public sealed class LlmRouterApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<ILlmProvider>();
            services.RemoveAll<IHostedService>();
            services.AddSingleton<ILlmProvider, FakeProvider>();
        });
    }
}

public sealed class FakeProvider : ILlmProvider
{
    public const string ProviderName = "FakeProvider";
    public const string SupportedModelName = "test-model";

    private static readonly IReadOnlySet<string> Models = new HashSet<string>([SupportedModelName], StringComparer.Ordinal);

    public string Name => ProviderName;

    public IReadOnlySet<string> SupportedModels => Models;

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        ChatResponse response = new(
            "fake-response-id",
            request.Model,
            ProviderName,
            "integration-test-response",
            new TokenUsage(2, 3),
            1);

        return Task.FromResult(response);
    }

    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new ChatStreamChunk("fake-stream-id", request.Model, ProviderName, "integration-stream-chunk");
        yield return new ChatStreamChunk("fake-stream-id", request.Model, ProviderName, string.Empty, IsFinal: true, Usage: new TokenUsage(2, 3));
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}

file sealed class NamedFakeProvider(string name, string model) : ILlmProvider
{
    public string Name { get; } = name;
    public IReadOnlySet<string> SupportedModels { get; } = new HashSet<string>([model], StringComparer.Ordinal);

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        return Task.FromResult(new ChatResponse("id", request.Model, Name, $"from-{Name}", new TokenUsage(1, 1), 1));
    }

    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        yield return new ChatStreamChunk("id", request.Model, Name, $"from-{Name}");
        yield return new ChatStreamChunk("id", request.Model, Name, string.Empty, IsFinal: true, Usage: new TokenUsage(1, 1));
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
}

file sealed class AlwaysFailingProvider(string name, string model) : ILlmProvider
{
    public string Name { get; } = name;
    public IReadOnlySet<string> SupportedModels { get; } = new HashSet<string>([model], StringComparer.Ordinal);

    public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        return Task.FromException<ChatResponse>(new HttpRequestException($"{Name} unavailable", null, HttpStatusCode.ServiceUnavailable));
    }

    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new HttpRequestException($"{Name} unavailable", null, HttpStatusCode.ServiceUnavailable);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(false);
}
