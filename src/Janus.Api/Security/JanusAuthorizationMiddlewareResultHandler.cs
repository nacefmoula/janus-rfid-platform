using Janus.Api.Observability;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Janus.Api.Security;

public class JanusAuthorizationMiddlewareResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _defaultHandler = new();
    private readonly ILogger<JanusAuthorizationMiddlewareResultHandler> _logger;

    public JanusAuthorizationMiddlewareResultHandler(ILogger<JanusAuthorizationMiddlewareResultHandler> logger)
    {
        _logger = logger;
    }

    public async Task HandleAsync(
        RequestDelegate next,
        HttpContext context,
        AuthorizationPolicy policy,
        PolicyAuthorizationResult authorizeResult)
    {
        var normalizedEndpoint = SecurityPathHelper.NormalizeEndpoint(context.Request.Path);

        if (authorizeResult.Challenged)
        {
            if (!context.Items.ContainsKey(JanusTokenAuthHandler.AuthFailureRecordedItemKey))
            {
                JanusMetrics.SecurityAuthFailuresTotal.Add(1,
                    new KeyValuePair<string, object?>("endpoint", normalizedEndpoint),
                    new KeyValuePair<string, object?>("reason", "missing_token"));

                _logger.LogWarning("Authentication challenge: missing token for endpoint {Endpoint}", normalizedEndpoint);
            }

            // LOT-5A : un jeton de borne connu mais refusé reçoit une raison explicite (le porteur prouve déjà détenir le jeton) ;
            // un jeton inconnu ou absent reste un 401 générique.
            var failureReason = context.Items[JanusTokenAuthHandler.AuthFailureRecordedItemKey] as string;
            var (error, message) = failureReason switch
            {
                "revoked_token" => ("revoked_token", "This terminal has been revoked."),
                "expired_token" => ("expired_token", "This terminal token has expired. Enroll the terminal again."),
                "organization_suspended" => ("organization_suspended", "The organization of this terminal is suspended."),
                _ => ("unauthorized", "Valid authentication credential required.")
            };

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new { error, message }), context.RequestAborted);
            return;
        }

        if (authorizeResult.Forbidden)
        {
            JanusMetrics.SecurityAuthzDenialsTotal.Add(1,
                new KeyValuePair<string, object?>("endpoint", normalizedEndpoint),
                new KeyValuePair<string, object?>("policy", "forbidden"));

            _logger.LogWarning("Authorization denial: user {User} denied on endpoint {Endpoint}",
                context.User.Identity?.Name ?? "anonymous", normalizedEndpoint);

            string message = normalizedEndpoint switch
            {
                "/api/cards/{uid}/audit" => "Terminal credentials cannot view administrative audit logs.",
                "/api/cards/{uid}" when context.Request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase) => "Terminal credentials cannot perform card administration.",
                "/api/validations" => "Terminal credentials required for RFID validations.",
                "/api/demo/cards" => "Terminal credentials cannot access the demo card registry.",
                "/api/dashboard/summary" => "The operational financial summary is restricted to administrators.",
                "/metrics" => "Administrative or scraping credentials required for metrics.",
                _ => "Authenticated identity does not have permission for this resource."
            };

            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonSerializer.Serialize(new
            {
                error = "forbidden",
                message
            }), context.RequestAborted);
            return;
        }

        await _defaultHandler.HandleAsync(next, context, policy, authorizeResult);
    }
}
