using Microsoft.AspNetCore.Diagnostics;

namespace LlmRouter.Api.Middleware;

/// <summary>
/// Centralized API exception-to-HTTP response mapping.
/// </summary>
public static class ApiExceptionHandlingExtensions
{
    /// <summary>
    /// Maps framework-level request parsing failures to client errors and keeps unexpected failures as 500.
    /// </summary>
    public static IApplicationBuilder UseApiExceptionHandling(this IApplicationBuilder app)
    {
        return app.UseExceptionHandler(errorApp =>
        {
            errorApp.Run(async context =>
            {
                IExceptionHandlerFeature? exceptionFeature = context.Features.Get<IExceptionHandlerFeature>();
                Exception? exception = exceptionFeature?.Error;

                if (exception is BadHttpRequestException)
                {
                    await Results.Problem(
                            title: "Invalid request body",
                            detail: "Request body must be valid JSON encoded as UTF-8.",
                            statusCode: StatusCodes.Status400BadRequest)
                        .ExecuteAsync(context)
                        .ConfigureAwait(false);
                    return;
                }

                await Results.Problem(
                        title: "An error occurred while processing your request.",
                        statusCode: StatusCodes.Status500InternalServerError)
                    .ExecuteAsync(context)
                    .ConfigureAwait(false);
            });
        });
    }
}
