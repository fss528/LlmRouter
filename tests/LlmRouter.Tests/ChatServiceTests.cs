using System.Net;
using System.Runtime.CompilerServices;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Services;
using NSubstitute;

namespace LlmRouter.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task CompleteAsync_ReturnsResponseFromFirstProviderWithCostAndAttemptTrace()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider provider = CreateProvider("OpenAI", "gpt-4o");
        ChatResponse response = new("res-1", "gpt-4o", "OpenAI", "hello", new TokenUsage(1_000, 500), 25);
        provider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult(response));
        IProviderRouter router = CreateRouter(request, new ProviderRoute(provider, "gpt-4o"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        ChatResponse actual = await service.CompleteAsync(request);

        Assert.Equal("OpenAI", actual.Provider);
        Assert.Equal("gpt-4o", actual.RequestedModel);
        Assert.NotNull(actual.EstimatedCostUsd);
        Assert.True(actual.EstimatedCostUsd > 0);
        Assert.NotNull(actual.Attempts);
        Assert.Single(actual.Attempts);
        Assert.True(actual.Attempts[0].Succeeded);
        healthTracker.Received(1).RecordSuccess("OpenAI", Arg.Any<long>());
    }

    [Fact]
    public async Task CompleteAsync_FallsBackToSecondProviderWhenFirstThrowsTransientError()
    {
        ChatRequest request = CreateRequest("fast");
        ILlmProvider first = CreateProvider("Gemini", "gemini-2.5-flash");
        ILlmProvider second = CreateProvider("OpenAI", "gpt-4o-mini");
        first.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new HttpRequestException("rate limited", null, HttpStatusCode.TooManyRequests)));
        second.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateResponse("OpenAI", "gpt-4o-mini")));

        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(first, "gemini-2.5-flash"),
            new ProviderRoute(second, "gpt-4o-mini"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        ChatResponse actual = await service.CompleteAsync(request);

        Assert.Equal("OpenAI", actual.Provider);
        Assert.Equal("gpt-4o-mini", actual.Model);
        Assert.Equal("fast", actual.RequestedModel);
        Assert.Equal(2, actual.Attempts?.Count);
        Assert.False(actual.Attempts![0].Succeeded);
        Assert.Equal(429, actual.Attempts[0].StatusCode);
        Assert.True(actual.Attempts[1].Succeeded);
        healthTracker.Received(1).RecordFailure("Gemini");
        healthTracker.Received(1).RecordSuccess("OpenAI", Arg.Any<long>());
    }

    [Fact]
    public async Task CompleteAsync_DoesNotRecordFailureOrFallbackOnClientBadRequest()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider first = CreateProvider("OpenAI", "gpt-4o");
        ILlmProvider second = CreateProvider("Anthropic", "claude-sonnet-4-20250514");
        first.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new HttpRequestException("context too long", null, HttpStatusCode.BadRequest)));

        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(first, "gpt-4o"),
            new ProviderRoute(second, "claude-sonnet-4-20250514"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        ProviderClientException exception = await Assert.ThrowsAsync<ProviderClientException>(() => service.CompleteAsync(request));

        Assert.Equal("OpenAI", exception.ProviderName);
        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        healthTracker.DidNotReceive().RecordFailure(Arg.Any<string>());
        await second.DidNotReceive().CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteAsync_FallsBackWhenProviderTimesOutWithoutCallerCancellation()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider slow = CreateProvider("OpenAI", "gpt-4o");
        ILlmProvider backup = CreateProvider("Anthropic", "claude-sonnet-4-20250514");

        slow.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(async callInfo =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), callInfo.Arg<CancellationToken>());
                return CreateResponse("OpenAI", "gpt-4o");
            });

        backup.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateResponse("Anthropic", "claude-sonnet-4-20250514")));

        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(slow, "gpt-4o"),
            new ProviderRoute(backup, "claude-sonnet-4-20250514"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker, providerTimeout: TimeSpan.FromMilliseconds(40));

        ChatResponse actual = await service.CompleteAsync(request);

        Assert.Equal("Anthropic", actual.Provider);
        healthTracker.Received(1).RecordFailure("OpenAI");
        healthTracker.Received(1).RecordSuccess("Anthropic", Arg.Any<long>());
    }

    [Fact]
    public async Task CompleteAsync_ReleasesUnusedHalfOpenProbeWhenEarlierRouteSucceeds()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider primary = CreateProvider("first", "gpt-4o");
        ILlmProvider halfOpen = CreateProvider("second", "gpt-4o");
        primary.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(CreateResponse("first", "gpt-4o")));

        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(primary, "gpt-4o", IsHalfOpenProbe: false),
            new ProviderRoute(halfOpen, "gpt-4o", IsHalfOpenProbe: true));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        await service.CompleteAsync(request);

        healthTracker.Received(1).ReleaseHalfOpenProbe("second");
    }

    [Fact]
    public async Task CompleteAsync_ThrowsAllProvidersFailedExceptionWhenAllProvidersThrow()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider first = CreateProvider("first", "gpt-4o");
        ILlmProvider second = CreateProvider("second", "gpt-4o");
        first.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new InvalidOperationException("first failed")));
        second.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new InvalidOperationException("second failed")));
        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(first, "gpt-4o"),
            new ProviderRoute(second, "gpt-4o"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        AllProvidersFailedException exception = await Assert.ThrowsAsync<AllProvidersFailedException>(() => service.CompleteAsync(request));

        ProviderCallFailedException providerException = Assert.IsType<ProviderCallFailedException>(exception.InnerException);
        Assert.Equal("second", providerException.ProviderName);
        Assert.Equal("second failed", providerException.InnerException?.Message);
        Assert.Equal(2, exception.Attempts.Count);
        healthTracker.Received(1).RecordFailure("first");
        healthTracker.Received(1).RecordFailure("second");
    }

    [Fact]
    public async Task CompleteAsync_IncludesProviderAndHttpStatusWhenProviderHttpCallFails()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider provider = CreateProvider("Gemini", "gemini-2.5-flash");
        HttpRequestException providerException = new("quota exceeded", null, HttpStatusCode.TooManyRequests);
        provider.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(providerException));
        IProviderRouter router = CreateRouter(request, new ProviderRoute(provider, "gemini-2.5-flash"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        AllProvidersFailedException exception = await Assert.ThrowsAsync<AllProvidersFailedException>(() => service.CompleteAsync(request));

        ProviderCallFailedException callFailedException = Assert.IsType<ProviderCallFailedException>(exception.InnerException);
        Assert.Equal("Gemini", callFailedException.ProviderName);
        Assert.Equal(HttpStatusCode.TooManyRequests, callFailedException.StatusCode);
        Assert.Equal(ProviderErrorKind.RateLimited, callFailedException.ErrorKind);
        Assert.Equal("Gemini failed with HTTP 429 TooManyRequests.", callFailedException.Message);
    }

    [Fact]
    public async Task CompleteAsync_RethrowsOperationCanceledExceptionWithoutFallback()
    {
        ChatRequest request = CreateRequest("gpt-4o");
        ILlmProvider first = CreateProvider("first", "gpt-4o");
        ILlmProvider second = CreateProvider("second", "gpt-4o");
        using CancellationTokenSource cts = new();
        cts.Cancel();

        first.CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException<ChatResponse>(new OperationCanceledException("cancelled", cts.Token)));
        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(first, "gpt-4o"),
            new ProviderRoute(second, "gpt-4o"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CompleteAsync(request, cts.Token));

        await second.DidNotReceive().CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        healthTracker.DidNotReceive().RecordFailure("first");
    }

    [Fact]
    public async Task CompleteStreamAsync_FallsBackBeforeFirstChunkAndEmitsFinalMetrics()
    {
        ChatRequest request = CreateRequest("fast");
        ILlmProvider failingProvider = CreateProvider("Gemini", "gemini-2.5-flash");
        ILlmProvider workingProvider = CreateProvider("OpenAI", "gpt-4o-mini");

        failingProvider.CompleteStreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => FailBeforeFirstChunk());
        workingProvider.CompleteStreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => StreamTextChunks("OpenAI", "gpt-4o-mini", "Hello ", "world!"));

        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(failingProvider, "gemini-2.5-flash"),
            new ProviderRoute(workingProvider, "gpt-4o-mini"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        List<ChatStreamChunk> chunks = [];
        await foreach (ChatStreamChunk chunk in service.CompleteStreamAsync(request))
        {
            chunks.Add(chunk);
        }

        Assert.Equal(3, chunks.Count);
        Assert.Equal("Hello ", chunks[0].Delta);
        Assert.Equal("world!", chunks[1].Delta);
        Assert.True(chunks[2].IsFinal);
        Assert.Equal("OpenAI", chunks[2].Provider);
        Assert.NotNull(chunks[2].TimeToFirstTokenMs);
        Assert.NotNull(chunks[2].EstimatedCostUsd);
        healthTracker.Received(1).RecordFailure("Gemini");
        healthTracker.Received(1).RecordSuccess("OpenAI", Arg.Any<long>());
    }

    [Fact]
    public async Task CompleteStreamAsync_DoesNotFallbackAfterFirstChunkWasYielded()
    {
        ChatRequest request = CreateRequest("fast");
        ILlmProvider partialProvider = CreateProvider("Gemini", "gemini-2.5-flash");
        ILlmProvider backupProvider = CreateProvider("OpenAI", "gpt-4o-mini");

        partialProvider.CompleteStreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => FailMidStream("Gemini", "gemini-2.5-flash"));

        IProviderRouter router = CreateRouter(
            request,
            new ProviderRoute(partialProvider, "gemini-2.5-flash"),
            new ProviderRoute(backupProvider, "gpt-4o-mini"));
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        List<ChatStreamChunk> received = [];
        await Assert.ThrowsAsync<ProviderCallFailedException>(async () =>
        {
            await foreach (ChatStreamChunk chunk in service.CompleteStreamAsync(request))
            {
                received.Add(chunk);
            }
        });

        Assert.Single(received);
        Assert.Equal("partial-", received[0].Delta);
        healthTracker.Received(1).RecordFailure("Gemini");
        backupProvider.DidNotReceive().CompleteStreamAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
    }

    private static async IAsyncEnumerable<ChatStreamChunk> FailBeforeFirstChunk([EnumeratorCancellation] CancellationToken ct = default)
    {
        await Task.Yield();
        throw new HttpRequestException("upstream 503", null, HttpStatusCode.ServiceUnavailable);
#pragma warning disable CS0162
        yield break;
#pragma warning restore CS0162
    }

    private static async IAsyncEnumerable<ChatStreamChunk> FailMidStream(
        string provider,
        string model,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return new ChatStreamChunk("s1", model, provider, "partial-");
        await Task.Yield();
        throw new HttpRequestException("stream dropped", null, HttpStatusCode.BadGateway);
    }

    private static async IAsyncEnumerable<ChatStreamChunk> StreamTextChunks(
        string provider,
        string model,
        params string[] parts)
    {
        foreach (string part in parts)
        {
            await Task.Yield();
            yield return new ChatStreamChunk("s1", model, provider, part);
        }

        yield return new ChatStreamChunk("s1", model, provider, string.Empty, IsFinal: true, Usage: new TokenUsage(10, 5));
    }

    private static ILlmProvider CreateProvider(string name, params string[] models)
    {
        ILlmProvider provider = Substitute.For<ILlmProvider>();
        provider.Name.Returns(name);
        provider.SupportedModels.Returns(new HashSet<string>(models, StringComparer.Ordinal));
        return provider;
    }

    private static IProviderRouter CreateRouter(ChatRequest request, params ProviderRoute[] routes)
    {
        IProviderRouter router = Substitute.For<IProviderRouter>();
        router.Resolve(request).Returns(routes);
        return router;
    }

    private static ChatRequest CreateRequest(string model)
    {
        return new ChatRequest(model, [new Message("user", "hello")]);
    }

    private static ChatResponse CreateResponse(string providerName, string model)
    {
        return new ChatResponse("response-id", model, providerName, "hello", new TokenUsage(100, 50), 10);
    }
}
