namespace Janus.Domain;

/// <summary>
/// Bornes métier partagées entre le modèle de persistance et la validation d'entrée de l'API.
/// Modifier une valeur ici impose une nouvelle migration.
/// </summary>
public static class JanusSchemaLimits
{
    /// <summary>Longueur maximale d'un UID de carte normalisé.</summary>
    public const int CardUidMaxLength = 64;
    /// <summary>Longueur maximale du libellé d'une carte.</summary>
    public const int CardLabelMaxLength = 120;
    /// <summary>Longueur maximale d'un motif d'audit ou d'écriture comptable.</summary>
    public const int AuditReasonMaxLength = 256;
    /// <summary>Longueur maximale d'un type d'écriture comptable.</summary>
    public const int LedgerEntryTypeMaxLength = 32;
    /// <summary>Longueur maximale d'une clé d'idempotence d'écriture comptable.</summary>
    public const int LedgerIdempotencyKeyMaxLength = 160;
    /// <summary>Longueur maximale de l'identité de l'acteur d'une écriture comptable.</summary>
    public const int LedgerActorMaxLength = 80;
    /// <summary>Longueur maximale du code d'une organisation.</summary>
    public const int OrganizationCodeMaxLength = 32;
    /// <summary>Longueur maximale du nom d'une organisation.</summary>
    public const int OrganizationNameMaxLength = 120;
    /// <summary>Longueur maximale du code d'une borne (identique à Validations.TerminalId).</summary>
    public const int TerminalCodeMaxLength = 80;
    /// <summary>Longueur maximale du libellé d'une borne.</summary>
    public const int TerminalLabelMaxLength = 120;
    /// <summary>Longueur d'une empreinte SHA-256 en hexadécimal.</summary>
    public const int Sha256HexLength = 64;
    /// <summary>Longueur maximale d'une action d'audit.</summary>
    public const int AuditActionMaxLength = 32;
}
