using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Models;
using LlmRouter.Core.Routing;
using LlmRouter.Core.Telemetry;

namespace LlmRouter.Core.Services;

/// <summary>
/// Orchestrates routing, per-attempt timeouts, ordered fallback, SSE streaming,
/// cost accounting, health tracking, and OpenTelemetry diagnostics.
/// </summary>
public sealed class ChatService
{
    private readonly IHealthTracker _healthTracker;
    private readonly Action<string, Exception>? _logWarning;
    private readonly IModelCatalog _modelCatalog;
    private readonly TimeSpan? _providerTimeout;
    private readonly IProviderRouter _router;

    /// <summary>
    /// Creates a chat service. Optional dependencies keep Core free of framework coupling.
    /// </summary>
    public ChatService(
        IProviderRouter router,
        IHealthTracker healthTracker,
        Action<string, Exception>? logWarning = null,
        IModelCatalog? modelCatalog = null,
        TimeSpan? providerTimeout = null)
    {
        _router = router;
        _healthTracker = healthTracker;
        _logWarning = logWarning;
        _modelCatalog = modelCatalog ?? new ModelRoutingCatalog();
        _providerTimeout = providerTimeout;
    }

    /// <summary>
    /// Executes a chat completion against the ordered provider routes until one succeeds.
    /// </summary>
    /// <exception cref="UnsupportedModelException">Thrown when no provider supports the requested model.</exception>
    /// <exception cref="ProviderClientException">Thrown when a provider rejects the request payload as a client error (400/422).</exception>
    /// <exception cref="AllProvidersFailedException">Thrown when all resolved providers fail.</exception>
    public async Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using Activity? rootActivity = RouterTelemetry.ActivitySource.StartActivity("llm.chat");
        rootActivity?.SetTag("llm.requested_model", request.Model);
        rootActivity?.SetTag("llm.strategy", request.Strategy.ToString());
        rootActivity?.SetTag("llm.streaming", false);

        IReadOnlyList<ProviderRoute> routes = _router.Resolve(request);
        HashSet<string> unreleasedHalfOpenProbes = TrackHalfOpenProbes(routes);
        List<ProviderAttempt> attempts = new(routes.Count);
        Exception? lastException = null;
        string? previousFailedProvider = null;
        ProviderErrorKind? previousFailureKind = null;

        try
        {
            foreach (ProviderRoute route in routes)
            {
                if (previousFailedProvider is not null)
                {
                    RouterTelemetry.FallbacksTotal.Add(
                        1,
                        new KeyValuePair<string, object?>("from_provider", previousFailedProvider),
                        new KeyValuePair<string, object?>("to_provider", route.Provider.Name),
                        new KeyValuePair<string, object?>("reason", previousFailureKind?.ToString() ?? "Unknown"));
                }

                using Activity? attemptActivity = RouterTelemetry.ActivitySource.StartActivity("llm.provider.attempt");
                attemptActivity?.SetTag("llm.provider", route.Provider.Name);
                attemptActivity?.SetTag("llm.model", route.Model);
                attemptActivity?.SetTag("llm.attempt_index", attempts.Count);

                Stopwatch stopwatch = Stopwatch.StartNew();
                using CancellationTokenSource? timeoutCts = CreateLinkedTimeoutCts(ct);
                CancellationToken attemptCt = timeoutCts?.Token ?? ct;

                try
                {
                    ChatRequest providerRequest = route.Model == request.Model
                        ? request
                        : request with { Model = route.Model };

                    ChatResponse response = await route.Provider.CompleteAsync(providerRequest, attemptCt).ConfigureAwait(false);
                    stopwatch.Stop();

                    long effectiveLatency = response.LatencyMs > 0 ? response.LatencyMs : stopwatch.ElapsedMilliseconds;
                    _healthTracker.RecordSuccess(route.Provider.Name, effectiveLatency);
                    unreleasedHalfOpenProbes.Remove(route.Provider.Name);

                    string servedModel = string.IsNullOrWhiteSpace(response.Model) ? route.Model : response.Model;
                    decimal? estimatedCost = _modelCatalog.EstimateCost(route.Provider.Name, servedModel, response.Usage)
                        ?? _modelCatalog.EstimateCost(route.Provider.Name, route.Model, response.Usage)
                        ?? response.EstimatedCostUsd;

                    attempts.Add(new ProviderAttempt(route.Provider.Name, servedModel, true, effectiveLatency));
                    RecordSuccessTelemetry(route.Provider.Name, servedModel, request, effectiveLatency, response.Usage, estimatedCost, streaming: false, attemptActivity, rootActivity);

                    return response with
                    {
                        Model = servedModel,
                        LatencyMs = effectiveLatency,
                        RequestedModel = request.Model,
                        EstimatedCostUsd = estimatedCost,
                        Attempts = attempts
                    };
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    attemptActivity?.SetStatus(ActivityStatusCode.Error, "Cancelled by caller");
                    throw;
                }
                catch (ProviderClientException clientEx)
                {
                    attemptActivity?.SetStatus(ActivityStatusCode.Error, clientEx.Message);
                    throw;
                }
                catch (HttpRequestException httpEx) when (IsClientBadRequest(httpEx.StatusCode))
                {
                    attemptActivity?.SetStatus(ActivityStatusCode.Error, httpEx.Message);
                    throw new ProviderClientException(route.Provider.Name, httpEx.StatusCode!.Value, httpEx.Message, httpEx);
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    ProviderCallFailedException wrapped = WrapProviderFailure(route.Provider.Name, ex, timeoutCts);
                    lastException = wrapped;
                    previousFailedProvider = route.Provider.Name;
                    previousFailureKind = wrapped.ErrorKind;

                    _healthTracker.RecordFailure(route.Provider.Name);
                    unreleasedHalfOpenProbes.Remove(route.Provider.Name);

                    attempts.Add(new ProviderAttempt(
                        route.Provider.Name,
                        route.Model,
                        false,
                        stopwatch.ElapsedMilliseconds,
                        wrapped.Message,
                        wrapped.StatusCode is HttpStatusCode code ? (int)code : null));

                    RecordFailureTelemetry(route.Provider.Name, route.Model, stopwatch.ElapsedMilliseconds, wrapped, attemptActivity);
                    _logWarning?.Invoke($"Provider {route.Provider.Name} failed; trying next provider.", wrapped);
                }
            }

            RouterTelemetry.RequestsTotal.Add(
                1,
                new KeyValuePair<string, object?>("strategy", request.Strategy.ToString()),
                new KeyValuePair<string, object?>("requested_model", request.Model),
                new KeyValuePair<string, object?>("outcome", "failed"),
                new KeyValuePair<string, object?>("streaming", false));

            rootActivity?.SetStatus(ActivityStatusCode.Error, "All providers failed");
            throw new AllProvidersFailedException("All providers failed to complete the chat request.", lastException, attempts);
        }
        finally
        {
            ReleaseUnusedHalfOpenProbes(unreleasedHalfOpenProbes);
        }
    }

    /// <summary>
    /// Streams chat completion chunks from the ordered routes.
    /// Falls back transparently if a provider fails before emitting the first chunk.
    /// </summary>
    public async IAsyncEnumerable<ChatStreamChunk> CompleteStreamAsync(
        ChatRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        using Activity? rootActivity = RouterTelemetry.ActivitySource.StartActivity("llm.chat.stream");
        rootActivity?.SetTag("llm.requested_model", request.Model);
        rootActivity?.SetTag("llm.strategy", request.Strategy.ToString());
        rootActivity?.SetTag("llm.streaming", true);

        IReadOnlyList<ProviderRoute> routes = _router.Resolve(request);
        HashSet<string> unreleasedHalfOpenProbes = TrackHalfOpenProbes(routes);
        List<ProviderAttempt> attempts = new(routes.Count);
        Exception? lastException = null;
        string? previousFailedProvider = null;
        ProviderErrorKind? previousFailureKind = null;

        try
        {
            foreach (ProviderRoute route in routes)
            {
                if (previousFailedProvider is not null)
                {
                    RouterTelemetry.FallbacksTotal.Add(
                        1,
                        new KeyValuePair<string, object?>("from_provider", previousFailedProvider),
                        new KeyValuePair<string, object?>("to_provider", route.Provider.Name),
                        new KeyValuePair<string, object?>("reason", previousFailureKind?.ToString() ?? "Unknown"));
                }

                using Activity? attemptActivity = RouterTelemetry.ActivitySource.StartActivity("llm.provider.attempt");
                attemptActivity?.SetTag("llm.provider", route.Provider.Name);
                attemptActivity?.SetTag("llm.model", route.Model);
                attemptActivity?.SetTag("llm.streaming", true);

                Stopwatch stopwatch = Stopwatch.StartNew();
                using CancellationTokenSource? timeoutCts = CreateLinkedTimeoutCts(ct);
                CancellationToken attemptCt = timeoutCts?.Token ?? ct;

                ChatRequest providerRequest = route.Model == request.Model
                    ? request
                    : request with { Model = route.Model };

                IAsyncEnumerator<ChatStreamChunk>? enumerator = null;
                bool hasYieldedChunk = false;
                long? ttftMs = null;
                string streamId = Guid.NewGuid().ToString("N");
                string servedModel = route.Model;
                TokenUsage? latestUsage = null;
                bool completedCleanly = false;

                try
                {
                    enumerator = route.Provider.CompleteStreamAsync(providerRequest, attemptCt).GetAsyncEnumerator(attemptCt);

                    while (true)
                    {
                        ChatStreamChunk? chunkToYield = null;
                        bool moveNextResult;

                        try
                        {
                            moveNextResult = await enumerator.MoveNextAsync().ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            attemptActivity?.SetStatus(ActivityStatusCode.Error, "Cancelled by caller");
                            throw;
                        }
                        catch (ProviderClientException)
                        {
                            throw;
                        }
                        catch (HttpRequestException httpEx) when (IsClientBadRequest(httpEx.StatusCode))
                        {
                            throw new ProviderClientException(route.Provider.Name, httpEx.StatusCode!.Value, httpEx.Message, httpEx);
                        }
                        catch (Exception ex) when (!hasYieldedChunk)
                        {
                            stopwatch.Stop();
                            ProviderCallFailedException wrapped = WrapProviderFailure(route.Provider.Name, ex, timeoutCts);
                            lastException = wrapped;
                            previousFailedProvider = route.Provider.Name;
                            previousFailureKind = wrapped.ErrorKind;

                            _healthTracker.RecordFailure(route.Provider.Name);
                            unreleasedHalfOpenProbes.Remove(route.Provider.Name);

                            attempts.Add(new ProviderAttempt(
                                route.Provider.Name,
                                route.Model,
                                false,
                                stopwatch.ElapsedMilliseconds,
                                wrapped.Message,
                                wrapped.StatusCode is HttpStatusCode code ? (int)code : null));

                            RecordFailureTelemetry(route.Provider.Name, route.Model, stopwatch.ElapsedMilliseconds, wrapped, attemptActivity);
                            _logWarning?.Invoke($"Streaming provider {route.Provider.Name} failed before first chunk; trying next provider.", wrapped);
                            break;
                        }
                        catch (Exception ex)
                        {
                            stopwatch.Stop();
                            ProviderCallFailedException wrapped = WrapProviderFailure(route.Provider.Name, ex, timeoutCts);
                            _healthTracker.RecordFailure(route.Provider.Name);
                            unreleasedHalfOpenProbes.Remove(route.Provider.Name);
                            RecordFailureTelemetry(route.Provider.Name, route.Model, stopwatch.ElapsedMilliseconds, wrapped, attemptActivity);
                            throw wrapped;
                        }

                        if (!moveNextResult)
                        {
                            completedCleanly = true;
                            break;
                        }

                        ChatStreamChunk current = enumerator.Current;
                        if (!string.IsNullOrWhiteSpace(current.Id))
                        {
                            streamId = current.Id;
                        }

                        if (!string.IsNullOrWhiteSpace(current.Model))
                        {
                            servedModel = current.Model;
                        }

                        if (current.Usage is not null)
                        {
                            latestUsage = current.Usage;
                        }

                        if (!hasYieldedChunk)
                        {
                            hasYieldedChunk = true;
                        }

                        if (ttftMs is null && !string.IsNullOrEmpty(current.Delta))
                        {
                            ttftMs = stopwatch.ElapsedMilliseconds;
                            RouterTelemetry.TimeToFirstTokenMs.Record(
                                ttftMs.Value,
                                new KeyValuePair<string, object?>("provider", route.Provider.Name),
                                new KeyValuePair<string, object?>("model", servedModel));
                        }

                        if (!current.IsFinal && !string.IsNullOrEmpty(current.Delta))
                        {
                            chunkToYield = current with
                            {
                                Id = streamId,
                                Model = servedModel,
                                Provider = route.Provider.Name,
                                TimeToFirstTokenMs = ttftMs
                            };
                        }

                        if (chunkToYield is not null)
                        {
                            yield return chunkToYield;
                        }
                    }
                }
                finally
                {
                    if (enumerator is not null)
                    {
                        await enumerator.DisposeAsync().ConfigureAwait(false);
                    }
                }

                if (completedCleanly)
                {
                    stopwatch.Stop();
                    long totalLatency = stopwatch.ElapsedMilliseconds;
                    ttftMs ??= totalLatency;

                    _healthTracker.RecordSuccess(route.Provider.Name, totalLatency);
                    unreleasedHalfOpenProbes.Remove(route.Provider.Name);

                    TokenUsage usage = latestUsage ?? new TokenUsage(0, 0);
                    decimal? estimatedCost = _modelCatalog.EstimateCost(route.Provider.Name, servedModel, usage)
                        ?? _modelCatalog.EstimateCost(route.Provider.Name, route.Model, usage);

                    RecordSuccessTelemetry(route.Provider.Name, servedModel, request, totalLatency, usage, estimatedCost, streaming: true, attemptActivity, rootActivity);

                    yield return new ChatStreamChunk(
                        streamId,
                        servedModel,
                        route.Provider.Name,
                        string.Empty,
                        IsFinal: true,
                        Usage: usage,
                        TimeToFirstTokenMs: ttftMs,
                        LatencyMs: totalLatency,
                        EstimatedCostUsd: estimatedCost);
                    yield break;
                }
            }

            RouterTelemetry.RequestsTotal.Add(
                1,
                new KeyValuePair<string, object?>("strategy", request.Strategy.ToString()),
                new KeyValuePair<string, object?>("requested_model", request.Model),
                new KeyValuePair<string, object?>("outcome", "failed"),
                new KeyValuePair<string, object?>("streaming", true));

            rootActivity?.SetStatus(ActivityStatusCode.Error, "All providers failed");
            throw new AllProvidersFailedException("All providers failed to complete the streaming chat request.", lastException, attempts);
        }
        finally
        {
            ReleaseUnusedHalfOpenProbes(unreleasedHalfOpenProbes);
        }
    }

    private HashSet<string> TrackHalfOpenProbes(IReadOnlyList<ProviderRoute> routes)
    {
        HashSet<string> acquired = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProviderRoute route in routes)
        {
            if (route.IsHalfOpenProbe)
            {
                acquired.Add(route.Provider.Name);
            }
        }

        return acquired;
    }

    private void ReleaseUnusedHalfOpenProbes(HashSet<string> unreleased)
    {
        foreach (string providerName in unreleased)
        {
            _healthTracker.ReleaseHalfOpenProbe(providerName);
        }
    }

    private CancellationTokenSource? CreateLinkedTimeoutCts(CancellationToken ct)
    {
        if (_providerTimeout is not TimeSpan timeout || timeout <= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan)
        {
            return null;
        }

        CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        return cts;
    }

    private ProviderCallFailedException WrapProviderFailure(string providerName, Exception ex, CancellationTokenSource? timeoutCts)
    {
        if (ex is ProviderCallFailedException existing)
        {
            return existing;
        }

        if (ex is OperationCanceledException && timeoutCts is { IsCancellationRequested: true })
        {
            TimeoutException timeoutException = new($"Provider {providerName} timed out after {_providerTimeout?.TotalSeconds:0.#}s.", ex);
            return new ProviderCallFailedException(providerName, timeoutException, ProviderErrorKind.Timeout);
        }

        if (ex is TimeoutException)
        {
            return new ProviderCallFailedException(providerName, ex, ProviderErrorKind.Timeout);
        }

        if (ex is HttpRequestException httpEx)
        {
            ProviderErrorKind kind = ClassifyHttpStatus(httpEx.StatusCode);
            TimeSpan? retryAfter = httpEx.Data["RetryAfter"] as TimeSpan?;
            return new ProviderCallFailedException(providerName, httpEx, kind, retryAfter);
        }

        return new ProviderCallFailedException(providerName, ex, ProviderErrorKind.NetworkOrUnexpected);
    }

    private static bool IsClientBadRequest(HttpStatusCode? statusCode)
    {
        return statusCode is HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity;
    }

    private static ProviderErrorKind ClassifyHttpStatus(HttpStatusCode? statusCode)
    {
        return statusCode switch
        {
            HttpStatusCode.BadRequest or HttpStatusCode.UnprocessableEntity => ProviderErrorKind.ClientError,
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderErrorKind.Authentication,
            HttpStatusCode.TooManyRequests => ProviderErrorKind.RateLimited,
            HttpStatusCode.RequestTimeout or HttpStatusCode.GatewayTimeout => ProviderErrorKind.Timeout,
            HttpStatusCode status when (int)status >= 500 => ProviderErrorKind.ServerError,
            _ => ProviderErrorKind.NetworkOrUnexpected
        };
    }

    private static void RecordSuccessTelemetry(
        string providerName,
        string servedModel,
        ChatRequest request,
        long latencyMs,
        TokenUsage usage,
        decimal? estimatedCost,
        bool streaming,
        Activity? attemptActivity,
        Activity? rootActivity)
    {
        attemptActivity?.SetTag("llm.outcome", "success");
        attemptActivity?.SetTag("llm.latency_ms", latencyMs);

        rootActivity?.SetTag("llm.provider", providerName);
        rootActivity?.SetTag("llm.served_model", servedModel);
        rootActivity?.SetTag("llm.tokens.prompt", usage.PromptTokens);
        rootActivity?.SetTag("llm.tokens.completion", usage.CompletionTokens);
        if (estimatedCost is decimal cost)
        {
            rootActivity?.SetTag("llm.estimated_cost_usd", (double)cost);
            RouterTelemetry.EstimatedCostUsdTotal.Add(
                (double)cost,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", servedModel));
        }

        RouterTelemetry.ProviderAttemptsTotal.Add(
            1,
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("model", servedModel),
            new KeyValuePair<string, object?>("outcome", "success"));

        RouterTelemetry.ProviderDurationMs.Record(
            latencyMs,
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("model", servedModel));

        RouterTelemetry.RequestsTotal.Add(
            1,
            new KeyValuePair<string, object?>("strategy", request.Strategy.ToString()),
            new KeyValuePair<string, object?>("requested_model", request.Model),
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("outcome", "success"),
            new KeyValuePair<string, object?>("streaming", streaming));

        if (usage.PromptTokens > 0)
        {
            RouterTelemetry.TokensTotal.Add(
                usage.PromptTokens,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", servedModel),
                new KeyValuePair<string, object?>("type", "prompt"));
        }

        if (usage.CompletionTokens > 0)
        {
            RouterTelemetry.TokensTotal.Add(
                usage.CompletionTokens,
                new KeyValuePair<string, object?>("provider", providerName),
                new KeyValuePair<string, object?>("model", servedModel),
                new KeyValuePair<string, object?>("type", "completion"));
        }
    }

    private static void RecordFailureTelemetry(
        string providerName,
        string model,
        long latencyMs,
        ProviderCallFailedException ex,
        Activity? attemptActivity)
    {
        attemptActivity?.SetStatus(ActivityStatusCode.Error, ex.Message);
        attemptActivity?.SetTag("llm.error_kind", ex.ErrorKind.ToString());

        RouterTelemetry.ProviderAttemptsTotal.Add(
            1,
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("model", model),
            new KeyValuePair<string, object?>("outcome", ex.ErrorKind.ToString()));

        RouterTelemetry.ProviderDurationMs.Record(
            latencyMs,
            new KeyValuePair<string, object?>("provider", providerName),
            new KeyValuePair<string, object?>("model", model));
    }
}
