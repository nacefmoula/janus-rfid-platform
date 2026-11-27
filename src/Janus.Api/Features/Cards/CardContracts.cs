namespace Janus.Api.Features.Cards;

/// <param name="BalanceMillimes">
/// Solde absolu : obligatoire à la création (défaut 0), facultatif sur une carte existante (absent = solde inchangé).
/// Une modification absolue du solde d'une carte existante est une correction exceptionnelle : les recharges passent par
/// POST /api/cards/{uid}/credits.
/// </param>
/// <param name="Status">Statut cible (Active ou Revoked) ; absent = Active à la création, inchangé sinon.</param>
/// <param name="Label">Libellé ; absent = libellé par défaut à la création, inchangé sinon.</param>
/// <param name="Reason">Motif d'audit.</param>
/// <param name="ExpectedBalanceMillimes">
/// Garde de concurrence optimiste, OBLIGATOIRE pour toute modification absolue du solde d'une carte existante
/// (428 expected_balance_required sinon). Si le solde courant diffère, la mise à jour est refusée (409 stale_balance),
/// ce qui empêche d'écraser un débit survenu depuis la lecture du client.
/// </param>
/// <param name="OrganizationCode">
/// Organisation émettrice (LOT-5A). Obligatoire à la création hors Development (400 organization_required) ; en Development,
/// absent = organisation de démonstration. Sur une carte existante : absent = inchangé, différent = 409 organization_mismatch
/// (une carte ne change jamais d'organisation).
/// </param>
public sealed record CardUpsertRequest(int? BalanceMillimes, string? Status, string? Label, string? Reason = null, int? ExpectedBalanceMillimes = null, string? OrganizationCode = null);

/// <summary>Vue administrateur d'une carte (avec solde).</summary>
public sealed record AdminTransitCardDto(
    string Uid,
    string Status,
    string Label,
    int BalanceMillimes
);

/// <summary>Entrée d'audit administratif exposée par l'API.</summary>
public sealed record CardAuditLogDto(
    long Id,
    string Action,
    int? PreviousBalanceMillimes,
    int NewBalanceMillimes,
    string? PreviousStatus,
    string NewStatus,
    string? Reason,
    string Actor,
    DateTimeOffset CreatedAt
);
