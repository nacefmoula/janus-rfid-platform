using Janus.Api.Configuration;
using Janus.Api.Observability;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>Messages d'erreur d'une opération financière (renvoyés au client, sans détail interne).</summary>
/// <param name="Busy">Verrou ou instruction expirés (55P03 / 57014) : 503 database_busy, sans nouvelle tentative automatique.</param>
/// <param name="TransientConflictPersisted">Sérialisation ou interblocage persistant après les nouvelles tentatives : 503 database_busy.</param>
/// <param name="RetriesExhausted">Boucle de nouvelles tentatives épuisée : 503 database_busy.</param>
/// <param name="CommitNotPersisted">Commit ambigu, opération introuvable après réconciliation : 503 database_unavailable.</param>
/// <param name="ReconciliationFailed">Commit ambigu et réconciliation impossible : 503 database_unavailable.</param>
public sealed record FinancialOperationMessages(
    string Busy,
    string TransientConflictPersisted,
    string RetriesExhausted,
    string CommitNotPersisted = "The operation could not be committed. Please retry with the same idempotency key.",
    string ReconciliationFailed = "Commit state is ambiguous and reconciliation failed. Please retry with the same idempotency key.");

/// <summary>Résultat de l'étape transactionnelle d'une opération.</summary>
public sealed class FinancialStep
{
    private FinancialStep(IResult? abortResponse, Func<IResult>? onCommitted)
    {
        AbortResponse = abortResponse;
        OnCommitted = onCommitted;
    }

    /// <summary>Réponse à renvoyer après annulation de la transaction (refus métier), ou null.</summary>
    public IResult? AbortResponse { get; }

    /// <summary>Construction de la réponse après COMMIT réussi (métriques, journalisation), ou null.</summary>
    public Func<IResult>? OnCommitted { get; }

    /// <summary>Valide la transaction (SaveChanges + COMMIT), puis construit la réponse.</summary>
    public static FinancialStep Commit(Func<IResult> onCommitted)
    {
        ArgumentNullException.ThrowIfNull(onCommitted);
        return new FinancialStep(null, onCommitted);
    }

    /// <summary>Annule la transaction et renvoie une réponse de refus métier.</summary>
    public static FinancialStep Abort(IResult response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return new FinancialStep(response, null);
    }
}

/// <summary>État mutable d'une tentative, renseigné par l'étape transactionnelle.</summary>
public sealed class FinancialAttempt(int number)
{
    /// <summary>Numéro de tentative (0 = première).</summary>
    public int Number { get; } = number;

    /// <summary>
    /// Une violation d'unicité (23505) lors de cette tentative doit déclencher un unique nouvelle tentative complet
    /// (course de création : la ligne créée par le concurrent sera alors verrouillée).
    /// </summary>
    public bool RetryOnceOnUniqueViolation { get; set; }
}

/// <summary>Description déclarative d'une écriture financière exécutée par <see cref="FinancialTransactionRunner"/>.</summary>
public sealed class FinancialOperation
{
    /// <summary>Nom de l'opération (label de métrique borné : validation, card_upsert, card_credit).</summary>
    public required string Name { get; init; }

    /// <summary>Clé de corrélation journalisée (identifiants techniques, jamais de secret).</summary>
    public required string CorrelationKey { get; init; }

    /// <summary>Étape transactionnelle, exécutée sous transaction READ COMMITTED avec timeouts.</summary>
    public required Func<JanusDbContext, FinancialAttempt, Task<FinancialStep>> ExecuteAsync { get; init; }

    /// <summary>Messages d'erreur renvoyés au client.</summary>
    public required FinancialOperationMessages Messages { get; init; }

    /// <summary>
    /// Recherche d'une opération déjà engagée sous la même clé d'idempotence (pré-contrôle et résolution d'une course 23505).
    /// Retourne la réponse de rejeu ou de conflit, ou null.
    /// </summary>
    public Func<JanusDbContext, Task<IResult?>>? FindCommittedAsync { get; init; }

    /// <summary>
    /// Réconciliation après un échec pendant ou après COMMIT (résultat ambigu), dans un scope DI neuf.
    /// Retourne la réponse si l'opération a été engagée, null sinon. Null = pas de réconciliation (l'erreur est traitée comme avant COMMIT).
    /// </summary>
    public Func<JanusDbContext, Exception, Task<IResult?>>? ReconcileAmbiguousCommitAsync { get; init; }

    /// <summary>Action exécutée juste après COMMIT (point d'injection de panne pour les tests).</summary>
    public Func<Task>? AfterCommitAsync { get; init; }

    /// <summary>Détermine si une exception avant COMMIT révèle une course d'idempotence (23505 par défaut).</summary>
    public Func<Exception, string?, bool> IsIdempotencyRace { get; init; } =
        static (_, sqlState) => string.Equals(sqlState, PostgresErrors.UniqueViolation, StringComparison.Ordinal);
}

/// <summary>
/// Exécution résiliente et unique des écritures financières (validation, mise à jour de carte, crédit) :
/// une tentative par scope DI, transaction READ COMMITTED avec lock_timeout / statement_timeout,
/// nouvelle tentative borné uniquement sur 40001 / 40P01, 503 sans nouvelle tentative sur 55P03 / 57014,
/// et jamais de nouvelle tentative aveugle après COMMIT : réconciliation par clé d'idempotence dans un scope neuf.
/// </summary>
public sealed class FinancialTransactionRunner(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<FinancialTransactionRunner> logger)
{
    /// <summary>Nombre maximal de nouvelles tentatives après la première tentative.</summary>
    public const int MaxTransientRetries = 2;

    /// <summary>Exécute l'opération et retourne la réponse HTTP.</summary>
    public async Task<IResult> ExecuteAsync(FinancialOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var lockTimeoutMs = ConfigurationReader.GetClampedInt(configuration, "Database:LockTimeoutMs", "DATABASE_LOCK_TIMEOUT_MS", 5000, 50, 60000);
        var statementTimeoutMs = ConfigurationReader.GetClampedInt(configuration, "Database:StatementTimeoutMs", "DATABASE_STATEMENT_TIMEOUT_MS", 10000, 100, 120000);

        for (int attemptNumber = 0; attemptNumber <= MaxTransientRetries; attemptNumber++)
        {
            await using var attemptScope = scopeFactory.CreateAsyncScope();
            var db = attemptScope.ServiceProvider.GetRequiredService<JanusDbContext>();

            // Pré-contrôle d'idempotence sur une connexion saine, hors transaction.
            if (operation.FindCommittedAsync is not null)
            {
                var alreadyCommitted = await operation.FindCommittedAsync(db);
                if (alreadyCommitted is not null)
                {
                    return alreadyCommitted;
                }
            }

            var attempt = new FinancialAttempt(attemptNumber);
            await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
            bool commitAttempted = false;
            try
            {
                await PostgresTransactions.ApplyTimeoutsAsync(db, lockTimeoutMs, statementTimeoutMs);

                var step = await operation.ExecuteAsync(db, attempt);
                if (step.AbortResponse is not null)
                {
                    await tx.RollbackAsync(CancellationToken.None);
                    return step.AbortResponse;
                }

                await db.SaveChangesAsync();
                commitAttempted = true;
                await tx.CommitAsync(CancellationToken.None);

                if (operation.AfterCommitAsync is not null)
                {
                    await operation.AfterCommitAsync();
                }

                return step.OnCommitted!();
            }
            catch (Exception ex) when (commitAttempted && operation.ReconcileAmbiguousCommitAsync is not null)
            {
                // Échec pendant ou immédiatement après COMMIT : résultat AMBIGU. Ne jamais réutiliser le DbContext
                // potentiellement corrompu ni rejouer l'écriture à l'aveugle ; vérifier dans un scope neuf.
                IResult? reconciled;
                try
                {
                    await using var reconcileScope = scopeFactory.CreateAsyncScope();
                    var reconcileDb = reconcileScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                    reconciled = await operation.ReconcileAmbiguousCommitAsync(reconcileDb, ex);
                }
                catch (Exception reconcileEx)
                {
                    logger.LogError(reconcileEx,
                        "Reconciliation query failed after ambiguous commit: operation={Operation}, key={CorrelationKey}, attempt={Attempt}",
                        operation.Name, operation.CorrelationKey, attemptNumber);
                    return ServiceUnavailable("database_unavailable", operation.Messages.ReconciliationFailed);
                }

                if (reconciled is not null)
                {
                    logger.LogWarning(ex,
                        "Ambiguous commit reconciled via fresh scope: operation={Operation}, key={CorrelationKey}, attempt={Attempt}, exceptionType={ExceptionType}",
                        operation.Name, operation.CorrelationKey, attemptNumber, ex.GetType().Name);
                    return reconciled;
                }

                // L'opération est introuvable dans un scope sain : le COMMIT n'a pas abouti côté PostgreSQL.
                await PostgresTransactions.SafeRollbackAsync(tx, logger, operation.Name);
                if (attemptNumber == 0)
                {
                    JanusMetrics.RecordDbTransactionEvent(operation.Name, "transient_retry");
                    logger.LogWarning(ex,
                        "Commit did not persist in PostgreSQL; retrying once in a new scope: operation={Operation}, key={CorrelationKey}, outcome=transient_retry, attempt={Attempt}",
                        operation.Name, operation.CorrelationKey, attemptNumber);
                    await Task.Delay(TimeSpan.FromMilliseconds(20));
                    continue;
                }

                return ServiceUnavailable("database_unavailable", operation.Messages.CommitNotPersisted);
            }
            catch (Exception ex)
            {
                // Échec AVANT COMMIT (ou opération sans réconciliation) : aucune écriture engagée par cette tentative.
                await PostgresTransactions.SafeRollbackAsync(tx, logger, operation.Name);
                var sqlState = PostgresErrors.TryGetSqlState(ex);

                if (string.Equals(sqlState, PostgresErrors.UniqueViolation, StringComparison.Ordinal) &&
                    attempt.RetryOnceOnUniqueViolation && attemptNumber == 0)
                {
                    JanusMetrics.RecordDbTransactionEvent(operation.Name, "conflict_recovered");
                    logger.LogWarning(
                        "Concurrent creation collision resolved by retrying under row lock: operation={Operation}, key={CorrelationKey}, sqlState={SqlState}, outcome=conflict_recovered, attempt={Attempt}",
                        operation.Name, operation.CorrelationKey, sqlState, attemptNumber);
                    continue;
                }

                if (operation.FindCommittedAsync is not null && operation.IsIdempotencyRace(ex, sqlState))
                {
                    await using var raceScope = scopeFactory.CreateAsyncScope();
                    var raceDb = raceScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                    var winner = await operation.FindCommittedAsync(raceDb);
                    if (winner is not null)
                    {
                        JanusMetrics.RecordDbTransactionEvent(operation.Name, "conflict_recovered");
                        logger.LogWarning(
                            "Concurrent idempotent race resolved via fresh scope: operation={Operation}, key={CorrelationKey}, sqlState={SqlState}, outcome=conflict_recovered, attempt={Attempt}",
                            operation.Name, operation.CorrelationKey, sqlState ?? ex.GetType().Name, attemptNumber);
                        return winner;
                    }
                }

                if (string.Equals(sqlState, PostgresErrors.LockNotAvailable, StringComparison.Ordinal) ||
                    string.Equals(sqlState, PostgresErrors.QueryCanceled, StringComparison.Ordinal))
                {
                    JanusMetrics.RecordDbTransactionEvent(operation.Name, "lock_timeout");
                    logger.LogWarning(
                        "Database lock or statement timeout (no automatic retry): operation={Operation}, key={CorrelationKey}, sqlState={SqlState}, outcome=lock_timeout, attempt={Attempt}",
                        operation.Name, operation.CorrelationKey, sqlState, attemptNumber);
                    return ServiceUnavailable("database_busy", operation.Messages.Busy);
                }

                if (string.Equals(sqlState, PostgresErrors.SerializationFailure, StringComparison.Ordinal) ||
                    string.Equals(sqlState, PostgresErrors.DeadlockDetected, StringComparison.Ordinal))
                {
                    if (attemptNumber < MaxTransientRetries)
                    {
                        JanusMetrics.RecordDbTransactionEvent(operation.Name, "transient_retry");
                        logger.LogWarning(
                            "Transient serialization or deadlock conflict; retrying in a new scope: operation={Operation}, key={CorrelationKey}, sqlState={SqlState}, outcome=transient_retry, attempt={Attempt}",
                            operation.Name, operation.CorrelationKey, sqlState, attemptNumber);
                        await Task.Delay(TimeSpan.FromMilliseconds(15 * (attemptNumber + 1)));
                        continue;
                    }

                    return ServiceUnavailable("database_busy", operation.Messages.TransientConflictPersisted);
                }

                logger.LogError(ex, "Unrecoverable error during {Operation}: key={CorrelationKey}, sqlState={SqlState}, attempt={Attempt}",
                    operation.Name, operation.CorrelationKey, sqlState, attemptNumber);
                throw;
            }
        }

        return ServiceUnavailable("database_busy", operation.Messages.RetriesExhausted);
    }

    private static IResult ServiceUnavailable(string error, string message) =>
        Results.Json(new { error, message }, statusCode: StatusCodes.Status503ServiceUnavailable);
}

/// <summary>Enregistrement du runner transactionnel.</summary>
public static class FinancialTransactionRunnerServiceCollectionExtensions
{
    /// <summary>Ajoute <see cref="FinancialTransactionRunner"/> (scoped : un par requête).</summary>
    public static IServiceCollection AddFinancialTransactionRunner(this IServiceCollection services) =>
        services.AddScoped<FinancialTransactionRunner>();
}
