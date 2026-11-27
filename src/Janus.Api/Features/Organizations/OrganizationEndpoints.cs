using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Security;
using Janus.Domain;
using Janus.Domain.Organizations;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Features.Organizations;

/// <summary>Administration des organisations clientes (administrateur de plateforme).</summary>
public static class OrganizationEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapOrganizationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/organizations", CreateAsync)
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapGet("/api/organizations", async (JanusDbContext db) =>
            Results.Ok(await db.Organizations.AsNoTracking()
                .OrderBy(o => o.Code)
                .Select(o => new OrganizationDto(o.Code, o.Name, o.Status.ToString(), o.CreatedAt))
                .ToListAsync()))
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(OrganizationCreateRequest request, JanusDbContext db, TimeProvider timeProvider)
    {
        var code = OrganizationCodes.Normalize(request.Code);
        if (!OrganizationCodes.IsValidOrganizationCode(code))
            return Results.BadRequest(new { error = "invalid_organization_code", message = "Code must be 2 to 32 characters: A-Z, 0-9 and '-', starting with a letter or digit." });

        var name = request.Name?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > JanusSchemaLimits.OrganizationNameMaxLength)
            return Results.BadRequest(new { error = "invalid_name", message = $"Name must be 1 to {JanusSchemaLimits.OrganizationNameMaxLength} characters." });

        var organization = new Organization { Code = code!, Name = name, Status = OrganizationStatus.Active, CreatedAt = timeProvider.GetUtcNow() };
        db.Organizations.Add(organization);
        try
        {
            await db.SaveChangesAsync();
        }
        catch (DbUpdateException ex) when (PostgresErrors.TryGetSqlState(ex) == PostgresErrors.UniqueViolation)
        {
            return Results.Conflict(new { error = "organization_code_taken", message = "An organization with this code already exists." });
        }

        return Results.Created($"/api/organizations/{organization.Code}",
            new OrganizationDto(organization.Code, organization.Name, organization.Status.ToString(), organization.CreatedAt));
    }
}
