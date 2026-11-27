namespace Janus.Domain.Organizations;

/// <summary>Organisation cliente qui émet ses propres cartes et exploite ses propres bornes.</summary>
public sealed class Organization
{
    /// <summary>Identifiant technique.</summary>
    public int Id { get; set; }
    /// <summary>Code public unique et stable (voir <see cref="OrganizationCodes"/>), utilisé dans les routes.</summary>
    public required string Code { get; set; }
    /// <summary>Nom lisible.</summary>
    public required string Name { get; set; }
    /// <summary>Statut d'exploitation.</summary>
    public OrganizationStatus Status { get; set; }
    /// <summary>Horodatage serveur de création.</summary>
    public DateTimeOffset CreatedAt { get; set; }
}
