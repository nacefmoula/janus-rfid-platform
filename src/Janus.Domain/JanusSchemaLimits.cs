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
}
