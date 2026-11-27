using Janus.Api.Infrastructure.Persistence;

namespace Janus.Api.Features.Health;

/// <summary>Sondes de vivacité et de disponibilité (anonymes).</summary>
public static class HealthEndpoints
{
    /// <summary>Enregistre /health, /health/live, /health/ready et la route de test d'erreur (Development ou option explicite).</summary>
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder endpoints, IHostEnvironment environment, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(configuration);

        endpoints.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "janus-api" }))
            .AllowAnonymous();

        // Liveness probe standardisée (vérifie que le runtime ASP.NET Core répond sans dépendance externe)
        endpoints.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "janus-api" }))
            .AllowAnonymous();

        // JANUS-SEC-011: Sonde de readiness vérifiant la connectivité effective à la base de données PostgreSQL
        endpoints.MapGet("/health/ready", async (JanusDbContext db, ILogger<Program> logger) =>
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                var canConnect = await db.Database.CanConnectAsync(cts.Token);
                if (canConnect)
                {
                    return Results.Ok(new { status = "ready", service = "janus-api", database = "connected" });
                }

                logger.LogWarning("Readiness probe: Database.CanConnectAsync returned false.");
                return Results.Json(new { status = "unhealthy", service = "janus-api", database = "disconnected" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Readiness probe exception: unable to connect to database.");
                return Results.Json(new { status = "unhealthy", service = "janus-api", database = "disconnected", error = "database_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }
        })
            .AllowAnonymous();

        if (environment.IsDevelopment() || configuration.GetValue<bool>("Testing:EnableFaultEndpoint", false))
        {
            endpoints.MapGet("/test-fault/unhandled-exception", () =>
            {
                throw new InvalidOperationException("Simulated catastrophic crash in internal service. SecretValue=SUPER_SECRET_12345");
            }).AllowAnonymous();
        }

        return endpoints;
    }
}
