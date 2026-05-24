using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Routing;
using NSubstitute;

namespace LlmRouter.Tests;

public sealed class ProviderRouterTests
{
    [Fact]
    public void Resolve_FiltersOutProvidersThatDoNotSupportRequestedModel()
    {
        ILlmProvider supported = CreateProvider("supported", "gpt-4o");
        ILlmProvider unsupported = CreateProvider("unsupported", "claude-sonnet-4-20250514");
        IHealthTracker healthTracker = CreateHealthTracker(("supported", CircuitState.Closed), ("unsupported", CircuitState.Closed));
        ProviderRouter router = new([supported, unsupported], healthTracker);

        IReadOnlyList<ILlmProvider> resolved = router.Resolve(CreateRequest("gpt-4o"));

        Assert.Single(resolved);
        Assert.Same(supported, resolved[0]);
    }

    [Fact]
    public void Resolve_ExcludesProvidersWithOpenCircuit()
    {
        ILlmProvider open = CreateProvider("open", "gpt-4o");
        ILlmProvider closed = CreateProvider("closed", "gpt-4o");
        IHealthTracker healthTracker = CreateHealthTracker(("open", CircuitState.Open), ("closed", CircuitState.Closed));
        ProviderRouter router = new([open, closed], healthTracker);

        IReadOnlyList<ILlmProvider> resolved = router.Resolve(CreateRequest("gpt-4o"));

        Assert.Single(resolved);
        Assert.Same(closed, resolved[0]);
    }

    [Fact]
    public void Resolve_LeastLatency_OrdersByP95Ascending()
    {
        ILlmProvider slow = CreateProvider("slow", "gpt-4o");
        ILlmProvider fast = CreateProvider("fast", "gpt-4o");
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        healthTracker.GetHealth("slow").Returns(CreateHealth("slow", CircuitState.Closed, p95LatencyMs: 250));
        healthTracker.GetHealth("fast").Returns(CreateHealth("fast", CircuitState.Closed, p95LatencyMs: 40));
        ProviderRouter router = new([slow, fast], healthTracker);

        IReadOnlyList<ILlmProvider> resolved = router.Resolve(CreateRequest("gpt-4o", RoutingStrategy.LeastLatency));

        Assert.Equal(["fast", "slow"], resolved.Select(provider => provider.Name));
    }

    [Fact]
    public void Resolve_RoundRobin_RotatesProvidersThreadSafely()
    {
        ILlmProvider first = CreateProvider("first", "gpt-4o");
        ILlmProvider second = CreateProvider("second", "gpt-4o");
        ILlmProvider third = CreateProvider("third", "gpt-4o");
        IHealthTracker healthTracker = CreateHealthTracker(("first", CircuitState.Closed), ("second", CircuitState.Closed), ("third", CircuitState.Closed));
        ProviderRouter router = new([first, second, third], healthTracker);
        ChatRequest request = CreateRequest("gpt-4o", RoutingStrategy.RoundRobin);

        IReadOnlyList<string> firstResolution = router.Resolve(request).Select(provider => provider.Name).ToList();
        IReadOnlyList<string> secondResolution = router.Resolve(request).Select(provider => provider.Name).ToList();
        IReadOnlyList<string> thirdResolution = router.Resolve(request).Select(provider => provider.Name).ToList();

        Assert.Equal(["first", "second", "third"], firstResolution);
        Assert.Equal(["second", "third", "first"], secondResolution);
        Assert.Equal(["third", "first", "second"], thirdResolution);
    }

    [Fact]
    public void Resolve_ThrowsWhenNoProviderSupportsModel()
    {
        ILlmProvider provider = CreateProvider("provider", "gpt-4o");
        IHealthTracker healthTracker = CreateHealthTracker(("provider", CircuitState.Closed));
        ProviderRouter router = new([provider], healthTracker);

        InvalidOperationException exception = Assert.Throws<InvalidOperationException>(() => router.Resolve(CreateRequest("unknown-model")));

        Assert.Contains("No provider supports model unknown-model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReturnsAllModelCompatibleProvidersWhenAllCircuitsAreOpen()
    {
        ILlmProvider first = CreateProvider("first", "gpt-4o");
        ILlmProvider second = CreateProvider("second", "gpt-4o");
        IHealthTracker healthTracker = CreateHealthTracker(("first", CircuitState.Open), ("second", CircuitState.Open));
        ProviderRouter router = new([first, second], healthTracker);

        IReadOnlyList<ILlmProvider> resolved = router.Resolve(CreateRequest("gpt-4o"));

        Assert.Equal(["first", "second"], resolved.Select(provider => provider.Name));
    }

    private static ILlmProvider CreateProvider(string name, params string[] supportedModels)
    {
        ILlmProvider provider = Substitute.For<ILlmProvider>();
        provider.Name.Returns(name);
        provider.SupportedModels.Returns(new HashSet<string>(supportedModels, StringComparer.Ordinal));
        return provider;
    }

    private static IHealthTracker CreateHealthTracker(params (string ProviderName, CircuitState CircuitState)[] states)
    {
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        foreach ((string providerName, CircuitState circuitState) in states)
        {
            healthTracker.GetHealth(providerName).Returns(CreateHealth(providerName, circuitState));
        }

        return healthTracker;
    }

    private static ProviderHealth CreateHealth(string providerName, CircuitState circuitState, double p95LatencyMs = 0)
    {
        return new ProviderHealth(providerName, circuitState != CircuitState.Open, p95LatencyMs, 0, DateTimeOffset.UtcNow, circuitState);
    }

    private static ChatRequest CreateRequest(string model, RoutingStrategy strategy = RoutingStrategy.LeastLatency)
    {
        return new ChatRequest(model, [new Message("user", "hello")], Strategy: strategy);
    }
}
