namespace Janus.Domain.Terminals;

/// <summary>Statut d'une borne (persisté en entier). La révocation est définitive.</summary>
public enum TerminalStatus
{
    /// <summary>Borne autorisée (sous réserve d'expiration du jeton et du statut de son organisation).</summary>
    Active = 0,
    /// <summary>Borne révoquée : son jeton est refusé définitivement.</summary>
    Revoked = 1
}
