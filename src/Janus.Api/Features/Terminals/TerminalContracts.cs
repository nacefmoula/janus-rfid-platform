namespace Janus.Api.Features.Terminals;

/// <summary>Enrôlement d'une borne dans une organisation.</summary>
/// <param name="Code">Code unique sur toute la plateforme (majuscules, chiffres, tirets ; 3 à 80 caractères).</param>
/// <param name="Label">Libellé lisible (1 à 120 caractères).</param>
public sealed record TerminalEnrollRequest(string? Code, string? Label);

/// <summary>Révocation définitive d'une borne.</summary>
/// <param name="Reason">Motif d'audit obligatoire (1 à 256 caractères).</param>
public sealed record TerminalRevokeRequest(string? Reason);

/// <summary>Vue d'une borne (jamais de jeton ni d'empreinte).</summary>
public sealed record TerminalDto(
    string Code,
    string OrganizationCode,
    string Label,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    string? RevocationReason);

/// <summary>Résultat d'un enrôlement : le jeton n'est renvoyé qu'une seule fois, ici.</summary>
public sealed record TerminalEnrollmentResult(
    string Code,
    string OrganizationCode,
    string Label,
    string Status,
    DateTimeOffset ExpiresAt,
    DateTimeOffset CreatedAt,
    string Token);
