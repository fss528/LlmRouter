using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Routing;
using LlmRouter.Core.Services;
using NSubstitute;

namespace LlmRouter.Tests;

public sealed class ProviderRouterTests
{
    [Fact]
    public void Resolve_FiltersOutProvidersThatDoNotSupportRequestedModel()
    {
        ILlmProvider supported = CreateProvider("supported", "custom-model-a");
        ILlmProvider unsupported = CreateProvider("unsupported", "custom-model-b");
        IHealthTracker healthTracker = CreateHealthTracker(("supported", CircuitState.Closed), ("unsupported", CircuitState.Closed));
        ProviderRouter router = new([supported, unsupported], healthTracker);

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("custom-model-a"));

        Assert.Single(resolved);
        Assert.Same(supported, resolved[0].Provider);
        Assert.Equal("custom-model-a", resolved[0].Model);
    }

    [Fact]
    public void Resolve_ExcludesProvidersWithOpenCircuit()
    {
        ILlmProvider open = CreateProvider("open", "custom-model");
        ILlmProvider closed = CreateProvider("closed", "custom-model");
        IHealthTracker healthTracker = CreateHealthTracker(("open", CircuitState.Open), ("closed", CircuitState.Closed));
        ProviderRouter router = new([open, closed], healthTracker);

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("custom-model"));

        Assert.Single(resolved);
        Assert.Same(closed, resolved[0].Provider);
    }

    [Fact]
    public void Resolve_LeastLatency_OrdersByP95Ascending()
    {
        ILlmProvider slow = CreateProvider("slow", "custom-model");
        ILlmProvider fast = CreateProvider("fast", "custom-model");
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        healthTracker.GetHealth("slow").Returns(CreateHealth("slow", CircuitState.Closed, p95LatencyMs: 250, latencySampleCount: 5));
        healthTracker.GetHealth("fast").Returns(CreateHealth("fast", CircuitState.Closed, p95LatencyMs: 40, latencySampleCount: 5));
        ProviderRouter router = new([slow, fast], healthTracker);

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("custom-model", RoutingStrategy.LeastLatency));

        Assert.Equal(["fast", "slow"], resolved.Select(route => route.Provider.Name));
    }

    [Fact]
    public void Resolve_LeastLatency_PlacesUnmeasuredZeroMsProvidersAfterMeasuredHealthyProviders()
    {
        ILlmProvider unmeasured = CreateProvider("unmeasured", "custom-model");
        ILlmProvider measured = CreateProvider("measured", "custom-model");
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        healthTracker.GetHealth("unmeasured").Returns(CreateHealth("unmeasured", CircuitState.Closed, p95LatencyMs: 0, latencySampleCount: 0));
        healthTracker.GetHealth("measured").Returns(CreateHealth("measured", CircuitState.Closed, p95LatencyMs: 180, latencySampleCount: 3));
        ProviderRouter router = new([unmeasured, measured], healthTracker);

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("custom-model", RoutingStrategy.LeastLatency));

        Assert.Equal(["measured", "unmeasured"], resolved.Select(route => route.Provider.Name));
    }

    [Fact]
    public void Resolve_DeprioritizesUnhealthyProvidersBelowHealthyProviders()
    {
        ILlmProvider unhealthy = CreateProvider("unhealthy", "custom-model");
        ILlmProvider healthy = CreateProvider("healthy", "custom-model");
        IHealthTracker healthTracker = Substitute.For<IHealthTracker>();
        healthTracker.GetHealth("unhealthy").Returns(new ProviderHealth("unhealthy", IsHealthy: false, 10, 0, DateTimeOffset.UtcNow, CircuitState.Closed, 5));
        healthTracker.GetHealth("healthy").Returns(new ProviderHealth("healthy", IsHealthy: true, 200, 0, DateTimeOffset.UtcNow, CircuitState.Closed, 5));
        ProviderRouter router = new([unhealthy, healthy], healthTracker);

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("custom-model", RoutingStrategy.LeastLatency));

        Assert.Equal(["healthy", "unhealthy"], resolved.Select(route => route.Provider.Name));
    }

    [Fact]
    public void Resolve_RoundRobin_RotatesAcrossSeparateRouterInstancesUsingSharedState()
    {
        ILlmProvider first = CreateProvider("first", "custom-model");
        ILlmProvider second = CreateProvider("second", "custom-model");
        ILlmProvider third = CreateProvider("third", "custom-model");
        IHealthTracker healthTracker = CreateHealthTracker(("first", CircuitState.Closed), ("second", CircuitState.Closed), ("third", CircuitState.Closed));
        ThreadSafeRoundRobinState sharedState = new();
        ChatRequest request = CreateRequest("custom-model", RoutingStrategy.RoundRobin);

        // Simulate separate Scoped DI instances per HTTP request sharing a Singleton IRoundRobinState.
        IReadOnlyList<string> firstResolution = new ProviderRouter([first, second, third], healthTracker, roundRobinState: sharedState)
            .Resolve(request).Select(route => route.Provider.Name).ToList();
        IReadOnlyList<string> secondResolution = new ProviderRouter([first, second, third], healthTracker, roundRobinState: sharedState)
            .Resolve(request).Select(route => route.Provider.Name).ToList();
        IReadOnlyList<string> thirdResolution = new ProviderRouter([first, second, third], healthTracker, roundRobinState: sharedState)
            .Resolve(request).Select(route => route.Provider.Name).ToList();

        Assert.Equal(["first", "second", "third"], firstResolution);
        Assert.Equal(["second", "third", "first"], secondResolution);
        Assert.Equal(["third", "first", "second"], thirdResolution);
    }

    [Fact]
    public void Resolve_LowestCost_OrdersLogicalTierByBlendedTokenCost()
    {
        ILlmProvider anthropic = CreateProvider("Anthropic", "claude-haiku-4-5-20251001");
        ILlmProvider openAi = CreateProvider("OpenAI", "gpt-4o-mini");
        ILlmProvider gemini = CreateProvider("Gemini", "gemini-2.5-flash");
        IHealthTracker healthTracker = CreateHealthTracker(
            ("Anthropic", CircuitState.Closed),
            ("OpenAI", CircuitState.Closed),
            ("Gemini", CircuitState.Closed));

        ProviderRouter router = new([anthropic, openAi, gemini], healthTracker, new ModelRoutingCatalog());

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("fast", RoutingStrategy.LowestCost));

        Assert.Equal(3, resolved.Count);
        // Gemini 2.5 Flash & GPT-4o-mini are cheaper ($0.15/$0.60) than Claude Haiku ($0.80/$4.00)
        Assert.Equal("Anthropic", resolved[^1].Provider.Name);
    }

    [Fact]
    public void Resolve_LogicalAliasAndCrossProviderFallbacks_ReturnConcreteModels()
    {
        ILlmProvider anthropic = CreateProvider("Anthropic", "claude-sonnet-4-20250514");
        ILlmProvider openAi = CreateProvider("OpenAI", "gpt-4o");
        ILlmProvider gemini = CreateProvider("Gemini", "gemini-2.5-pro");
        IHealthTracker healthTracker = CreateHealthTracker(
            ("Anthropic", CircuitState.Closed),
            ("OpenAI", CircuitState.Closed),
            ("Gemini", CircuitState.Closed));

        ProviderRouter router = new([anthropic, openAi, gemini], healthTracker, new ModelRoutingCatalog());

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("gpt-4o", RoutingStrategy.PriorityWithFallback));

        Assert.Equal(
            [("OpenAI", "gpt-4o"), ("Anthropic", "claude-sonnet-4-20250514"), ("Gemini", "gemini-2.5-pro")],
            resolved.Select(route => (route.Provider.Name, route.Model)));
    }

    [Fact]
    public void Resolve_HalfOpen_AllowsOnlySingleConcurrentProbeSlot()
    {
        ILlmProvider halfOpen = CreateProvider("halfOpen", "custom-model");
        ILlmProvider closed = CreateProvider("closed", "custom-model");
        ManualTimeProvider time = new(DateTimeOffset.UtcNow);
        InMemoryHealthTracker timedTracker = new(time);
        for (int i = 0; i < 5; i++)
        {
            timedTracker.RecordFailure("halfOpen");
        }

        time.Advance(TimeSpan.FromSeconds(31));
        ProviderRouter router = new([halfOpen, closed], timedTracker);

        IReadOnlyList<ProviderRoute> firstResolve = router.Resolve(CreateRequest("custom-model", RoutingStrategy.PriorityWithFallback));
        IReadOnlyList<ProviderRoute> secondResolve = router.Resolve(CreateRequest("custom-model", RoutingStrategy.PriorityWithFallback));

        Assert.Equal(["halfOpen", "closed"], firstResolve.Select(r => r.Provider.Name));
        Assert.True(firstResolve[0].IsHalfOpenProbe);
        Assert.Equal(["closed"], secondResolve.Select(r => r.Provider.Name));
    }

    [Fact]
    public void Resolve_ThrowsUnsupportedModelExceptionWhenNoProviderSupportsModel()
    {
        ILlmProvider provider = CreateProvider("provider", "gpt-4o");
        IHealthTracker healthTracker = CreateHealthTracker(("provider", CircuitState.Closed));
        ProviderRouter router = new([provider], healthTracker);

        UnsupportedModelException exception = Assert.Throws<UnsupportedModelException>(() => router.Resolve(CreateRequest("unknown-model")));

        Assert.Equal("unknown-model", exception.Model);
        Assert.Contains("No provider supports model unknown-model", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolve_ReturnsAllModelCompatibleProvidersWhenAllCircuitsAreOpen()
    {
        ILlmProvider first = CreateProvider("first", "custom-model");
        ILlmProvider second = CreateProvider("second", "custom-model");
        IHealthTracker healthTracker = CreateHealthTracker(("first", CircuitState.Open), ("second", CircuitState.Open));
        ProviderRouter router = new([first, second], healthTracker);

        IReadOnlyList<ProviderRoute> resolved = router.Resolve(CreateRequest("custom-model"));

        Assert.Equal(["first", "second"], resolved.Select(route => route.Provider.Name));
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

    private static ProviderHealth CreateHealth(
        string providerName,
        CircuitState circuitState,
        double p95LatencyMs = 0,
        int latencySampleCount = 0)
    {
        return new ProviderHealth(
            providerName,
            circuitState != CircuitState.Open,
            p95LatencyMs,
            0,
            DateTimeOffset.UtcNow,
            circuitState,
            latencySampleCount);
    }

    private static ChatRequest CreateRequest(string model, RoutingStrategy strategy = RoutingStrategy.LeastLatency)
    {
        return new ChatRequest(model, [new Message("user", "hello")], Strategy: strategy);
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;
        public override DateTimeOffset GetUtcNow() => _utcNow;
        public void Advance(TimeSpan duration) => _utcNow = _utcNow.Add(duration);
    }
}
