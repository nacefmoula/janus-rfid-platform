namespace Janus.Domain.Terminals;

/// <summary>Trace append-only de l'enrôlement et de la révocation d'une borne (jamais de jeton ni d'empreinte).</summary>
public sealed class TerminalAuditLog
{
    /// <summary>Identifiant technique (ordre d'engagement).</summary>
    public long Id { get; set; }
    /// <summary>Organisation de la borne.</summary>
    public int OrganizationId { get; set; }
    /// <summary>Borne concernée.</summary>
    public int TerminalId { get; set; }
    /// <summary>Code de la borne au moment de l'action.</summary>
    public required string TerminalCode { get; set; }
    /// <summary>Action (voir <see cref="TerminalAuditActions"/>).</summary>
    public required string Action { get; set; }
    /// <summary>Motif (obligatoire pour une révocation).</summary>
    public string? Reason { get; set; }
    /// <summary>Fin de validité du jeton émis (enrôlement).</summary>
    public DateTimeOffset? TokenExpiresAt { get; set; }
    /// <summary>Identité authentifiée de l'acteur.</summary>
    public required string Actor { get; set; }
    /// <summary>Horodatage serveur (TimeProvider).</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
