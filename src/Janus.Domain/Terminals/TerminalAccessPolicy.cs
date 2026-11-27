using Janus.Domain.Organizations;

namespace Janus.Domain.Terminals;

/// <summary>Décision d'accès d'une borne authentifiée par un jeton connu.</summary>
public enum TerminalAccessDecision
{
    /// <summary>Accès autorisé.</summary>
    Allowed,
    /// <summary>Borne révoquée.</summary>
    Revoked,
    /// <summary>Jeton expiré.</summary>
    Expired,
    /// <summary>Organisation de la borne suspendue.</summary>
    OrganizationSuspended
}

/// <summary>Règle d'accès d'une borne. Ordre normatif : révocation, expiration, suspension de l'organisation.</summary>
public static class TerminalAccessPolicy
{
    /// <summary>Évalue l'accès d'une borne à l'instant <paramref name="now"/> (le jeton expire à <c>ExpiresAt</c> inclus).</summary>
    public static TerminalAccessDecision Evaluate(TerminalStatus terminalStatus, DateTimeOffset expiresAt, OrganizationStatus organizationStatus, DateTimeOffset now)
    {
        if (terminalStatus == TerminalStatus.Revoked)
            return TerminalAccessDecision.Revoked;
        if (now >= expiresAt)
            return TerminalAccessDecision.Expired;
        if (organizationStatus == OrganizationStatus.Suspended)
            return TerminalAccessDecision.OrganizationSuspended;
        return TerminalAccessDecision.Allowed;
    }

    /// <summary>Raison bornée (métriques, code d'erreur 401) associée à un refus ; null si l'accès est autorisé.</summary>
    public static string? ToFailureReason(TerminalAccessDecision decision) => decision switch
    {
        TerminalAccessDecision.Revoked => "revoked_token",
        TerminalAccessDecision.Expired => "expired_token",
        TerminalAccessDecision.OrganizationSuspended => "organization_suspended",
        _ => null
    };
}
