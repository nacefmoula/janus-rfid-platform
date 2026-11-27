using Janus.Domain.Organizations;
using Janus.Domain.Terminals;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>
/// Enrôlement des bornes de démonstration (Lot 5A), en Development uniquement, par l'API elle-même :
/// <list type="bullet">
/// <item>les jetons <c>TerminalAuth:*</c> sont déjà fournis au conteneur <c>api</c> ; le migrateur ne reçoit aucun secret de plus ;</item>
/// <item>seule l'empreinte SHA-256 est écrite, avec les droits INSERT du rôle runtime (aucun droit supplémentaire) ;</item>
/// <item>sûr en démarrage concurrent (<c>ON CONFLICT DO NOTHING</c>, audit uniquement si la ligne a été insérée).</item>
/// </list>
/// Si une borne de démo existe déjà avec une autre empreinte (jeton de .env modifié), l'API refuse de démarrer avec un message
/// explicite plutôt que de répondre 401 sans explication. Procédure : README §11.6.
/// </summary>
public static class DemoTerminalEnrollment
{
    /// <summary>Acteur inscrit à l'audit des enrôlements automatiques.</summary>
    public const string BootstrapActor = "DEV-BOOTSTRAP";

    private static readonly DemoTerminal[] DemoTerminals =
    [
        new("TERMINAL-DEMO-01", "TerminalAuth:DemoToken", "TERMINAL_DEMO_TOKEN", "Borne de démonstration 1"),
        new("TERMINAL-DEMO-02", "TerminalAuth:Terminal2Token", "TERMINAL_02_TOKEN", "Borne de démonstration 2")
    ];

    /// <summary>
    /// Enrôle les bornes de démonstration manquantes. Retourne 1 (code de sortie) si une borne de démo existe avec un autre
    /// jeton que celui configuré, sinon null pour poursuivre le démarrage.
    /// </summary>
    public static async Task<int?> RunAsync(WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var logger = app.Logger;

        if (!app.Environment.IsDevelopment())
        {
            if (DemoTerminals.Any(t => !string.IsNullOrWhiteSpace(app.Configuration[t.ConfigKey])))
            {
                logger.LogWarning("TerminalAuth:* is configured but ignored outside Development: terminals authenticate only with tokens enrolled through POST /api/organizations/{{code}}/terminals.");
            }
            return null;
        }

        var lifetimeDays = TerminalTokenLifetime.ResolveDays(app.Configuration["Terminals:TokenLifetimeDays"]);
        var timeProvider = app.Services.GetRequiredService<TimeProvider>();

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JanusDbContext>();

        try
        {
            return await EnrollAllAsync(app, db, timeProvider, lifetimeDays);
        }
        catch (Exception ex) when (IsDatabaseUnavailable(ex))
        {
            // Base injoignable au démarrage : l'API démarre quand même (sondes /health/live et /health/ready) ; la vérification
            // est refaite au prochain démarrage. Une incohérence de jeton, elle, arrête toujours le démarrage (voir ci-dessous).
            logger.LogWarning("Demo terminals not checked at startup: the database is unavailable ({ExceptionType}). Restart the API once PostgreSQL is reachable.", ex.GetType().Name);
            return null;
        }
    }

    private static bool IsDatabaseUnavailable(Exception ex) =>
        ex is NpgsqlException or TimeoutException ||
        (ex is InvalidOperationException && ex.InnerException is NpgsqlException or TimeoutException);

    private static async Task<int?> EnrollAllAsync(WebApplication app, JanusDbContext db, TimeProvider timeProvider, int lifetimeDays)
    {
        var logger = app.Logger;
        foreach (var demo in DemoTerminals)
        {
            var token = app.Configuration[demo.ConfigKey];
            if (string.IsNullOrWhiteSpace(token))
            {
                logger.LogInformation("Demo terminal {TerminalCode} not enrolled: '{ConfigKey}' is not configured.", demo.Code, demo.ConfigKey);
                continue;
            }

            var tokenHash = TerminalToken.ComputeHash(token.Trim());
            var existing = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.Code == demo.Code);
            if (existing is null)
            {
                var now = timeProvider.GetUtcNow();
                await EnrollAsync(db, demo, tokenHash, now, now.AddDays(lifetimeDays));
                existing = await db.Terminals.AsNoTracking().SingleOrDefaultAsync(t => t.Code == demo.Code);
            }

            if (existing is null)
            {
                // Le code est libre mais l'insertion n'a rien produit : l'empreinte est déjà utilisée par une autre borne.
                logger.LogCritical(
                    "Demo terminal {TerminalCode} cannot be enrolled: the token configured in '{ConfigKey}' is already used by another terminal. " +
                    "The API refuses to start so that this terminal never fails silently with 401. Set a distinct value for {EnvVar} in .env, " +
                    "then run 'docker compose up -d api' (no data is lost). See README §11.6.",
                    demo.Code, demo.ConfigKey, demo.EnvVar);
                return 1;
            }

            if (!string.Equals(existing.TokenHash, tokenHash, StringComparison.Ordinal))
            {
                logger.LogCritical(
                    "Demo terminal {TerminalCode} is already enrolled with a different token than the one configured in '{ConfigKey}' " +
                    "(the token in .env was probably changed after the database was created). Enrolled token hashes are immutable. " +
                    "The API refuses to start so that this terminal never fails silently with 401. Fix without deleting data: " +
                    "(1) restore the previous value of {EnvVar} in .env, then run 'docker compose up -d api'; or " +
                    "(2) enroll a new terminal with POST /api/organizations/{OrganizationCode}/terminals and use the returned token. See README §11.6.",
                    demo.Code, demo.ConfigKey, demo.EnvVar, OrganizationDefaults.DemoOrganizationCode);
                return 1;
            }

            var decision = TerminalAccessPolicy.Evaluate(existing.Status, existing.ExpiresAt, OrganizationStatus.Active, timeProvider.GetUtcNow());
            if (TerminalAccessPolicy.ToFailureReason(decision) is { } refusal)
            {
                logger.LogWarning(
                    "Demo terminal {TerminalCode} is enrolled but refused: its requests are answered 401 {Reason}. " +
                    "Enroll a new terminal with POST /api/organizations/{OrganizationCode}/terminals to keep testing. See README §11.6.",
                    demo.Code, refusal, OrganizationDefaults.DemoOrganizationCode);
            }
        }

        return null;
    }

    private static async Task EnrollAsync(JanusDbContext db, DemoTerminal demo, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        await using var tx = await db.Database.BeginTransactionAsync();
        var organizationId = OrganizationDefaults.DemoOrganizationId;
        var actor = BootstrapActor;
        var status = (int)TerminalStatus.Active;

        var inserted = await db.Database.SqlQuery<int>($@"
INSERT INTO ""Terminals"" (""OrganizationId"", ""Code"", ""Label"", ""Status"", ""TokenHash"", ""ExpiresAt"", ""CreatedAt"", ""CreatedBy"")
VALUES ({organizationId}, {demo.Code}, {demo.Label}, {status}, {tokenHash}, {expiresAt}, {now}, {actor})
ON CONFLICT DO NOTHING
RETURNING ""Id"" AS ""Value""").ToListAsync();

        if (inserted.Count == 1)
        {
            db.TerminalAuditLogs.Add(new TerminalAuditLog
            {
                OrganizationId = organizationId,
                TerminalId = inserted[0],
                TerminalCode = demo.Code,
                Action = TerminalAuditActions.Enrolled,
                Reason = "Borne de démonstration enrôlée au démarrage (Development)",
                TokenExpiresAt = expiresAt,
                Actor = actor,
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        await tx.CommitAsync();
    }

    private sealed record DemoTerminal(string Code, string ConfigKey, string EnvVar, string Label);
}
