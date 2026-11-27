namespace Janus.Domain.Validations;

/// <summary>Événement de passage reçu d'un terminal, idempotent sur (TerminalId, EventId).</summary>
public sealed class ValidationEvent
{
    /// <summary>Identifiant technique.</summary>
    public long Id { get; set; }
    /// <summary>Organisation propriétaire (immuable, isolation multi-organisation).</summary>
    public int OrganizationId { get; set; }
    /// <summary>Terminal authentifié à l'origine de l'événement.</summary>
    public required string TerminalId { get; set; }
    /// <summary>Identifiant d'événement généré par le terminal (clé d'idempotence).</summary>
    public Guid EventId { get; set; }
    /// <summary>UID normalisé de la carte présentée.</summary>
    public required string CardUid { get; set; }
    /// <summary>Empreinte SHA-256 du payload canonique, pour distinguer rejeu exact et conflit.</summary>
    public required string PayloadHash { get; set; }
    /// <summary>Type d'événement métier.</summary>
    public required string EventType { get; set; }
    /// <summary>Décision rendue.</summary>
    public ValidationDecision Decision { get; set; }
    /// <summary>Raison de la décision (ACTIVE_CARD, CARD_REVOKED…).</summary>
    public required string Reason { get; set; }
    /// <summary>Horodatage déclaré par le terminal (informatif).</summary>
    public DateTimeOffset? OccurredAt { get; set; }
    /// <summary>Horodatage serveur de réception (référence pour l'anti-passback).</summary>
    public DateTimeOffset ReceivedAt { get; set; }
}
