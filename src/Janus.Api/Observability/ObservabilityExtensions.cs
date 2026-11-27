using Janus.Api.Security;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;

namespace Janus.Api.Observability;

/// <summary>Configuration OpenTelemetry (métriques) et exposition Prometheus authentifiée.</summary>
public static class ObservabilityExtensions
{
    /// <summary>Ajoute les instruments ASP.NET Core, runtime, Npgsql et métier, avec export Prometheus.</summary>
    public static IServiceCollection AddJanusObservability(this IServiceCollection services)
    {
        services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService("janus-api", serviceVersion: "1.0.0"))
            .WithMetrics(metrics =>
            {
                metrics
                    .AddAspNetCoreInstrumentation()
                    .AddRuntimeInstrumentation()
                    .AddMeter(JanusMetrics.MeterName)
                    .AddMeter("Npgsql")
                    .AddView(
                        instrumentName: "janus_validation_duration_seconds",
                        new ExplicitBucketHistogramConfiguration
                        {
                            Boundaries = [0.001, 0.002, 0.005, 0.010, 0.025, 0.050, 0.075, 0.100, 0.250, 0.500, 1.0, 2.5, 5.0]
                        })
                    .AddPrometheusExporter();
            });
        return services;
    }

    /// <summary>Expose /metrics, réservé au scraper Prometheus et aux administrateurs.</summary>
    public static IEndpointRouteBuilder MapJanusMetrics(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPrometheusScrapingEndpoint()
            .RequireAuthorization(JanusPolicies.MetricsScrape)
            .RequireRateLimiting(JanusRateLimitPolicies.CardsRead);
        return endpoints;
    }
}
