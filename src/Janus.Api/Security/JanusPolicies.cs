namespace Janus.Api.Security;

/// <summary>Noms des politiques d'autorisation (évite les chaînes magiques dans les endpoints).</summary>
public static class JanusPolicies
{
    /// <summary>Rôle Terminal + permission validations:create.</summary>
    public const string TerminalOnly = "TerminalOnly";
    /// <summary>Rôle Admin + permission cards:admin.</summary>
    public const string AdminOnly = "AdminOnly";
    /// <summary>Permission cards:read (Terminal ou Admin), sans accès aux soldes.</summary>
    public const string CardsRead = "CardsRead";
    /// <summary>Rôle Admin + permission dashboard:read (agrégats financiers).</summary>
    public const string DashboardRead = "DashboardRead";
    /// <summary>Rôle Admin + permission audit:read.</summary>
    public const string AuditRead = "AuditRead";
    /// <summary>Permission metrics:scrape ou rôle Admin.</summary>
    public const string MetricsScrape = "MetricsScrape";
}

/// <summary>Noms des politiques de limitation de débit.</summary>
public static class JanusRateLimitPolicies
{
    /// <summary>Token bucket par terminal authentifié.</summary>
    public const string Validations = "ValidationsPolicy";
    /// <summary>Fenêtre glissante par administrateur.</summary>
    public const string Admin = "AdminPolicy";
    /// <summary>Fenêtre glissante par identité en lecture.</summary>
    public const string CardsRead = "CardsReadPolicy";
}
