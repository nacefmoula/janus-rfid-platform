using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Domain.Terminals;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
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

/// <summary>
/// Authentification Bearer. Administrateur et collecteur de métriques : jetons de configuration comparés en temps constant.
/// Bornes (Lot 5A) : jeton opaque recherché par son empreinte SHA-256 dans <c>Terminals</c> (aucun cache : une révocation
/// est effective dès son COMMIT), puis règle <see cref="TerminalAccessPolicy"/> (révocation, expiration, organisation suspendue).
/// L'authentification a lieu avant la transaction financière : une requête authentifiée juste avant une révocation peut encore aboutir.
/// </summary>
public class JanusTokenAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "JanusToken";
    public const string AuthFailureRecordedItemKey = "JanusAuthFailureRecorded";

    private readonly IConfiguration _configuration;
    private readonly TimeProvider _timeProvider;

    public JanusTokenAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IConfiguration configuration,
        TimeProvider timeProvider)
        : base(options, logger, encoder)
    {
        _configuration = configuration;
        _timeProvider = timeProvider;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var staticResult = AuthenticateStaticOrRejectMalformed(out var suppliedToken);
        if (staticResult is not null)
        {
            return staticResult;
        }

        return await AuthenticateTerminalAsync(suppliedToken!);
    }

    private AuthenticateResult? AuthenticateStaticOrRejectMalformed(out string? suppliedToken)
    {
        suppliedToken = null;

        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        var rawHeader = authHeaderValues.ToString();
        if (string.IsNullOrWhiteSpace(rawHeader))
        {
            return AuthenticateResult.NoResult();
        }

        var trimmedHeader = rawHeader.Trim();
        var normalizedEndpoint = SecurityPathHelper.NormalizeEndpoint(Request.Path);

        // Cas 1 : "Bearer" seul sans jeton (en-tête malformé / Bearer vide)
        if (trimmedHeader.Equals("Bearer", StringComparison.OrdinalIgnoreCase))
        {
            RecordAuthFailure(normalizedEndpoint, "malformed_header", "empty Bearer token");
            return AuthenticateResult.Fail("Malformed Authorization header: empty Bearer token.");
        }

        // Cas 2 : Schéma d'authentification autre que Bearer (ex: Basic, Digest...)
        if (!trimmedHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            RecordAuthFailure(normalizedEndpoint, "non_bearer", "unsupported authentication scheme");
            return AuthenticateResult.Fail("Unsupported authentication scheme. Bearer token required.");
        }

        // Cas 3 : Préfixe "Bearer " présent mais jeton vide ou contenant des espaces internes
        suppliedToken = trimmedHeader[7..].Trim();
        if (string.IsNullOrEmpty(suppliedToken) || suppliedToken.Any(char.IsWhiteSpace))
        {
            RecordAuthFailure(normalizedEndpoint, "malformed_header", "malformed or empty Bearer token value");
            return AuthenticateResult.Fail("Malformed Bearer token.");
        }

        var adminExpected = _configuration["AdminAuth:DemoToken"];
        var scrapeExpected = _configuration["MetricsAuth:ScrapeToken"];

        // 3. Admin
        if (CheckToken(suppliedToken, adminExpected))
        {
            var principal = CreatePrincipal("ADMIN", "Admin", ["cards:admin", "cards:read", "audit:read", "metrics:scrape", "demo:read", "dashboard:read"]);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return AuthenticateResult.Success(ticket);
        }

        // 4. Prometheus Scraper dédié (droits minimaux strictement limités à metrics:scrape)
        if (!string.IsNullOrEmpty(scrapeExpected) && CheckToken(suppliedToken, scrapeExpected))
        {
            var principal = CreatePrincipal("PROMETHEUS-SCRAPER", "Scraper", ["metrics:scrape"]);
            var ticket = new AuthenticationTicket(principal, SchemeName);
            return AuthenticateResult.Success(ticket);
        }

        // Bornes : résolution en base (voir AuthenticateTerminalAsync)
        return null;
    }

    private async Task<AuthenticateResult> AuthenticateTerminalAsync(string suppliedToken)
    {
        var normalizedEndpoint = SecurityPathHelper.NormalizeEndpoint(Request.Path);
        var tokenHash = TerminalToken.ComputeHash(suppliedToken);
        var db = Context.RequestServices.GetRequiredService<JanusDbContext>();

        var terminal = await db.Terminals.AsNoTracking()
            .Where(t => t.TokenHash == tokenHash)
            .Join(db.Organizations, t => t.OrganizationId, o => o.Id, (t, o) => new
            {
                t.Code,
                t.OrganizationId,
                OrganizationCode = o.Code,
                t.Status,
                t.ExpiresAt,
                OrganizationStatus = o.Status
            })
            .SingleOrDefaultAsync(Context.RequestAborted);

        // Cas 4 : Jeton Bearer inconnu / invalide
        if (terminal is null)
        {
            RecordAuthFailure(normalizedEndpoint, "invalid_token", "invalid token");
            return AuthenticateResult.Fail("Invalid authentication token.");
        }

        var decision = TerminalAccessPolicy.Evaluate(terminal.Status, terminal.ExpiresAt, terminal.OrganizationStatus, _timeProvider.GetUtcNow());
        if (TerminalAccessPolicy.ToFailureReason(decision) is { } reason)
        {
            // Raison bornée (liste fermée) ; le code de la borne n'apparaît que dans le journal, jamais dans les métriques.
            RecordAuthFailure(normalizedEndpoint, reason, $"terminal access refused ({terminal.Code})");
            return AuthenticateResult.Fail("Terminal access refused.");
        }

        var principal = CreatePrincipal(terminal.Code, "Terminal", ["validations:create", "cards:read"]);
        var identity = (ClaimsIdentity)principal.Identity!;
        identity.AddClaim(new Claim(JanusClaims.OrganizationId, terminal.OrganizationId.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        identity.AddClaim(new Claim(JanusClaims.OrganizationCode, terminal.OrganizationCode));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
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
