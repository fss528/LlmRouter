using System.Text.Json.Serialization;
using LlmRouter.Core.Abstractions;
using LlmRouter.Core.Routing;
using LlmRouter.Core.Services;
using LlmRouter.Infrastructure.BackgroundServices;
using LlmRouter.Infrastructure.Providers;

namespace LlmRouter.Api.Configuration;

/// <summary>
/// API composition root registrations.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers HTTP API infrastructure such as JSON, ProblemDetails, and health checks.
    /// </summary>
    public static IServiceCollection AddApiInfrastructure(this IServiceCollection services)
    {
        services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });
        services.AddOpenApi();
        services.AddProblemDetails();
        services.AddHealthChecks();

        return services;
    }

    /// <summary>
    /// Registers router domain services and provider adapters in explicit priority order.
    /// </summary>
    public static IServiceCollection AddLlmRouter(this IServiceCollection services)
    {
        services.AddSingleton<IHealthTracker, InMemoryHealthTracker>();

        // Registration order is the production priority for RoutingStrategy.PriorityWithFallback.
        services.AddHttpClient<AnthropicProvider>();
        services.AddTransient<ILlmProvider>(serviceProvider => serviceProvider.GetRequiredService<AnthropicProvider>());

        services.AddHttpClient<OpenAiProvider>();
        services.AddTransient<ILlmProvider>(serviceProvider => serviceProvider.GetRequiredService<OpenAiProvider>());

        services.AddHttpClient<GeminiProvider>();
        services.AddTransient<ILlmProvider>(serviceProvider => serviceProvider.GetRequiredService<GeminiProvider>());

        services.AddScoped<IProviderRouter, ProviderRouter>();
        services.AddScoped(CreateChatService);
        services.AddHostedService<HealthCheckerService>();

        return services;
    }

    private static ChatService CreateChatService(IServiceProvider serviceProvider)
    {
        IProviderRouter router = serviceProvider.GetRequiredService<IProviderRouter>();
        IHealthTracker healthTracker = serviceProvider.GetRequiredService<IHealthTracker>();
        ILogger<ChatService> logger = serviceProvider.GetRequiredService<ILogger<ChatService>>();

        return new ChatService(
            router,
            healthTracker,
            (message, exception) => logger.LogWarning(exception, "{WarningMessage}", message));
    }
}
