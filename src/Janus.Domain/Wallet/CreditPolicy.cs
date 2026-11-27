using Janus.Domain.Cards;

namespace Janus.Domain.Wallet;

/// <summary>Motifs de refus d'un crédit.</summary>
public enum CreditRejection
{
    /// <summary>Aucun refus.</summary>
    None = 0,
    /// <summary>Identifiant d'opération absent.</summary>
    MissingOperationId,
    /// <summary>Montant hors de [1, plafond].</summary>
    InvalidAmount,
    /// <summary>Motif d'audit absent ou trop long.</summary>
    InvalidReason,
    /// <summary>Carte en opposition.</summary>
    CardRevoked,
    /// <summary>Le solde résultant dépasserait la capacité supportée.</summary>
    BalanceLimitExceeded
}

/// <summary>Règles métier d'un crédit administratif (recharge).</summary>
public static class CreditPolicy
{
    /// <summary>Plafond par défaut d'un crédit unitaire : 1 000 TND.</summary>
    public const int DefaultMaxCreditMillimes = 1_000_000;

    /// <summary>Contrôles indépendants de l'état de la carte (avant toute transaction).</summary>
    public static CreditRejection ValidateRequest(Guid operationId, int amountMillimes, string? reason, int maxCreditMillimes)
    {
        if (operationId == Guid.Empty)
            return CreditRejection.MissingOperationId;
        if (amountMillimes <= 0 || amountMillimes > maxCreditMillimes)
            return CreditRejection.InvalidAmount;
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.Length > JanusSchemaLimits.AuditReasonMaxLength)
            return CreditRejection.InvalidReason;
        return CreditRejection.None;
    }

    /// <summary>Contrôles dépendant de l'état de la carte (sous verrou de ligne).</summary>
    public static CreditRejection ValidateAgainstCard(TransitCard card, int amountMillimes)
    {
        ArgumentNullException.ThrowIfNull(card);
        // Règle métier : aucun mouvement de valeur sur une carte en opposition (réactivation préalable obligatoire).
        if (card.Status == CardStatus.Revoked)
            return CreditRejection.CardRevoked;
        if ((long)card.BalanceMillimes + amountMillimes > int.MaxValue)
            return CreditRejection.BalanceLimitExceeded;
        return CreditRejection.None;
    }
}
