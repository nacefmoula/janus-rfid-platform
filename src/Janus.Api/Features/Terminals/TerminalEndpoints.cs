using System.Security.Claims;
using Janus.Api.Configuration;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Security;
using Janus.Domain;
using Janus.Domain.Organizations;
using Janus.Domain.Terminals;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Janus.Api.Features.Terminals;

/// <summary>
/// Enrôlement et révocation des bornes (administrateur de plateforme). Chaque opération écrit une trace append-only dans
/// <c>TerminalAuditLogs</c> dans la même transaction. Le jeton n'est renvoyé qu'à l'enrôlement et n'est jamais stocké.
/// </summary>
public static class TerminalEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapTerminalEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/organizations/{organizationCode}/terminals", EnrollAsync)
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapGet("/api/organizations/{organizationCode}/terminals", ListAsync)
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapPost("/api/terminals/{terminalCode}/revocation", RevokeAsync)
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        return endpoints;
    }

    private static async Task<IResult> EnrollAsync(
        string organizationCode,
        TerminalEnrollRequest request,
        HttpContext http,
        ClaimsPrincipal user,
        JanusDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        var orgCode = OrganizationCodes.Normalize(organizationCode);
        if (!OrganizationCodes.IsValidOrganizationCode(orgCode))
            return Results.BadRequest(new { error = "invalid_organization_code", message = "Invalid organization code." });

        var code = OrganizationCodes.Normalize(request.Code);
        if (!OrganizationCodes.IsValidTerminalCode(code))
            return Results.BadRequest(new { error = "invalid_terminal_code", message = "Terminal code must be 3 to 80 characters: A-Z, 0-9 and '-', starting with a letter or digit." });

        var label = request.Label?.Trim();
        if (string.IsNullOrEmpty(label) || label.Length > JanusSchemaLimits.TerminalLabelMaxLength)
            return Results.BadRequest(new { error = "invalid_label", message = $"Label must be 1 to {JanusSchemaLimits.TerminalLabelMaxLength} characters." });

        var lifetimeDays = TerminalTokenLifetime.ResolveDays(configuration["Terminals:TokenLifetimeDays"]);
        var actor = ActorOf(user);

        return await InTransactionAsync(db, configuration, async () =>
        {
            var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Code == orgCode);
            if (organization is null)
                return Results.NotFound(new { error = "organization_not_found" });
            if (organization.Status == OrganizationStatus.Suspended)
                return Results.Conflict(new { error = "organization_suspended", message = "Terminals cannot be enrolled in a suspended organization." });

            var now = timeProvider.GetUtcNow();
            var token = TerminalToken.Generate();
            var terminal = new Terminal
            {
                OrganizationId = organization.Id,
                Code = code!,
                Label = label,
                Status = TerminalStatus.Active,
                TokenHash = TerminalToken.ComputeHash(token),
                ExpiresAt = now.AddDays(lifetimeDays),
                CreatedAt = now,
                CreatedBy = actor
            };
            db.Terminals.Add(terminal);
            await db.SaveChangesAsync();

            db.TerminalAuditLogs.Add(new TerminalAuditLog
            {
                OrganizationId = organization.Id,
                TerminalId = terminal.Id,
                TerminalCode = terminal.Code,
                Action = TerminalAuditActions.Enrolled,
                TokenExpiresAt = terminal.ExpiresAt,
                Actor = actor,
                CreatedAt = now
            });
            await db.SaveChangesAsync();

            // Le jeton n'apparaît que dans cette réponse : interdiction de mise en cache par tout intermédiaire.
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers.Pragma = "no-cache";
            return Results.Created($"/api/organizations/{organization.Code}/terminals",
                new TerminalEnrollmentResult(terminal.Code, organization.Code, terminal.Label, terminal.Status.ToString(), terminal.ExpiresAt, terminal.CreatedAt, token));
        }, uniqueViolation: constraint => constraint == "IX_Terminals_Code"
            ? Results.Conflict(new { error = "terminal_code_taken", message = "A terminal with this code already exists on the platform." })
            : null);
    }

    private static async Task<IResult> ListAsync(string organizationCode, JanusDbContext db)
    {
        var orgCode = OrganizationCodes.Normalize(organizationCode);
        if (!OrganizationCodes.IsValidOrganizationCode(orgCode))
            return Results.BadRequest(new { error = "invalid_organization_code", message = "Invalid organization code." });

        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Code == orgCode);
        if (organization is null)
            return Results.NotFound(new { error = "organization_not_found" });

        var terminals = await db.Terminals.AsNoTracking()
            .Where(t => t.OrganizationId == organization.Id)
            .OrderBy(t => t.Code)
            .Select(t => new TerminalDto(t.Code, organization.Code, t.Label, t.Status.ToString(), t.ExpiresAt, t.CreatedAt, t.CreatedBy, t.RevokedAt, t.RevokedBy, t.RevocationReason))
            .ToListAsync();
        return Results.Ok(terminals);
    }

    private static async Task<IResult> RevokeAsync(
        string terminalCode,
        TerminalRevokeRequest request,
        ClaimsPrincipal user,
        JanusDbContext db,
        IConfiguration configuration,
        TimeProvider timeProvider)
    {
        var code = OrganizationCodes.Normalize(terminalCode);
        if (!OrganizationCodes.IsValidTerminalCode(code))
            return Results.BadRequest(new { error = "invalid_terminal_code", message = "Invalid terminal code." });

        var reason = request.Reason?.Trim();
        if (string.IsNullOrEmpty(reason) || reason.Length > JanusSchemaLimits.AuditReasonMaxLength)
            return Results.BadRequest(new { error = "invalid_reason", message = $"A revocation reason (1 to {JanusSchemaLimits.AuditReasonMaxLength} characters) is required." });

        var actor = ActorOf(user);

        return await InTransactionAsync(db, configuration, async () =>
        {
            // Verrou de ligne : deux révocations concurrentes sont sérialisées, la seconde voit l'état révoqué.
            var terminal = await db.Terminals.FromSqlInterpolated($"SELECT * FROM \"Terminals\" WHERE \"Code\" = {code} FOR UPDATE").SingleOrDefaultAsync();
            if (terminal is null)
                return Results.NotFound(new { error = "terminal_not_found" });
            if (terminal.Status == TerminalStatus.Revoked)
                return Results.Conflict(new { error = "terminal_already_revoked", message = "This terminal is already revoked; revocation is irreversible." });

            var now = timeProvider.GetUtcNow();
            terminal.Revoke(actor, reason, now);
            db.TerminalAuditLogs.Add(new TerminalAuditLog
            {
                OrganizationId = terminal.OrganizationId,
                TerminalId = terminal.Id,
                TerminalCode = terminal.Code,
                Action = TerminalAuditActions.Revoked,
                Reason = reason,
                Actor = actor,
                CreatedAt = now
            });
            await db.SaveChangesAsync();

            return Results.Ok(new { code = terminal.Code, status = terminal.Status.ToString(), revokedAt = terminal.RevokedAt, revokedBy = terminal.RevokedBy, reason = terminal.RevocationReason });
        });
    }

    private static string ActorOf(ClaimsPrincipal user) =>
        user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "ADMIN";

    /// <summary>
    /// Transaction courte avec timeouts bornés. Le résultat n'est engagé que si l'action réussit ; un 4xx métier est renvoyé
    /// après ROLLBACK. 23505 → réponse fournie par <paramref name="uniqueViolation"/> ; 55P03 / 57014 → 503 sans nouvelle tentative.
    /// </summary>
    private static async Task<IResult> InTransactionAsync(
        JanusDbContext db,
        IConfiguration configuration,
        Func<Task<IResult>> action,
        Func<string?, IResult?>? uniqueViolation = null)
    {
        var lockTimeoutMs = ConfigurationReader.GetClampedInt(configuration, "Database:LockTimeoutMs", "DATABASE_LOCK_TIMEOUT_MS", 5000, 50, 60000);
        var statementTimeoutMs = ConfigurationReader.GetClampedInt(configuration, "Database:StatementTimeoutMs", "DATABASE_STATEMENT_TIMEOUT_MS", 10000, 100, 120000);

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        try
        {
            await PostgresTransactions.ApplyTimeoutsAsync(db, lockTimeoutMs, statementTimeoutMs);
            var result = await action();
            if (result is IStatusCodeHttpResult { StatusCode: >= 200 and < 300 })
                await tx.CommitAsync();
            else
                await tx.RollbackAsync();
            return result;
        }
        catch (DbUpdateException ex) when (PostgresErrors.TryGetSqlState(ex) == PostgresErrors.UniqueViolation
                                           && uniqueViolation?.Invoke((ex.InnerException as PostgresException)?.ConstraintName) is { } conflict)
        {
            await tx.RollbackAsync();
            return conflict;
        }
        catch (Exception ex) when (PostgresErrors.TryGetSqlState(ex) is PostgresErrors.LockNotAvailable or PostgresErrors.QueryCanceled)
        {
            await tx.RollbackAsync();
            return Results.Json(new { error = "database_busy", message = "The terminal record is temporarily locked. Please retry." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }
    }
}
