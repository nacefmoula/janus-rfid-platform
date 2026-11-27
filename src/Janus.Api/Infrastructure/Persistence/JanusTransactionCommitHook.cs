namespace Janus.Api.Infrastructure.Persistence;

/// <summary>
/// Point d'interception injectable pour les tests d'intégration permettant de simuler de manière déterministe
/// une perte de connexion ou un échec d'acquittement immédiatement après que PostgreSQL a exécuté COMMIT.
/// En production, le délégué est null (aucun effet).
/// </summary>
public sealed class JanusTransactionCommitHook
{
    /// <summary>Délégué invoqué après le COMMIT d'une validation (terminalId, eventId).</summary>
    public Func<string, Guid, CancellationToken, Task>? OnAfterValidationDatabaseCommitAsync { get; set; }
}
