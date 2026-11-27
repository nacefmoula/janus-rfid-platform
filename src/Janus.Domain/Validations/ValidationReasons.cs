namespace Janus.Domain.Validations;

/// <summary>Raisons de décision exposées par l'API et utilisées comme labels de métriques (cardinalité bornée).</summary>
public static class ValidationReasons
{
    /// <summary>Carte active, solde suffisant : passage accepté.</summary>
    public const string ActiveCard = "ACTIVE_CARD";
    /// <summary>UID inconnu.</summary>
    public const string UnknownCard = "UNKNOWN_CARD";
    /// <summary>Carte en opposition.</summary>
    public const string CardRevoked = "CARD_REVOKED";
    /// <summary>Passage accepté récemment sur la même borne (anti-passback).</summary>
    public const string PassbackViolation = "PASSBACK_VIOLATION";
    /// <summary>Solde inférieur au tarif.</summary>
    public const string InsufficientFunds = "INSUFFICIENT_FUNDS";
}

/// <summary>Types d'événements de passage acceptés.</summary>
public static class ValidationEventTypes
{
    /// <summary>Validation de transport (seul type supporté à ce jour).</summary>
    public const string TransportValidation = "TRANSPORT_VALIDATION";
}
