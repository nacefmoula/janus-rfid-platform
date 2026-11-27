using Microsoft.EntityFrameworkCore;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;
using Janus.Domain.Cards;
using Janus.Domain.Validations;

namespace Janus.Api.Features.Dashboard;

/// <summary>Synthèse d'exploitation (agrégats financiers, réservée aux administrateurs).</summary>
public static class DashboardEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapDashboardEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // REV-02 / FIN-03: Endpoint métier synthétique pour le dashboard (agrégats SQL regroupés + compteur mémoire explicite pour les rejets 409 non persistés en base)
        endpoints.MapGet("/api/dashboard/summary", async (JanusDbContext db, TimeProvider timeProvider) => {
            var validationCounts = await db.Validations.AsNoTracking()
                .GroupBy(v => v.Decision)
                .Select(g => new { Decision = g.Key, Count = g.Count() })
                .ToListAsync();
            var acceptedValidations = validationCounts.FirstOrDefault(x => x.Decision == ValidationDecision.Accepted)?.Count ?? 0;
            var rejectedValidations = validationCounts.FirstOrDefault(x => x.Decision == ValidationDecision.Rejected)?.Count ?? 0;

            var totalDebitedMillimes = await db.FareTransactions.AsNoTracking().SumAsync(t => (long?)t.AmountMillimes) ?? 0L;

            var cardStats = await db.Cards.AsNoTracking()
                .GroupBy(c => c.Status)
                .Select(g => new { Status = g.Key, Count = g.Count(), TotalBalance = g.Sum(c => (long)c.BalanceMillimes) })
                .ToListAsync();
            var activeCardsCount = cardStats.FirstOrDefault(x => x.Status == CardStatus.Active)?.Count ?? 0;
            var revokedCardsCount = cardStats.FirstOrDefault(x => x.Status == CardStatus.Revoked)?.Count ?? 0;
            var totalCirculatingBalanceMillimes = cardStats.Sum(x => x.TotalBalance);

            return Results.Ok(new {
                totalValidations = acceptedValidations + rejectedValidations,
                acceptedValidations,
                rejectedValidations,
                conflictValidations = JanusMetrics.GetConflictValidationsCount(),
                totalDebitedMillimes,
                activeCardsCount,
                revokedCardsCount,
                totalCardsCount = activeCardsCount + revokedCardsCount,
                totalCirculatingBalanceMillimes,
                scrapedAt = timeProvider.GetUtcNow()
            });
        })
            .RequireAuthorization(JanusPolicies.DashboardRead)
            .RequireRateLimiting(JanusRateLimitPolicies.CardsRead);

        return endpoints;
    }
}
