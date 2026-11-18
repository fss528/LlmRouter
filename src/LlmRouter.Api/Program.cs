using LlmRouter.Api.Configuration;
using LlmRouter.Api.Endpoints;
using LlmRouter.Api.Middleware;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApiInfrastructure(builder.Configuration)
    .AddLlmRouter(builder.Configuration);

WebApplication app = builder.Build();

app.UseApiExceptionHandling();

if (ShouldUseHttpsRedirection(builder.Configuration, app.Environment))
{
    app.UseHttpsRedirection();
}

app.UseRateLimiter();
app.UseMiddleware<GatewayMiddleware>();

app.MapOpenApi(ApiRoutes.OpenApi);
app.MapScalarApiReference();
app.MapChatEndpoints();
app.MapOpenAiCompatibleEndpoints();
app.MapHealthEndpoints();

app.Run();

static bool ShouldUseHttpsRedirection(IConfiguration configuration, IHostEnvironment environment)
{
    if (environment.IsEnvironment("Testing"))
    {
        return false;
    }

    string? urls = configuration["ASPNETCORE_URLS"] ?? configuration["urls"];
    return !string.IsNullOrWhiteSpace(urls) && urls.Contains("https://", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Marker type used by integration tests that host the Minimal API in-memory.
/// </summary>
public partial class Program;
