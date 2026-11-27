using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Security;

namespace Janus.Api.Features.Simulator;

/// <summary>Lecture minimale des cartes pour les terminaux et le simulateur (jamais de solde).</summary>
public static class SimulatorEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapSimulatorEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // REV-02 / FIN-02: Endpoint dédié au simulateur ne retournant que les informations minimales (sans solde financier ni métadonnées internes), avec limite paginée
        endpoints.MapGet("/api/simulator/cards", async (HttpRequest http, ClaimsPrincipal user, JanusDbContext db) => {
            int limit = 20;
            if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 50);

            int offset = 0;
            if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
                offset = Math.Max(0, parsedOffset);

            return Results.Ok(await VisibleCards(db, user)
                .OrderBy(c => c.Uid)
                .Skip(offset)
                .Take(limit)
                .Select(c => new {
                    c.Uid,
                    status = c.Status.ToString(),
                    c.Label
                }).ToListAsync());
        })
            .RequireAuthorization(JanusPolicies.CardsRead)
            .RequireRateLimiting(JanusRateLimitPolicies.CardsRead);

        // JANUS-SEC-002: Endpoint minimal de vérification de présence pour le simulateur et les valideurs (masque le solde et les transactions)
        endpoints.MapGet("/api/simulator/cards/{uid}/presence", async (string uid, ClaimsPrincipal user, JanusDbContext db) => {
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
                return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

            var normalized = uid.Trim().ToUpperInvariant();
            var card = await VisibleCards(db, user).SingleOrDefaultAsync(c => c.Uid == normalized);
            if (card is null) return Results.NotFound(new { error = "card_not_found" });

            return Results.Ok(new {
                uid = card.Uid,
                status = card.Status.ToString()
            });
        })
            .RequireAuthorization(JanusPolicies.CardsRead)
            .RequireRateLimiting(JanusRateLimitPolicies.CardsRead);

        return endpoints;
    }

    /// <summary>LOT-5A : une borne ne voit que les cartes de son organisation ; l'administrateur de plateforme voit tout.</summary>
    private static IQueryable<Janus.Domain.Cards.TransitCard> VisibleCards(JanusDbContext db, ClaimsPrincipal user)
    {
        var cards = db.Cards.AsNoTracking();
        if (user.IsInRole("Terminal"))
        {
            var organizationId = JanusClaims.GetOrganizationId(user) ?? -1;
            cards = cards.Where(c => c.OrganizationId == organizationId);
        }
        return cards;
    }
}
