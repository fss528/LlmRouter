using System.Net;
using System.Runtime.CompilerServices;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;

namespace LlmRouter.Infrastructure.Providers;

/// <summary>
/// Allows demo requests to inject deterministic provider faults (for example via X-Simulate-Failure header)
/// when DemoMode is enabled.
/// </summary>
public interface ISimulatedFaultContext
{
    /// <summary>Provider name requested to fail for the current execution scope, if any.</summary>
    string? FailProvider { get; set; }

    /// <summary>Optional HTTP status code to simulate on failure (defaults to 429).</summary>
    HttpStatusCode FailStatusCode { get; set; }
}

/// <summary>
/// AsyncLocal-backed implementation of <see cref="ISimulatedFaultContext"/>.
/// </summary>
public sealed class AsyncLocalSimulatedFaultContext : ISimulatedFaultContext
{
    private static readonly AsyncLocal<FaultState?> Current = new();

    /// <inheritdoc />
    public string? FailProvider
    {
        get => Current.Value?.FailProvider;
        set
        {
            FaultState state = Current.Value ?? new FaultState();
            state.FailProvider = value;
            Current.Value = state;
        }
    }

    /// <inheritdoc />
    public HttpStatusCode FailStatusCode
    {
        get => Current.Value?.StatusCode ?? HttpStatusCode.TooManyRequests;
        set
        {
            FaultState state = Current.Value ?? new FaultState();
            state.StatusCode = value;
            Current.Value = state;
        }
    }

    private sealed class FaultState
    {
        public string? FailProvider { get; set; }
        public HttpStatusCode StatusCode { get; set; } = HttpStatusCode.TooManyRequests;
    }
}

/// <summary>
/// Deterministic simulated provider used when DemoMode is enabled so the full gateway,
/// fallback, streaming, circuit breaker, Redis state, and OpenTelemetry stack can be exercised without paid API keys.
/// </summary>
public sealed class SimulatedProvider : ILlmProvider
{
    private readonly int _baseLatencyMs;
    private readonly ISimulatedFaultContext? _faultContext;
    private readonly IReadOnlySet<string> _supportedModels;

    /// <summary>
    /// Creates a simulated provider with realistic response latency and token usage.
    /// </summary>
    public SimulatedProvider(
        string name,
        IEnumerable<string> supportedModels,
        int baseLatencyMs,
        ISimulatedFaultContext? faultContext = null)
    {
        Name = name;
        _supportedModels = new HashSet<string>(supportedModels, StringComparer.Ordinal);
        _baseLatencyMs = Math.Max(10, baseLatencyMs);
        _faultContext = faultContext;
    }

    /// <inheritdoc />
    public string Name { get; }

    /// <inheritdoc />
    public IReadOnlySet<string> SupportedModels => _supportedModels;

    /// <inheritdoc />
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        await Task.Delay(_baseLatencyMs, ct).ConfigureAwait(false);
        ThrowIfFaultInjected();

        string promptPreview = request.Messages.LastOrDefault()?.Content ?? string.Empty;
        string responseText = $"[Demo:{Name}/{request.Model}] Simulated response for: \"{Truncate(promptPreview, 80)}\"";
        int promptTokens = Math.Max(8, promptPreview.Length / 4);
        int completionTokens = Math.Max(16, responseText.Length / 4);

        return new ChatResponse(
            Guid.NewGuid().ToString("N"),
            request.Model,
            Name,
            responseText,
            new TokenUsage(promptTokens, completionTokens),
            _baseLatencyMs);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        int ttftDelay = Math.Max(10, _baseLatencyMs / 3);
        await Task.Delay(ttftDelay, ct).ConfigureAwait(false);
        ThrowIfFaultInjected();

        string streamId = Guid.NewGuid().ToString("N");
        string promptPreview = request.Messages.LastOrDefault()?.Content ?? string.Empty;
        string[] tokens =
        [
            $"[Demo:{Name}/{request.Model}] ",
            "Streaming ",
            "multi-provider ",
            "gateway ",
            "response ",
            $"for \"{Truncate(promptPreview, 40)}\"."
        ];

        int perChunkDelay = Math.Max(5, (_baseLatencyMs - ttftDelay) / tokens.Length);
        foreach (string token in tokens)
        {
            ct.ThrowIfCancellationRequested();
            yield return new ChatStreamChunk(streamId, request.Model, Name, token);
            await Task.Delay(perChunkDelay, ct).ConfigureAwait(false);
        }

        int promptTokens = Math.Max(8, promptPreview.Length / 4);
        int completionTokens = tokens.Sum(t => Math.Max(1, t.Length / 4));
        yield return new ChatStreamChunk(
            streamId,
            request.Model,
            Name,
            string.Empty,
            IsFinal: true,
            Usage: new TokenUsage(promptTokens, completionTokens));
    }

    /// <inheritdoc />
    public Task<bool> IsHealthyAsync(CancellationToken ct = default)
    {
        bool healthy = !ShouldFail();
        return Task.FromResult(healthy);
    }

    private bool ShouldFail()
    {
        return _faultContext?.FailProvider is string target &&
               target.Equals(Name, StringComparison.OrdinalIgnoreCase);
    }

    private void ThrowIfFaultInjected()
    {
        if (ShouldFail())
        {
            HttpStatusCode status = _faultContext?.FailStatusCode ?? HttpStatusCode.TooManyRequests;
            throw new HttpRequestException($"Simulated {Name} fault ({(int)status} {status}).", null, status);
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return string.Concat(value.AsSpan(0, maxLength), "...");
    }
}
