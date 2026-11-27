namespace Janus.Domain.Organizations;

/// <summary>Statut d'une organisation cliente (persisté en entier).</summary>
public enum OrganizationStatus
{
    /// <summary>Organisation en service.</summary>
    Active = 0,
    /// <summary>Organisation suspendue : toutes ses bornes sont refusées, même avec un jeton valide.</summary>
    Suspended = 1
}
