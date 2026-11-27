namespace Janus.Api.Features.Organizations;

/// <summary>Création d'une organisation cliente.</summary>
/// <param name="Code">Code public unique (majuscules, chiffres, tirets ; 2 à 32 caractères).</param>
/// <param name="Name">Nom lisible (1 à 120 caractères).</param>
public sealed record OrganizationCreateRequest(string? Code, string? Name);

/// <summary>Vue d'une organisation.</summary>
public sealed record OrganizationDto(string Code, string Name, string Status, DateTimeOffset CreatedAt);
