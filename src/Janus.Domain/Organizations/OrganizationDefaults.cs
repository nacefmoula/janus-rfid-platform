namespace Janus.Domain.Organizations;

/// <summary>
/// Organisation de reprise créée par la migration AddOrganizationsAndTerminals : toutes les données antérieures au
/// Lot 5A lui sont rattachées. Valeurs figées (référencées par la migration) : ne jamais les modifier.
/// </summary>
public static class OrganizationDefaults
{
    /// <summary>Identifiant fixe de l'organisation de reprise.</summary>
    public const int DemoOrganizationId = 1;
    /// <summary>Code de l'organisation de reprise.</summary>
    public const string DemoOrganizationCode = "DEMO-TRANSIT";
    /// <summary>Nom de l'organisation de reprise.</summary>
    public const string DemoOrganizationName = "Réseau de démonstration";
}
