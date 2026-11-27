using System.Net;
using System.Text.Json;
using System.Threading.RateLimiting;
using Janus.Api.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;

namespace Janus.Api.Security;

/// <summary>Enregistrement et activation des services de sécurité (proxy de confiance, limitation de débit, authN/authZ).</summary>
public static class SecurityServiceCollectionExtensions
{
    /// <summary>Ajoute ForwardedHeaders strict, rate limiting, authentification Bearer et politiques d'autorisation.</summary>
    public static IServiceCollection AddJanusSecurity(this IServiceCollection services)
    {
        services.AddTrustedForwardedHeaders();
        services.AddJanusRateLimiting();

        // SEC-02: Authentification et Autorisation centralisées ASP.NET Core
        services.AddAuthentication(JanusTokenAuthHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, JanusTokenAuthHandler>(JanusTokenAuthHandler.SchemeName, null);

        services.AddAuthorizationBuilder()
            .AddPolicy(JanusPolicies.TerminalOnly, p => p.RequireRole("Terminal").RequireClaim("permission", "validations:create"))
            .AddPolicy(JanusPolicies.AdminOnly, p => p.RequireRole("Admin").RequireClaim("permission", "cards:admin"))
            .AddPolicy(JanusPolicies.CardsRead, p => p.RequireAssertion(ctx => ctx.User.HasClaim("permission", "cards:read")))
            // LOT-3C / SEC-19 : la synthèse d'exploitation expose des agrégats financiers (masse monétaire, total débité) -> administrateurs uniquement
            .AddPolicy(JanusPolicies.DashboardRead, p => p.RequireRole("Admin").RequireClaim("permission", "dashboard:read"))
            .AddPolicy(JanusPolicies.AuditRead, p => p.RequireRole("Admin").RequireClaim("permission", "audit:read"))
            .AddPolicy(JanusPolicies.MetricsScrape, p => p.RequireAssertion(ctx => ctx.User.HasClaim("permission", "metrics:scrape") || ctx.User.IsInRole("Admin")));

        services.AddSingleton<IAuthorizationMiddlewareResultHandler, JanusAuthorizationMiddlewareResultHandler>();
        return services;
    }

    /// <summary>
    /// Active ForwardedHeaders uniquement si des proxies ou réseaux de confiance sont configurés (REV-03).
    /// À appeler après UseRouting et avant UseAuthentication.
    /// </summary>
    public static IApplicationBuilder UseJanusTrustedForwardedHeaders(this IApplicationBuilder app, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        if (!string.IsNullOrWhiteSpace(configuration["ReverseProxy:TrustedProxies"]) ||
            !string.IsNullOrWhiteSpace(configuration["ReverseProxy:TrustedNetworks"]))
        {
            app.UseWhen(ctx => ctx.Connection.RemoteIpAddress is not null, branch => branch.UseForwardedHeaders());
        }

        return app;
    }

    // REV-03: Configuration stricte de ForwardedHeaders (uniquement proxies et réseaux explicitement approuvés)
    private static void AddTrustedForwardedHeaders(this IServiceCollection services)
    {
        services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, config) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();

            var trustedProxies = config["ReverseProxy:TrustedProxies"];
            if (!string.IsNullOrWhiteSpace(trustedProxies))
            {
                foreach (var ipStr in trustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (IPAddress.TryParse(ipStr, out var ip))
                        options.KnownProxies.Add(ip);
                }
            }

            var trustedNetworks = config["ReverseProxy:TrustedNetworks"];
            if (!string.IsNullOrWhiteSpace(trustedNetworks))
            {
                foreach (var cidr in trustedNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = cidr.Split('/');
                    if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var prefixLen))
                        options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLen));
                }
            }
        });
    }

    // SEC-07 / REV-03 / REV-04: Rate Limiting à deux niveaux (pré-auth par source réseau fiable pour anonymes + post-auth par identité authentifiée)
    // Paramètres initiaux calibrés vis-à-vis du pool Npgsql par défaut (MaxPoolSize=100) :
    // - Validations : 30 req/s soutenu par borne (burst de 60 jetons pour reprise de buffer hors-ligne)
    // - Admin : 15 req/s par identité administrative
    // - Lectures / observation : 60 req/s par identité authentifiée
    private static void AddJanusRateLimiting(this IServiceCollection services)
    {
        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.OnRejected = async (context, token) =>
            {
                context.HttpContext.Response.ContentType = "application/json";
                await context.HttpContext.Response.WriteAsync(
                    JsonSerializer.Serialize(new { error = "rate_limit_exceeded", message = "Too many requests. Please retry later." }),
                    token);
            };

            options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
            {
                // Les requêtes authentifiées sont gouvernées par leurs politiques dédiées par identité (ne partagent jamais le quota anonyme)
                if (httpContext.User.Identity?.IsAuthenticated == true)
                {
                    return RateLimitPartition.GetNoLimiter("authenticated");
                }

                var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";
                return RateLimitPartition.GetSlidingWindowLimiter($"anon:{remoteIp}", _ =>
                    SlidingWindowPerSecond(ConfigurationReader.GetPositiveInt(
                        httpContext.RequestServices.GetRequiredService<IConfiguration>(),
                        "RateLimiting:AnonymousPermitLimit", "ANONYMOUS_PERMIT_LIMIT", 30)));
            });

            options.AddPolicy(JanusRateLimitPolicies.Validations, httpContext =>
            {
                if (httpContext.User.Identity?.IsAuthenticated != true)
                {
                    return RateLimitPartition.GetNoLimiter("unauth_pass_to_authz");
                }

                var partitionKey = $"terminal:{httpContext.User.Identity.Name ?? "unknown_terminal"}";
                return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ =>
                {
                    var cfg = httpContext.RequestServices.GetRequiredService<IConfiguration>();
                    var valLimit = ConfigurationReader.GetPositiveInt(cfg, "RateLimiting:ValidationsPermitLimit", "VALIDATIONS_PERMIT_LIMIT", 30);
                    var valBurst = ConfigurationReader.GetPositiveInt(cfg, "RateLimiting:ValidationsBurstLimit", "VALIDATIONS_BURST_LIMIT", valLimit * 2);
                    return new TokenBucketRateLimiterOptions
                    {
                        TokenLimit = Math.Max(valLimit, valBurst),
                        TokensPerPeriod = valLimit,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                        QueueLimit = 0,
                        AutoReplenishment = true
                    };
                });
            });

            options.AddPolicy(JanusRateLimitPolicies.Admin, httpContext =>
                PerIdentitySlidingWindow(httpContext, "admin", "unknown_admin", "RateLimiting:AdminPermitLimit", "ADMIN_PERMIT_LIMIT", 15));

            options.AddPolicy(JanusRateLimitPolicies.CardsRead, httpContext =>
                PerIdentitySlidingWindow(httpContext, "read", "unknown_reader", "RateLimiting:CardsReadPermitLimit", "CARDS_READ_PERMIT_LIMIT", 60));
        });
    }

    private static RateLimitPartition<string> PerIdentitySlidingWindow(HttpContext httpContext, string prefix, string unknownName, string configKey, string envKey, int defaultLimit)
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return RateLimitPartition.GetNoLimiter("unauth_pass_to_authz");
        }

        var partitionKey = $"{prefix}:{httpContext.User.Identity.Name ?? unknownName}";
        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ =>
            SlidingWindowPerSecond(ConfigurationReader.GetPositiveInt(
                httpContext.RequestServices.GetRequiredService<IConfiguration>(), configKey, envKey, defaultLimit)));
    }

    private static SlidingWindowRateLimiterOptions SlidingWindowPerSecond(int permitLimit) => new()
    {
        PermitLimit = permitLimit,
        Window = TimeSpan.FromSeconds(1),
        SegmentsPerWindow = 2,
        QueueLimit = 0,
        AutoReplenishment = true
    };
}
