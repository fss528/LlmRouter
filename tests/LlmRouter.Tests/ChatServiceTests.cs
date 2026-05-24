using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Services;
using NSubstitute;
using System.Net;

namespace LlmRouter.Tests;

public sealed class ChatServiceTests
{
    [Fact]
    public async Task CompleteAsync_ReturnsResponseFromFirstProviderOnSuccess()
    {
        ChatRequest request = CreateRequest();
        ILlmProvider provider = CreateProvider("first");
        ChatResponse response = CreateResponse("first");
        provider.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromResult(response));
        IProviderRouter router = CreateRouter(request, provider);
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        ChatResponse actual = await service.CompleteAsync(request);

        Assert.Equal(response.Provider, actual.Provider);
        healthTracker.Received(1).RecordSuccess("first", Arg.Any<long>());
        await provider.Received(1).CompleteAsync(request, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task CompleteAsync_FallsBackToSecondProviderWhenFirstThrows()
    {
        ChatRequest request = CreateRequest();
        ILlmProvider first = CreateProvider("first");
        ILlmProvider second = CreateProvider("second");
        first.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromException<ChatResponse>(new InvalidOperationException("boom")));
        second.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromResult(CreateResponse("second")));
        IProviderRouter router = CreateRouter(request, first, second);
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        ChatResponse actual = await service.CompleteAsync(request);

        Assert.Equal("second", actual.Provider);
        healthTracker.Received(1).RecordFailure("first");
        healthTracker.Received(1).RecordSuccess("second", Arg.Any<long>());
    }

    [Fact]
    public async Task CompleteAsync_ThrowsAllProvidersFailedExceptionWhenAllProvidersThrow()
    {
        ChatRequest request = CreateRequest();
        ILlmProvider first = CreateProvider("first");
        ILlmProvider second = CreateProvider("second");
        first.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromException<ChatResponse>(new InvalidOperationException("first failed")));
        second.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromException<ChatResponse>(new InvalidOperationException("second failed")));
        IProviderRouter router = CreateRouter(request, first, second);
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        AllProvidersFailedException exception = await Assert.ThrowsAsync<AllProvidersFailedException>(() => service.CompleteAsync(request));

        ProviderCallFailedException providerException = Assert.IsType<ProviderCallFailedException>(exception.InnerException);
        Assert.Equal("second", providerException.ProviderName);
        Assert.Equal("second failed", providerException.InnerException?.Message);
        healthTracker.Received(1).RecordFailure("first");
        healthTracker.Received(1).RecordFailure("second");
    }

    [Fact]
    public async Task CompleteAsync_IncludesProviderAndHttpStatusWhenProviderHttpCallFails()
    {
        ChatRequest request = CreateRequest();
        ILlmProvider provider = CreateProvider("Gemini");
        HttpRequestException providerException = new("quota exceeded", null, HttpStatusCode.TooManyRequests);
        provider.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromException<ChatResponse>(providerException));
        IProviderRouter router = CreateRouter(request, provider);
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        AllProvidersFailedException exception = await Assert.ThrowsAsync<AllProvidersFailedException>(() => service.CompleteAsync(request));

        ProviderCallFailedException callFailedException = Assert.IsType<ProviderCallFailedException>(exception.InnerException);
        Assert.Equal("Gemini", callFailedException.ProviderName);
        Assert.Equal(HttpStatusCode.TooManyRequests, callFailedException.StatusCode);
        Assert.Equal("Gemini failed with HTTP 429 TooManyRequests.", callFailedException.Message);
    }

    [Fact]
    public async Task CompleteAsync_RethrowsOperationCanceledExceptionWithoutFallback()
    {
        ChatRequest request = CreateRequest();
        ILlmProvider first = CreateProvider("first");
        ILlmProvider second = CreateProvider("second");
        first.CompleteAsync(request, Arg.Any<CancellationToken>()).Returns(Task.FromException<ChatResponse>(new OperationCanceledException("cancelled")));
        IProviderRouter router = CreateRouter(request, first, second);
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        ChatService service = new(router, healthTracker);

        await Assert.ThrowsAsync<OperationCanceledException>(() => service.CompleteAsync(request));

        await second.DidNotReceive().CompleteAsync(Arg.Any<ChatRequest>(), Arg.Any<CancellationToken>());
        healthTracker.DidNotReceive().RecordFailure("first");
    }

    private static ILlmProvider CreateProvider(string name)
    {
        ILlmProvider provider = Substitute.For<ILlmProvider>();
        provider.Name.Returns(name);
        provider.SupportedModels.Returns(new HashSet<string>(["gpt-4o"], StringComparer.Ordinal));
        return provider;
    }

    private static IProviderRouter CreateRouter(ChatRequest request, params ILlmProvider[] providers)
    {
        IProviderRouter router = Substitute.For<IProviderRouter>();
        router.Resolve(request).Returns(providers);
        return router;
    }

    private static ChatRequest CreateRequest()
    {
        return new ChatRequest("gpt-4o", [new Message("user", "hello")]);
    }

    private static ChatResponse CreateResponse(string providerName)
    {
        return new ChatResponse("response-id", "gpt-4o", providerName, "hello", new TokenUsage(1, 1), 1);
    }
}
