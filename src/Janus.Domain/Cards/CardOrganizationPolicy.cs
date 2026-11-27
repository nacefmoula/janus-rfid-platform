using Janus.Domain.Organizations;

namespace Janus.Domain.Cards;

/// <summary>Motif de refus du rattachement d'une carte à une organisation.</summary>
public enum CardOrganizationRejection
{
    /// <summary>Aucun refus.</summary>
    None,
    /// <summary>Code d'organisation mal formé.</summary>
    InvalidOrganizationCode,
    /// <summary>Création sans organisation hors Development.</summary>
    OrganizationRequired,
    /// <summary>Organisation demandée différente de celle de la carte existante (une carte ne change jamais d'organisation).</summary>
    OrganizationMismatch
}

/// <summary>Règles de rattachement d'une carte à son organisation émettrice.</summary>
public static class CardOrganizationPolicy
{
    /// <summary>
    /// Code d'organisation à utiliser pour créer une carte. Sans code, l'organisation de démonstration n'est utilisée que si
    /// <paramref name="allowDemoOrganizationDefault"/> est vrai (environnement Development uniquement).
    /// </summary>
    public static (string? OrganizationCode, CardOrganizationRejection Rejection) ResolveForCreation(string? normalizedCode, bool allowDemoOrganizationDefault)
    {
        if (normalizedCode is not null)
        {
            return OrganizationCodes.IsValidOrganizationCode(normalizedCode)
                ? (normalizedCode, CardOrganizationRejection.None)
                : (null, CardOrganizationRejection.InvalidOrganizationCode);
        }

        return allowDemoOrganizationDefault
            ? (OrganizationDefaults.DemoOrganizationCode, CardOrganizationRejection.None)
            : (null, CardOrganizationRejection.OrganizationRequired);
    }

    /// <summary>Vérifie qu'une mise à jour ne tente pas de déplacer une carte existante vers une autre organisation.</summary>
    public static CardOrganizationRejection ValidateForExistingCard(int? requestedOrganizationId, int cardOrganizationId) =>
        requestedOrganizationId is int requested && requested != cardOrganizationId
            ? CardOrganizationRejection.OrganizationMismatch
            : CardOrganizationRejection.None;
}
