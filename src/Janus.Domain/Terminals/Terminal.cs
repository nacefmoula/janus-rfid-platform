namespace Janus.Domain.Terminals;

/// <summary>Borne enrôlée par une organisation. Seule l'empreinte SHA-256 de son jeton est conservée.</summary>
public sealed class Terminal
{
    /// <summary>Identifiant technique.</summary>
    public int Id { get; set; }
    /// <summary>Organisation propriétaire (immuable).</summary>
    public int OrganizationId { get; set; }
    /// <summary>Code unique sur toute la plateforme ; c'est l'identité du terminal dans Validations (idempotence).</summary>
    public required string Code { get; set; }
    /// <summary>Libellé lisible (emplacement, ligne…).</summary>
    public required string Label { get; set; }
    /// <summary>Statut d'exploitation.</summary>
    public TerminalStatus Status { get; set; }
    /// <summary>Empreinte SHA-256 (hex majuscule) du jeton ; le jeton lui-même n'est jamais stocké.</summary>
    public required string TokenHash { get; set; }
    /// <summary>Fin de validité du jeton (obligatoire).</summary>
    public DateTimeOffset ExpiresAt { get; set; }
    /// <summary>Horodatage serveur de l'enrôlement.</summary>
    public DateTimeOffset CreatedAt { get; set; }
    /// <summary>Identité authentifiée de l'acteur qui a enrôlé la borne.</summary>
    public required string CreatedBy { get; set; }
    /// <summary>Horodatage de la révocation.</summary>
    public DateTimeOffset? RevokedAt { get; set; }
    /// <summary>Acteur de la révocation.</summary>
    public string? RevokedBy { get; set; }
    /// <summary>Motif de la révocation.</summary>
    public string? RevocationReason { get; set; }

    /// <summary>Révoque définitivement la borne.</summary>
    /// <exception cref="InvalidOperationException">Borne déjà révoquée.</exception>
    public void Revoke(string actor, string reason, DateTimeOffset at)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(actor);
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        if (Status == TerminalStatus.Revoked)
            throw new InvalidOperationException("Terminal revocation is irreversible and already applied.");
        Status = TerminalStatus.Revoked;
        RevokedAt = at;
        RevokedBy = actor;
        RevocationReason = reason;
    }
}
