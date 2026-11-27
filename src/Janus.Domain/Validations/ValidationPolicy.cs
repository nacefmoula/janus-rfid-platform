using Janus.Domain.Cards;

namespace Janus.Domain.Validations;

/// <summary>Résultat d'une décision de validation.</summary>
/// <param name="Decision">Acceptation ou refus.</param>
/// <param name="Reason">Raison normalisée (voir <see cref="ValidationReasons"/>).</param>
public readonly record struct ValidationOutcome(ValidationDecision Decision, string Reason)
{
    /// <summary>Le passage est accepté et doit être débité.</summary>
    public bool IsAccepted => Decision == ValidationDecision.Accepted;
}

/// <summary>
/// Règle de décision d'un passage, évaluée sous verrou de ligne de la carte.
/// Ordre normatif : carte inconnue, carte révoquée, anti-passback, solde insuffisant, puis acceptation.
/// </summary>
public static class ValidationPolicy
{
    /// <summary>Indique si l'anti-passback doit être évalué (évite une requête inutile pour une carte inconnue ou révoquée).</summary>
    public static bool RequiresPassbackCheck(CardStatus? cardStatus, bool antiPassbackEnabled) =>
        antiPassbackEnabled && cardStatus == CardStatus.Active;

    /// <summary>Calcule la décision.</summary>
    /// <param name="cardStatus">Statut de la carte, ou null si l'UID est inconnu.</param>
    /// <param name="balanceMillimes">Solde courant (ignoré si la carte est inconnue).</param>
    /// <param name="fareMillimes">Tarif strictement positif.</param>
    /// <param name="isPassback">Un passage accepté existe sur la même borne dans la fenêtre anti-passback.</param>
    public static ValidationOutcome Decide(CardStatus? cardStatus, int balanceMillimes, int fareMillimes, bool isPassback)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fareMillimes);

        if (cardStatus is null)
            return new ValidationOutcome(ValidationDecision.Rejected, ValidationReasons.UnknownCard);
        if (cardStatus == CardStatus.Revoked)
            return new ValidationOutcome(ValidationDecision.Rejected, ValidationReasons.CardRevoked);
        if (isPassback)
            return new ValidationOutcome(ValidationDecision.Rejected, ValidationReasons.PassbackViolation);
        if (balanceMillimes < fareMillimes)
            return new ValidationOutcome(ValidationDecision.Rejected, ValidationReasons.InsufficientFunds);

        return new ValidationOutcome(ValidationDecision.Accepted, ValidationReasons.ActiveCard);
    }

    /// <summary>Contrôle de forme d'un événement de passage.</summary>
    public static bool IsWellFormed(Guid eventId, string? cardUid, string? eventType) =>
        eventId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(cardUid) &&
        cardUid.Length <= JanusSchemaLimits.CardUidMaxLength &&
        string.Equals(eventType, ValidationEventTypes.TransportValidation, StringComparison.Ordinal);

    /// <summary>Normalisation canonique d'un UID de carte (insensible à la casse et aux espaces de bord).</summary>
    public static string NormalizeCardUid(string cardUid)
    {
        ArgumentNullException.ThrowIfNull(cardUid);
        return cardUid.Trim().ToUpperInvariant();
    }
}
