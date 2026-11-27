namespace Janus.Domain.Wallet;

/// <summary>
/// Construction des clés d'idempotence du journal comptable (unicité garantie par IX_WalletLedgerEntries_IdempotencyKey).
/// ATTENTION : le format est contractuel ; le modifier permettrait de rejouer deux fois un même mouvement.
/// </summary>
public static class LedgerIdempotencyKeys
{
    /// <summary>Débit d'une validation : un seul débit par (terminal, événement).</summary>
    public static string FareDebit(string terminalId, Guid eventId) => $"fare:{terminalId}:{eventId:N}";

    /// <summary>Crédit administratif : un seul crédit par operationId client.</summary>
    public static string AdminCredit(Guid operationId) => $"credit:{operationId:N}";

    /// <summary>Ouverture ou ajustement administratif absolu : identifiant unique par écriture.</summary>
    public static string AdminSet(Guid writeId) => $"admin-set:{writeId:N}";

    /// <summary>Solde d'ouverture d'une carte de démonstration.</summary>
    public static string SeedOpening(string cardUid) => $"opening:seed:{cardUid}";
}
