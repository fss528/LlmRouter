using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Infrastructure.BackgroundServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
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

    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        return Task.FromResult(true);
    }
}
