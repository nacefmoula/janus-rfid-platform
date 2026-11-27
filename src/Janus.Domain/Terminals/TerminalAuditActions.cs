namespace Janus.Domain.Terminals;

/// <summary>Actions du journal d'audit des bornes (liste fermée, contrainte CHECK en base).</summary>
public static class TerminalAuditActions
{
    /// <summary>Enrôlement d'une borne et émission de son jeton.</summary>
    public const string Enrolled = "TERMINAL_ENROLLED";
    /// <summary>Révocation définitive d'une borne.</summary>
    public const string Revoked = "TERMINAL_REVOKED";
}
