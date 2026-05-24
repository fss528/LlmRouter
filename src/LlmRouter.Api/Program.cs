using LlmRouter.Api.Configuration;
using LlmRouter.Api.Endpoints;
using LlmRouter.Api.Middleware;
using Scalar.AspNetCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

builder.Services
    .AddApiInfrastructure()
    .AddLlmRouter();

WebApplication app = builder.Build();

app.UseApiExceptionHandling();
app.UseHttpsRedirection();

app.MapOpenApi(ApiRoutes.OpenApi);
app.MapScalarApiReference();
app.MapChatEndpoints();
app.MapHealthEndpoints();

app.Run();

/// <summary>
/// Marker type used by integration tests that host the Minimal API in-memory.
/// </summary>
public partial class Program;
