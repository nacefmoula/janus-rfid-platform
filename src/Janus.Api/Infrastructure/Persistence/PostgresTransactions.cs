using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>Primitives transactionnelles partagées par les écritures financières.</summary>
public static class PostgresTransactions
{
    /// <summary>
    /// Applique lock_timeout et statement_timeout à la transaction courante uniquement (is_local = true),
    /// via des paramètres SQL (aucune interpolation de chaîne).
    /// </summary>
    public static async Task ApplyTimeoutsAsync(JanusDbContext db, int lockTimeoutMs, int statementTimeoutMs)
    {
        ArgumentNullException.ThrowIfNull(db);
        var safeLockMs = $"{Math.Clamp(lockTimeoutMs, 50, 60000)}ms";
        var safeStmtMs = $"{Math.Clamp(statementTimeoutMs, 100, 120000)}ms";
        await db.Database.ExecuteSqlAsync(
            $"SELECT set_config('lock_timeout', {safeLockMs}, true), set_config('statement_timeout', {safeStmtMs}, true);");
    }

    /// <summary>Annule la transaction en ignorant l'erreur d'une connexion déjà interrompue.</summary>
    public static async Task SafeRollbackAsync(IDbContextTransaction? tx, ILogger logger, string operation)
    {
        ArgumentNullException.ThrowIfNull(logger);
        if (tx is null)
        {
            return;
        }

        try
        {
            await tx.RollbackAsync(CancellationToken.None);
        }
        catch (Exception rollbackEx)
        {
            if (logger.IsEnabled(LogLevel.Debug))
            {
                logger.LogDebug(rollbackEx, "Transaction rollback on aborted connection ignored for operation={Operation}", operation);
            }
        }
    }
}
