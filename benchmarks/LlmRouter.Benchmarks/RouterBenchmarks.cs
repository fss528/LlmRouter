using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Routing;

BenchmarkSwitcher.FromAssembly(typeof(RouterBenchmarks).Assembly).Run(args);

[MemoryDiagnoser]
public class RouterBenchmarks
{
    private ProviderRouter _router = null!;
    private InMemoryHealthTracker _healthTracker = null!;
    private ChatRequest _priorityRequest = null!;
    private ChatRequest _leastLatencyRequest = null!;
    private ChatRequest _roundRobinRequest = null!;
    private ChatRequest _lowestCostRequest = null!;

    [GlobalSetup]
    public void Setup()
    {
        _healthTracker = new InMemoryHealthTracker();
        for (int i = 0; i < 50; i++)
        {
            _healthTracker.RecordSuccess("Anthropic", 180 + i);
            _healthTracker.RecordSuccess("OpenAI", 110 + i);
            _healthTracker.RecordSuccess("Gemini", 75 + i);
        }

        ILlmProvider[] providers =
        [
            new BenchmarkStubProvider("Anthropic", ["claude-haiku-4-5-20251001", "claude-sonnet-4-20250514"]),
            new BenchmarkStubProvider("OpenAI", ["gpt-4o-mini", "gpt-4o"]),
            new BenchmarkStubProvider("Gemini", ["gemini-2.5-flash", "gemini-2.5-pro"])
        ];

        _router = new ProviderRouter(
            providers,
            _healthTracker,
            new ModelRoutingCatalog(),
            new ThreadSafeRoundRobinState());

        Message[] messages = [new Message("user", "Benchmark routing overhead")];
        _priorityRequest = new ChatRequest("fast", messages, Strategy: RoutingStrategy.PriorityWithFallback);
        _leastLatencyRequest = new ChatRequest("fast", messages, Strategy: RoutingStrategy.LeastLatency);
        _roundRobinRequest = new ChatRequest("fast", messages, Strategy: RoutingStrategy.RoundRobin);
        _lowestCostRequest = new ChatRequest("fast", messages, Strategy: RoutingStrategy.LowestCost);
    }

    [Benchmark]
    public IReadOnlyList<ProviderRoute> Resolve_PriorityWithFallback() => _router.Resolve(_priorityRequest);

    [Benchmark]
    public IReadOnlyList<ProviderRoute> Resolve_LeastLatency() => _router.Resolve(_leastLatencyRequest);

    [Benchmark]
    public IReadOnlyList<ProviderRoute> Resolve_RoundRobin() => _router.Resolve(_roundRobinRequest);

    [Benchmark]
    public IReadOnlyList<ProviderRoute> Resolve_LowestCost() => _router.Resolve(_lowestCostRequest);

    [Benchmark]
    public ProviderHealth HealthTracker_RecordAndSnapshot()
    {
        _healthTracker.RecordSuccess("OpenAI", 120);
        return _healthTracker.GetHealth("OpenAI");
    }

    private sealed class BenchmarkStubProvider(string name, IEnumerable<string> models) : ILlmProvider
    {
        public string Name { get; } = name;
        public IReadOnlySet<string> SupportedModels { get; } = new HashSet<string>(models, StringComparer.Ordinal);

        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default) =>
            throw new NotSupportedException();

        public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
            ChatRequest request,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public Task<bool> IsHealthyAsync(CancellationToken ct = default) => Task.FromResult(true);
    }
}
