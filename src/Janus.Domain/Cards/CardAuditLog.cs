namespace Janus.Domain.Cards;

/// <summary>Trace d'audit administrative append-only (création, mise à jour, crédit d'une carte).</summary>
public sealed class CardAuditLog
{
    /// <summary>Identifiant technique (ordre d'engagement).</summary>
    public long Id { get; set; }
    /// <summary>Organisation propriétaire (immuable, isolation multi-organisation).</summary>
    public int OrganizationId { get; set; }
    /// <summary>UID de la carte concernée.</summary>
    public required string CardUid { get; set; }
    /// <summary>Action réalisée (ADMIN_CARD_CREATED, ADMIN_CARD_UPDATED, ADMIN_CARD_CREDITED).</summary>
    public required string Action { get; set; }
    /// <summary>Solde avant l'opération (null à la création).</summary>
    public int? PreviousBalanceMillimes { get; set; }
    /// <summary>Solde après l'opération.</summary>
    public int NewBalanceMillimes { get; set; }
    /// <summary>Statut avant l'opération (null à la création).</summary>
    public string? PreviousStatus { get; set; }
    /// <summary>Statut après l'opération.</summary>
    public required string NewStatus { get; set; }
    /// <summary>Motif déclaré par l'administrateur.</summary>
    public string? Reason { get; set; }
    /// <summary>Identité authentifiée de l'acteur.</summary>
    public required string Actor { get; set; }
    /// <summary>Horodatage serveur.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
