using Janus.Domain.Cards;

namespace Janus.Domain.Wallet;

/// <summary>
/// Écriture du journal comptable : append-only, une ligne par mouvement de solde.
/// Invariant : pour chaque carte, SUM(AmountMillimes) == TransitCard.BalanceMillimes.
/// </summary>
public sealed class WalletLedgerEntry
{
    /// <summary>Identifiant technique (ordre d'engagement).</summary>
    public long Id { get; set; }
    /// <summary>Organisation propriétaire (immuable, isolation multi-organisation).</summary>
    public int OrganizationId { get; set; }
    /// <summary>Carte concernée.</summary>
    public int CardId { get; set; }
    /// <summary>Navigation vers la carte concernée.</summary>
    public TransitCard? Card { get; set; }
    /// <summary>UID de la carte concernée.</summary>
    public required string CardUid { get; set; }
    /// <summary>Type de mouvement (voir <see cref="LedgerEntryTypes"/>).</summary>
    public required string EntryType { get; set; }
    /// <summary>Montant signé en millimes : positif = crédit, négatif = débit. Jamais nul.</summary>
    public int AmountMillimes { get; set; }
    /// <summary>Solde après le mouvement (jamais négatif).</summary>
    public int BalanceAfterMillimes { get; set; }
    /// <summary>Clé d'idempotence unique du mouvement (ex. "fare:{terminalId}:{eventId}", "credit:{operationId}").</summary>
    public required string IdempotencyKey { get; set; }
    /// <summary>Identité authentifiée à l'origine du mouvement (terminal, administrateur, migration…).</summary>
    public required string Actor { get; set; }
    /// <summary>Motif éventuel.</summary>
    public string? Reason { get; set; }
    /// <summary>Horodatage serveur.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
