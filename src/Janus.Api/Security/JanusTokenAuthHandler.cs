using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Janus.Api.Security;

public class JanusTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "JanusToken";
    public const string AuthFailureRecordedItemKey = "JanusAuthFailureRecorded";

    private readonly IConfiguration _configuration;

    public JanusTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var rawHeader = authHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var trimmedHeader = rawHeader.Trim();
        var normalizedEndpoint = SecurityPathHelper.NormalizeEndpoint(Request.Path);

        // Cas 1 : "Bearer" seul sans jeton (en-tête malformé / Bearer vide)
        if (trimmedHeader.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            RecordAuthFailure(normalizedEndpoint, "malformed_header", "empty Bearer token");
            return Task.FromResult(AuthenticateResult.Fail("Malformed Authorization header: empty Bearer token."));
        }

        // Cas 2 : Schéma d'authentification autre que Bearer (ex: Basic, Digest...)
        if (!trimmedHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RecordAuthFailure(normalizedEndpoint, "non_bearer", "unsupported authentication scheme");
            return Task.FromResult(AuthenticateResult.Fail("Unsupported authentication scheme. Bearer token required."));
        }

        // Cas 3 : Préfixe "Bearer " présent mais jeton vide ou contenant des espaces internes
        var suppliedToken = trimmedHeader[7..].Trim();
        if (string.IsNullOrEmpty(suppliedToken) || suppliedToken.Any(char.IsWhiteSpace))
        {
            RecordAuthFailure(normalizedEndpoint, "malformed_header", "malformed or empty Bearer token value");
            return Task.FromResult(AuthenticateResult.Fail("Malformed Bearer token."));
        }

        var terminalExpected = _configuration["TerminalAuth:DemoToken"];
        var terminal2Expected = _configuration["TerminalAuth:Terminal2Token"];
        var adminExpected = _configuration["AdminAuth:DemoToken"];
        var scrapeExpected = _configuration["MetricsAuth:ScrapeToken"];

        // 1. Terminal 1
        if (CheckToken(suppliedToken, terminalExpected))
        {
            var principal = CreatePrincipal("TERMINAL-DEMO-01", "Terminal", ["validations:create", "cards:read", "dashboard:read"]);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // 2. Terminal 2
        if (CheckToken(suppliedToken, terminal2Expected))
        {
            var principal = CreatePrincipal("TERMINAL-DEMO-02", "Terminal", ["validations:create", "cards:read", "dashboard:read"]);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // 3. Admin
        if (CheckToken(suppliedToken, adminExpected))
        {
            var principal = CreatePrincipal("ADMIN", "Admin", ["cards:admin", "cards:read", "audit:read", "metrics:scrape", "demo:read", "dashboard:read"]);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // 4. Prometheus Scraper dédié (droits minimaux strictement limités à metrics:scrape)
        if (!string.IsNullOrEmpty(scrapeExpected) && CheckToken(suppliedToken, scrapeExpected))
        {
            var principal = CreatePrincipal("PROMETHEUS-SCRAPER", "Scraper", ["metrics:scrape"]);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }

        // Cas 4 : Jeton Bearer inconnu / invalide
        RecordAuthFailure(normalizedEndpoint, "invalid_token", "invalid token");
        return Task.FromResult(AuthenticateResult.Fail("Invalid authentication token."));
    }

    private void RecordAuthFailure(string normalizedEndpoint, string reason, string logDetail)
    {
        Context.Items[AuthFailureRecordedItemKey] = reason;
        JanusMetrics.SecurityAuthFailuresTotal.Add(1,
            new KeyValuePair<string, object?>("endpoint", normalizedEndpoint),
            new KeyValuePair<string, object?>("reason", reason));

        Logger.LogWarning("Authentication failure ({Reason} - {Detail}) for endpoint {Endpoint}",
            reason, logDetail, normalizedEndpoint);
    }

    private static ClaimsPrincipal CreatePrincipal(string name, string role, string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, name),
            new(ClaimTypes.Name, name),
            new(ClaimTypes.Role, role)
        };
        foreach (var perm in permissions)
        {
            claims.Add(new Claim("permission", perm));
        }

        var identity = new ClaimsIdentity(claims, SchemeName);
        return new ClaimsPrincipal(identity);
    }

    private static bool CheckToken(string? supplied, string? expected)
    {
        if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
    }
}
