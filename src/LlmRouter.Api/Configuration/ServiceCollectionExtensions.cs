using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using LlmRouter.Api.Middleware;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Routing;
using LlmRouter.Core.Services;
using LlmRouter.Core.Telemetry;
using LlmRouter.Infrastructure.BackgroundServices;
using LlmRouter.Infrastructure.Configuration;
using LlmRouter.Infrastructure.Health;
using LlmRouter.Infrastructure.Providers;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using StackExchange.Redis;

namespace LlmRouter.Api.Configuration;

/// <summary>
/// API composition root registrations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers HTTP API infrastructure, options validation, rate limiting, OpenTelemetry, and health checks.
    /// </summary>
    public static IServiceCollection AddApiInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddOptions<ProvidersOptions>()
            .Bind(configuration.GetSection(ProvidersOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOptions<RoutingOptions>()
            .Bind(configuration.GetSection(RoutingOptions.SectionName));

        services.AddOptions<StateStoreOptions>()
            .Bind(configuration.GetSection(StateStoreOptions.SectionName));

        services.AddOptions<DemoModeOptions>()
            .Bind(configuration.GetSection(DemoModeOptions.SectionName));

        services.AddOptions<GatewayOptions>()
            .Bind(configuration.GetSection(GatewayOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddOpenApi();
        services.AddProblemDetails();

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("GatewayRateLimit", httpContext =>
            {
                GatewayOptions gateway = httpContext.RequestServices
                    .GetRequiredService<IOptionsMonitor<GatewayOptions>>()
                    .CurrentValue;

                if (!gateway.RateLimit.Enabled)
                {
                    return RateLimitPartition.GetNoLimiter("disabled");
                }

                string partitionKey =
                    httpContext.Request.Headers["X-Api-Key"].FirstOrDefault()
                    ?? httpContext.Connection.RemoteIpAddress?.ToString()
                    ?? "anonymous";

                int permitLimit = Math.Max(1, gateway.RateLimit.PermitLimit);
                TimeSpan window = TimeSpan.FromSeconds(Math.Max(1, gateway.RateLimit.WindowSeconds));

                return RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey,
                    _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = permitLimit,
                        Window = window,
                        QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                        QueueLimit = 0
                    });
            });
        });

        AddOpenTelemetryObservability(services, configuration);

        return services;
    }

    /// <summary>
    /// Registers router domain services, state store (InMemory or Redis), and provider adapters.
    /// </summary>
    public static IServiceCollection AddLlmRouter(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IRoundRobinState, ThreadSafeRoundRobinState>();
        services.AddSingleton<IModelCatalog>(sp =>
        {
            RoutingOptions routingOptions = sp.GetRequiredService<IOptions<RoutingOptions>>().Value;
            return new ModelRoutingCatalog(routingOptions.ToCatalogOptions());
        });

        RegisterHealthStateStore(services, configuration);
        RegisterProviders(services, configuration);

        services.AddScoped<IProviderRouter, ProviderRouter>();
        services.AddScoped(CreateChatService);
        services.AddHostedService<HealthCheckerService>();

        return services;
    }

    private static void RegisterHealthStateStore(IServiceCollection services, IConfiguration configuration)
    {
        StateStoreOptions stateOptions = configuration.GetSection(StateStoreOptions.SectionName).Get<StateStoreOptions>() ?? new();
        string? redisConnectionString = configuration.GetConnectionString("Redis");
        IHealthChecksBuilder healthChecks = services.AddHealthChecks();

        if (stateOptions.Provider.Equals("Redis", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(redisConnectionString))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                ConfigurationOptions redisConfig = ConfigurationOptions.Parse(redisConnectionString);
                redisConfig.AbortOnConnectFail = false;
                return ConnectionMultiplexer.Connect(redisConfig);
            });
            services.AddSingleton<IHealthTracker, RedisHealthTracker>();

            healthChecks.AddCheck("redis", () => HealthCheckResult.Healthy("Redis multiplexer configured."));
            return;
        }

        services.AddSingleton<IHealthTracker, InMemoryHealthTracker>();
    }

    private static void RegisterProviders(IServiceCollection services, IConfiguration configuration)
    {
        ProvidersOptions providersOptions = configuration.GetSection(ProvidersOptions.SectionName).Get<ProvidersOptions>() ?? new();
        TimeSpan httpTimeout = TimeSpan.FromSeconds(Math.Clamp(providersOptions.RequestTimeoutSeconds, 1, 300) + 5);

        services.AddSingleton<ISimulatedFaultContext, AsyncLocalSimulatedFaultContext>();

        services.AddHttpClient<AnthropicProvider>(client => client.Timeout = httpTimeout);
        services.AddHttpClient<OpenAiProvider>(client => client.Timeout = httpTimeout);
        services.AddHttpClient<GeminiProvider>(client => client.Timeout = httpTimeout);

        // Evaluate DemoMode via IOptions at resolution time so WebApplicationFactory overrides and env vars always apply.
        RegisterProviderOrDemo<AnthropicProvider>(
            services,
            "Anthropic",
            ["claude-sonnet-4-20250514", "claude-opus-4-20250514", "claude-haiku-4-5-20251001"],
            demo => demo.AnthropicLatencyMs);

        RegisterProviderOrDemo<OpenAiProvider>(
            services,
            "OpenAI",
            ["gpt-4o", "gpt-4o-mini", "gpt-4-turbo"],
            demo => demo.OpenAiLatencyMs);

        RegisterProviderOrDemo<GeminiProvider>(
            services,
            "Gemini",
            [
                "gemini-2.5-flash", "gemini-2.5-pro", "gemini-2.0-flash",
                "gemini-2.0-flash-001", "gemini-2.0-flash-lite", "gemini-2.0-flash-lite-001",
                "gemini-flash-latest", "gemini-flash-lite-latest", "gemini-pro-latest"
            ],
            demo => demo.GeminiLatencyMs);
    }

    private static void RegisterProviderOrDemo<TProvider>(
        IServiceCollection services,
        string name,
        string[] models,
        Func<DemoModeOptions, int> latencySelector)
        where TProvider : class, ILlmProvider
    {
        services.AddTransient<ILlmProvider>(sp =>
        {
            DemoModeOptions demo = sp.GetRequiredService<IOptions<DemoModeOptions>>().Value;
            if (demo.Enabled)
            {
                return new SimulatedProvider(
                    name,
                    models,
                    latencySelector(demo),
                    sp.GetRequiredService<ISimulatedFaultContext>());
            }

            return sp.GetRequiredService<TProvider>();
        });
    }

    private static ChatService CreateChatService(IServiceProvider serviceProvider)
    {
        IProviderRouter router = serviceProvider.GetRequiredService<IProviderRouter>();
        IHealthTracker healthTracker = serviceProvider.GetRequiredService<IHealthTracker>();
        IModelCatalog modelCatalog = serviceProvider.GetRequiredService<IModelCatalog>();
        ProvidersOptions providersOptions = serviceProvider.GetRequiredService<IOptions<ProvidersOptions>>().Value;
        ILogger<ChatService> logger = serviceProvider.GetRequiredService<ILogger<ChatService>>();

        TimeSpan attemptTimeout = TimeSpan.FromSeconds(Math.Clamp(providersOptions.RequestTimeoutSeconds, 1, 300));

        return new ChatService(
            router,
            healthTracker,
            (message, exception) => logger.LogWarning(exception, "{WarningMessage}", message),
            modelCatalog,
            attemptTimeout);
    }

    private static void AddOpenTelemetryObservability(IServiceCollection services, IConfiguration configuration)
    {
        string? otlpEndpoint = configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]
            ?? Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT");

        var otelBuilder = services.AddOpenTelemetry()
            .ConfigureResource(resource => resource.AddService(
                serviceName: "LlmRouter",
                serviceVersion: "1.0.0"))
            .WithTracing(tracing =>
            {
                tracing
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddSource(RouterTelemetry.ActivitySourceName);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    tracing.AddOtlpExporter();
                }
            })
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddHttpClientInstrumentation()
                    .AddMeter(RouterTelemetry.MeterName);

                if (!string.IsNullOrWhiteSpace(otlpEndpoint))
                {
                    metrics.AddOtlpExporter();
                }
            });
    }
}
