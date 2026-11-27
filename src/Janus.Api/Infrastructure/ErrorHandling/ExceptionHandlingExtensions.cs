using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace Janus.Api.Infrastructure.ErrorHandling;

/// <summary>JANUS-SEC-009 : gestion globale des exceptions non gérées.</summary>
public static class ExceptionHandlingExtensions
{
    /// <summary>
    /// Répond en JSON standardisé avec traceId, sans jamais exposer de pile d'appels ni de message interne (qui peut contenir des secrets).
    /// </summary>
    public static IApplicationBuilder UseJanusExceptionHandler(this IApplicationBuilder app)
    {
        app.UseExceptionHandler(exceptionHandlerApp =>
        {
            exceptionHandlerApp.Run(async context =>
            {
                var exceptionHandlerFeature = context.Features.Get<IExceptionHandlerFeature>();
                var exception = exceptionHandlerFeature?.Error;
                var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
                var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

                logger.LogError(exception, "Unhandled exception occurred. TraceId: {TraceId}", traceId);

                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "application/json";

                await context.Response.WriteAsJsonAsync(new
                {
                    error = "internal_error",
                    message = "An unexpected error occurred.",
                    traceId
                });
            });
        });
        return app;
    }
}
