using Janus.Domain.Cards;

namespace Janus.Domain.Wallet;

/// <summary>Historique des débits de validation (registre billettique), idempotent sur (TerminalId, EventId).</summary>
public sealed class FareTransaction
{
    /// <summary>Identifiant technique.</summary>
    public long Id { get; set; }
    /// <summary>Organisation propriétaire (immuable, isolation multi-organisation).</summary>
    public int OrganizationId { get; set; }
    /// <summary>Carte débitée.</summary>
    public int CardId { get; set; }
    /// <summary>Navigation vers la carte débitée.</summary>
    public TransitCard? Card { get; set; }
    /// <summary>UID de la carte débitée.</summary>
    public required string CardUid { get; set; }
    /// <summary>Terminal à l'origine du débit.</summary>
    public required string TerminalId { get; set; }
    /// <summary>Événement de validation ayant produit le débit.</summary>
    public Guid EventId { get; set; }
    /// <summary>Montant débité en millimes (positif).</summary>
    public int AmountMillimes { get; set; }
    /// <summary>Solde après débit.</summary>
    public int BalanceAfterMillimes { get; set; }
    /// <summary>Horodatage serveur.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
