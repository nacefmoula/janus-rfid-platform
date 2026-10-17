using Microsoft.AspNetCore.Http;

namespace Janus.Api.Security;

/// <summary>
/// Helper for endpoint path normalization ensuring strictly bounded Prometheus metric label cardinality.
/// Prevents card UIDs, query strings, or arbitrary IDs from leaking into Prometheus metric labels.
/// </summary>
public static class SecurityPathHelper
{
    public static string NormalizeEndpoint(PathString path)
    {
        var p = path.Value ?? "";
        if (p.StartsWith("/api/cards/", StringComparison.OrdinalIgnoreCase))
        {
            if (p.EndsWith("/audit", StringComparison.OrdinalIgnoreCase))
                return "/api/cards/{uid}/audit";
            return "/api/cards/{uid}";
        }
        if (p.Equals("/api/validations", StringComparison.OrdinalIgnoreCase))
            return "/api/validations";
        if (p.Equals("/api/demo/cards", StringComparison.OrdinalIgnoreCase))
            return "/api/demo/cards";
        if (p.Equals("/api/simulator/cards", StringComparison.OrdinalIgnoreCase))
            return "/api/simulator/cards";
        if (p.Equals("/api/dashboard/summary", StringComparison.OrdinalIgnoreCase))
            return "/api/dashboard/summary";
        if (p.Equals("/metrics", StringComparison.OrdinalIgnoreCase))
            return "/metrics";
        if (p.Equals("/health", StringComparison.OrdinalIgnoreCase))
            return "/health";

        return "other";
    }
}
