using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Janus.Api.Common;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;
using Janus.Domain;
using Janus.Domain.Cards;
using Janus.Domain.Organizations;
using Janus.Domain.Wallet;

namespace Janus.Api.Features.Cards;

/// <summary>Administration des cartes : inventaire, détail, audit et création / mise à jour (PUT).</summary>
public static class CardsEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapCardsEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // PAG-01: Endpoint administratif paginé pour l'inventaire des cartes (avec filtres recherche et statut, tri déterministe)
        endpoints.MapGet("/api/cards", async (HttpRequest http, JanusDbContext db) => {
            int page = 1;
            if (http.Query.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
                page = Math.Max(1, parsedPage);

            int pageSize = 25;
            if (http.Query.TryGetValue("pageSize", out var sizeStr) && int.TryParse(sizeStr, out var parsedSize))
                pageSize = Math.Clamp(parsedSize, 1, 100);

            var query = db.Cards.AsNoTracking();

            if (http.Query.TryGetValue("status", out var statusStr) && !string.IsNullOrWhiteSpace(statusStr)) {
                var statusVal = statusStr.ToString().Trim();
                if (!statusVal.Equals("ALL", StringComparison.OrdinalIgnoreCase)) {
                    if (Enum.TryParse<CardStatus>(statusVal, true, out var parsedStatus)) {
                        query = query.Where(c => c.Status == parsedStatus);
                    } else {
                        return Results.BadRequest(new { error = "invalid_status", message = "Invalid status filter. Allowed values: Active, Revoked, ALL." });
                    }
                }
            }

            if (http.Query.TryGetValue("q", out var qStr) && !string.IsNullOrWhiteSpace(qStr)) {
                var q = qStr.ToString().Trim().ToUpperInvariant();
                query = query.Where(c => c.Uid.ToUpper().Contains(q) || c.Label.ToUpper().Contains(q));
            }

            var totalCount = await query.CountAsync();
            var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);

            var offset = checked((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue));

            var items = await query
                .OrderBy(c => c.Uid)
                .Skip(offset)
                .Take(pageSize)
                .Select(c => new AdminTransitCardDto(
                    c.Uid,
                    c.Status.ToString(),
                    c.Label,
                    c.BalanceMillimes
                ))
                .ToListAsync();

            return Results.Ok(new PagedResult<AdminTransitCardDto>(
                items,
                page,
                pageSize,
                totalCount,
                totalPages
            ));
        })
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapGet("/api/demo/cards", async (HttpRequest http, JanusDbContext db) => {
            int limit = 50;
            if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 100);

            int offset = 0;
            if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
                offset = Math.Max(0, parsedOffset);

            return Results.Ok(await db.Cards.AsNoTracking()
                .OrderBy(c => c.Uid)
                .Skip(offset)
                .Take(limit)
                .Select(c => new {
                    c.Uid,
                    status = c.Status.ToString(),
                    c.Label,
                    balanceMillimes = c.BalanceMillimes
                }).ToListAsync());
        })
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapGet("/api/cards/{uid}", async (string uid, HttpRequest http, JanusDbContext db) => {
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > JanusSchemaLimits.CardUidMaxLength)
                return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

            var normalized = uid.Trim().ToUpperInvariant();
            var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(c => c.Uid == normalized);
            if (card is null) return Results.NotFound(new { error = "card_not_found" });

            int limit = 50;
            if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
                limit = Math.Clamp(parsedLimit, 1, 100);

            int offset = 0;
            if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
                offset = Math.Max(0, parsedOffset);

            var totalTxs = await db.FareTransactions.AsNoTracking().CountAsync(t => t.CardId == card.Id);

            var txs = await db.FareTransactions.AsNoTracking()
                .Where(t => t.CardId == card.Id)
                .OrderByDescending(t => t.CreatedAt)
                .ThenByDescending(t => t.Id)
                .Skip(offset)
                .Take(limit)
                .Select(t => new { t.Id, t.TerminalId, t.EventId, t.AmountMillimes, t.BalanceAfterMillimes, t.CreatedAt })
                .ToListAsync();

            return Results.Ok(new {
                card.Uid,
                status = card.Status.ToString(),
                card.Label,
                card.BalanceMillimes,
                transactionsCount = totalTxs,
                limit,
                offset,
                transactions = txs
            });
        // JANUS-SEC-002: L'accès aux détails financiers et à l'historique complet est strictement restreint aux administrateurs
        }).RequireAuthorization(JanusPolicies.AdminOnly).RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapGet("/api/cards/{uid}/audit", async (string uid, HttpRequest http, JanusDbContext db) => {
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
                return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

            var normalized = uid.Trim().ToUpperInvariant();
            var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(c => c.Uid == normalized);
            if (card is null) return Results.NotFound(new { error = "card_not_found" });

            // PAG-02: Gestion duale des paramètres modernes (page/pageSize) et historiques (limit/offset)
            // Règle de priorité : les paramètres modernes prévalent s'ils sont explicitement fournis.
            int page = 1;
            int pageSize = 25;
            int limit = 50;
            int offset = 0;

            bool hasModernParams = http.Query.ContainsKey("page") || http.Query.ContainsKey("pageSize");
            bool hasLegacyParams = http.Query.ContainsKey("limit") || http.Query.ContainsKey("offset");

            if (hasModernParams)
            {
                if (http.Query.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
                    page = Math.Max(1, parsedPage);

                if (http.Query.TryGetValue("pageSize", out var sizeStr) && int.TryParse(sizeStr, out var parsedSize))
                    pageSize = Math.Clamp(parsedSize, 1, 100);

                offset = checked((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue));
                limit = pageSize;
            }
            else if (hasLegacyParams)
            {
                if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
                    limit = Math.Clamp(parsedLimit, 1, 100);

                if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
                    offset = Math.Max(0, parsedOffset);

                pageSize = limit;
                page = (offset / limit) + 1;
            }
            else
            {
                // Défaut historique : limit = 50, offset = 0
                limit = 50;
                offset = 0;
                pageSize = limit;
                page = 1;
            }

            var totalAudits = await db.CardAuditLogs.AsNoTracking().CountAsync(a => a.CardUid == normalized);
            var totalPages = totalAudits == 0 ? 1 : (int)Math.Ceiling((double)totalAudits / pageSize);

            var audits = await db.CardAuditLogs.AsNoTracking()
                .Where(a => a.CardUid == normalized)
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Skip(offset)
                .Take(limit)
                .Select(a => new CardAuditLogDto(
                    a.Id,
                    a.Action,
                    a.PreviousBalanceMillimes,
                    a.NewBalanceMillimes,
                    a.PreviousStatus,
                    a.NewStatus,
                    a.Reason,
                    a.Actor,
                    a.CreatedAt
                ))
                .ToListAsync();

            return Results.Ok(new {
                items = audits,
                page,
                pageSize,
                totalCount = totalAudits,
                totalPages,
                cardUid = normalized,
                limit,
                offset,
                auditLogs = audits
            });
        }).RequireAuthorization(JanusPolicies.AuditRead).RequireRateLimiting(JanusRateLimitPolicies.Admin);

        endpoints.MapPut("/api/cards/{uid}", UpsertCardAsync)
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        return endpoints;
    }

    private static readonly FinancialOperationMessages UpsertMessages = new(
        Busy: "The card record is temporarily locked by a concurrent operation. Please retry.",
        TransientConflictPersisted: "Temporary transactional conflict persisted after retries. Please retry.",
        RetriesExhausted: "Unable to complete card update after bounded retries.");

    private static async Task<IResult> UpsertCardAsync(
        string uid,
        CardUpsertRequest request,
        ClaimsPrincipal user,
        FinancialTransactionRunner runner,
        TimeProvider timeProvider,
        IHostEnvironment environment,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(CardsEndpoints));

        if (string.IsNullOrWhiteSpace(uid) || uid.Length > JanusSchemaLimits.CardUidMaxLength)
            return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

        if (request.BalanceMillimes is < 0)
            return Results.BadRequest(new { error = "invalid_balance", message = "Balance cannot be negative." });

        // LOT-3A / INP-01 : bornes alignées sur le schéma (Cards.Label varchar(120), CardAuditLogs.Reason varchar(256)).
        // Sans ce contrôle, PostgreSQL lève 22001 (string_data_right_truncation) et l'API répond 500 au lieu de 400.
        if (request.Label is not null && request.Label.Length > JanusSchemaLimits.CardLabelMaxLength)
            return Results.BadRequest(new { error = "invalid_label", message = $"Label must not exceed {JanusSchemaLimits.CardLabelMaxLength} characters." });

        if (request.Reason is not null && request.Reason.Trim().Length > JanusSchemaLimits.AuditReasonMaxLength)
            return Results.BadRequest(new { error = "invalid_reason", message = $"Reason must not exceed {JanusSchemaLimits.AuditReasonMaxLength} characters." });

        // Validation stricte du statut si fourni (uniquement Active ou Revoked, insensible à la casse)
        CardStatus? requestedStatus = null;
        if (!string.IsNullOrWhiteSpace(request.Status))
        {
            var s = request.Status.Trim();
            if (s.Equals("Active", StringComparison.OrdinalIgnoreCase))
                requestedStatus = CardStatus.Active;
            else if (s.Equals("Revoked", StringComparison.OrdinalIgnoreCase))
                requestedStatus = CardStatus.Revoked;
            else
                return Results.BadRequest(new { error = "invalid_status", message = "Status must be 'Active' or 'Revoked'." });
        }

        // LOT-5A : organisation émettrice (format validé avant toute transaction).
        var requestedOrganizationCode = OrganizationCodes.Normalize(request.OrganizationCode);
        if (requestedOrganizationCode is not null && !OrganizationCodes.IsValidOrganizationCode(requestedOrganizationCode))
            return Results.BadRequest(new { error = "invalid_organization_code", message = "Invalid organization code." });

        var normalized = uid.Trim().ToUpperInvariant();
        var actor = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "ADMIN";

        return await runner.ExecuteAsync(new FinancialOperation
        {
            Name = "card_upsert",
            CorrelationKey = $"card={normalized}",
            Messages = UpsertMessages,
            ExecuteAsync = async (db, attempt) =>
            {
                var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {normalized} FOR UPDATE").SingleOrDefaultAsync();

                // P1-3 / Lot 2C : si deux PUT concurrents créent la même nouvelle carte (23505 sur IX_Cards_Uid), rejouer une unique fois
                // la transaction complète afin de verrouiller (SELECT ... FOR UPDATE) la carte créée par le concurrent et d'enregistrer
                // la seconde opération comme ADMIN_CARD_UPDATED avec PreviousBalance exact.
                attempt.RetryOnceOnUniqueViolation = card is null;

                // LOT-5A : résolution de l'organisation. Une carte existante ne change jamais d'organisation.
                int organizationId;
                if (card is null)
                {
                    var (creationCode, rejection) = CardOrganizationPolicy.ResolveForCreation(requestedOrganizationCode, environment.IsDevelopment());
                    if (rejection == CardOrganizationRejection.OrganizationRequired)
                        return FinancialStep.Abort(Results.BadRequest(new { error = "organization_required", message = "organizationCode is required to create a card." }));

                    var resolvedId = await db.Organizations.AsNoTracking().Where(o => o.Code == creationCode).Select(o => (int?)o.Id).SingleOrDefaultAsync();
                    if (resolvedId is not int createdIn)
                        return FinancialStep.Abort(Results.NotFound(new { error = "organization_not_found" }));
                    organizationId = createdIn;
                }
                else
                {
                    int? requestedOrganizationId = null;
                    if (requestedOrganizationCode is not null)
                    {
                        requestedOrganizationId = await db.Organizations.AsNoTracking().Where(o => o.Code == requestedOrganizationCode).Select(o => (int?)o.Id).SingleOrDefaultAsync();
                        if (requestedOrganizationId is null)
                            return FinancialStep.Abort(Results.NotFound(new { error = "organization_not_found" }));
                    }

                    // Note 5B : avec des opérateurs par organisation, cette réponse ne devra pas révéler l'existence de la carte ailleurs.
                    if (CardOrganizationPolicy.ValidateForExistingCard(requestedOrganizationId, card.OrganizationId) == CardOrganizationRejection.OrganizationMismatch)
                        return FinancialStep.Abort(Results.Conflict(new { error = "organization_mismatch", message = "A card never changes organization." }));
                    organizationId = card.OrganizationId;
                }

                // LOT-3D / FIN-10 : une modification absolue du solde d'une carte existante exige la garde optimiste.
                // Sans elle, le serveur ne peut pas distinguer une correction volontaire d'un écrasement accidentel (lecture périmée).
                if (card is not null && request.BalanceMillimes is int requestedBalance && requestedBalance != card.BalanceMillimes && request.ExpectedBalanceMillimes is null)
                {
                    JanusMetrics.RecordDbTransactionEvent("card_upsert", "expected_balance_missing");
                    return FinancialStep.Abort(Results.Json(new {
                        error = "expected_balance_required",
                        message = "Changing the balance of an existing card requires expectedBalanceMillimes. Use POST /api/cards/{uid}/credits for top-ups.",
                        currentBalanceMillimes = card.BalanceMillimes
                    }, statusCode: StatusCodes.Status428PreconditionRequired));
                }

                // LOT-3B / FIN-08 : garde de concurrence optimiste. Le client déclare le solde sur lequel il s'est basé ;
                // s'il a changé depuis (débit d'un terminal, autre admin), on refuse au lieu d'écraser silencieusement le mouvement.
                if (card is not null && request.ExpectedBalanceMillimes is int expectedBalance && expectedBalance != card.BalanceMillimes)
                {
                    JanusMetrics.RecordDbTransactionEvent("card_upsert", "stale_balance_rejected");
                    return FinancialStep.Abort(Results.Json(new {
                        error = "stale_balance",
                        message = "The card balance changed since it was read. Reload the card and retry.",
                        currentBalanceMillimes = card.BalanceMillimes
                    }, statusCode: StatusCodes.Status409Conflict));
                }

                var prevBalance = card?.BalanceMillimes;
                var prevStatus = card?.Status.ToString();
                var action = card is null ? "ADMIN_CARD_CREATED" : "ADMIN_CARD_UPDATED";

                // Statut effectif : Active par défaut à la création, inchangé sur une carte existante si non fourni.
                CardStatus targetStatus = card is null
                    ? (requestedStatus ?? CardStatus.Active)
                    : (requestedStatus ?? card.Status);

                // Motif d'audit distinct du libellé de la carte
                var auditReason = !string.IsNullOrWhiteSpace(request.Reason)
                    ? request.Reason.Trim()
                    : (card is null ? "Initial card provisioning" : "Admin balance/status update");

                var movementAt = timeProvider.GetUtcNow();
                int balanceDelta;
                string ledgerEntryType;

                if (card is null)
                {
                    var openingBalance = request.BalanceMillimes ?? 0;
                    card = new TransitCard {
                        OrganizationId = organizationId,
                        Uid = normalized,
                        Status = targetStatus,
                        Label = request.Label ?? $"Card {normalized}",
                        BalanceMillimes = openingBalance
                    };
                    db.Cards.Add(card);
                    balanceDelta = openingBalance;
                    ledgerEntryType = LedgerEntryTypes.OpeningBalance;
                }
                else
                {
                    var newBalance = request.BalanceMillimes ?? card.BalanceMillimes;
                    balanceDelta = newBalance - card.BalanceMillimes;
                    ledgerEntryType = LedgerEntryTypes.AdminAdjustment;
                    card.BalanceMillimes = newBalance;
                    card.Status = targetStatus;
                    if (!string.IsNullOrEmpty(request.Label))
                        card.Label = request.Label;
                }

                // LOT-3B / FIN-06 : tout mouvement de solde est inscrit au journal comptable (append-only).
                if (balanceDelta != 0)
                {
                    db.WalletLedgerEntries.Add(new WalletLedgerEntry {
                        OrganizationId = organizationId,
                        Card = card,
                        CardUid = normalized,
                        EntryType = ledgerEntryType,
                        AmountMillimes = balanceDelta,
                        BalanceAfterMillimes = card.BalanceMillimes,
                        IdempotencyKey = LedgerIdempotencyKeys.AdminSet(Guid.NewGuid()),
                        Actor = actor,
                        Reason = auditReason,
                        CreatedAt = movementAt
                    });
                }

                // Traçabilité d'audit avec identité réelle persistée (SEC-06)
                db.CardAuditLogs.Add(new CardAuditLog {
                    OrganizationId = organizationId,
                    CardUid = normalized,
                    Action = action,
                    PreviousBalanceMillimes = prevBalance,
                    NewBalanceMillimes = card.BalanceMillimes,
                    PreviousStatus = prevStatus,
                    NewStatus = card.Status.ToString(),
                    Reason = auditReason,
                    Actor = actor,
                    CreatedAt = movementAt
                });

                return FinancialStep.Commit(() =>
                {
                    // Journalisation sans secrets
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Admin card update: action={Action}, uid={CardUid}, balance={OldBal}->{NewBal}, status={OldStat}->{NewStat}, reason={Reason}, actor={Actor}",
                            action, normalized, prevBalance, card.BalanceMillimes, prevStatus, card.Status, auditReason, actor);
                    }

                    return Results.Ok(new {
                        card.Uid,
                        status = card.Status.ToString(),
                        card.Label,
                        card.BalanceMillimes
                    });
                });
            }
        });
    }
}
