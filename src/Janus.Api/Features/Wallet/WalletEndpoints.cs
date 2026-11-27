using System.Security.Claims;
using Janus.Api.Configuration;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;
using Janus.Domain;
using Janus.Domain.Cards;
using Janus.Domain.Wallet;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Features.Wallet;

/// <summary>Portefeuille : crédits idempotents et consultation du journal comptable.</summary>
public static class WalletEndpoints
{
    private const string CreditOperationName = "card_credit";

    private static readonly FinancialOperationMessages CreditMessages = new(
        Busy: "The card record is temporarily locked. Please retry with the same operationId.",
        TransientConflictPersisted: "Temporary transactional conflict persisted after retries. Please retry with the same operationId.",
        RetriesExhausted: "Unable to complete credit after bounded retries.",
        CommitNotPersisted: "Credit could not be committed. Please retry with the same operationId.",
        ReconciliationFailed: "Credit commit state is ambiguous. Please retry with the same operationId.");

    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // LOT-3B / FIN-07 : crédit administratif idempotent (recharge). Contrairement au PUT (affectation absolue),
        // le crédit est un delta appliqué sous verrou de ligne : il ne peut jamais écraser un débit concurrent.
        // Idempotence : clé "credit:{operationId}" unique dans le journal ; rejeu identique -> 200 duplicate, payload différent -> 409.
        endpoints.MapPost("/api/cards/{uid}/credits", CreditAsync)
            .RequireAuthorization(JanusPolicies.AdminOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Admin);

        // LOT-3B / FIN-09 : consultation paginée du journal comptable d'une carte, avec contrôle de réconciliation
        // (isBalanced = somme des mouvements == solde courant).
        endpoints.MapGet("/api/cards/{uid}/ledger", async (string uid, HttpRequest http, JanusDbContext db) => {
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > JanusSchemaLimits.CardUidMaxLength)
                return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

            var normalized = uid.Trim().ToUpperInvariant();
            var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(c => c.Uid == normalized);
            if (card is null) return Results.NotFound(new { error = "card_not_found" });

            int page = 1;
            if (http.Query.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
                page = Math.Max(1, parsedPage);

            int pageSize = 25;
            if (http.Query.TryGetValue("pageSize", out var sizeStr) && int.TryParse(sizeStr, out var parsedSize))
                pageSize = Math.Clamp(parsedSize, 1, 100);

            var entries = db.WalletLedgerEntries.AsNoTracking().Where(e => e.CardId == card.Id);
            var totalCount = await entries.CountAsync();
            var ledgerSum = await entries.SumAsync(e => (long)e.AmountMillimes);
            var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);
            var offset = checked((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue));

            var items = await entries
                .OrderByDescending(e => e.Id)
                .Skip(offset)
                .Take(pageSize)
                .Select(e => new LedgerEntryDto(e.Id, e.EntryType, e.AmountMillimes, e.BalanceAfterMillimes, e.Actor, e.Reason, e.CreatedAt))
                .ToListAsync();

            return Results.Ok(new {
                cardUid = card.Uid,
                balanceMillimes = card.BalanceMillimes,
                ledgerSumMillimes = ledgerSum,
                isBalanced = ledgerSum == card.BalanceMillimes,
                items,
                page,
                pageSize,
                totalCount,
                totalPages
            });
        }).RequireAuthorization(JanusPolicies.AdminOnly).RequireRateLimiting(JanusRateLimitPolicies.Admin);

        return endpoints;
    }

    private static async Task<IResult> CreditAsync(
        string uid,
        CardCreditRequest request,
        ClaimsPrincipal user,
        IConfiguration config,
        FinancialTransactionRunner runner,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
    {
        var logger = loggerFactory.CreateLogger(typeof(WalletEndpoints));
        if (string.IsNullOrWhiteSpace(uid) || uid.Length > JanusSchemaLimits.CardUidMaxLength)
            return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

        var maxCreditMillimes = ConfigurationReader.GetClampedInt(config, "Wallet:MaxCreditMillimes", "WALLET_MAX_CREDIT_MILLIMES", CreditPolicy.DefaultMaxCreditMillimes, 1, int.MaxValue);
        switch (CreditPolicy.ValidateRequest(request.OperationId, request.AmountMillimes, request.Reason, maxCreditMillimes))
        {
            case CreditRejection.MissingOperationId:
                return Results.BadRequest(new { error = "invalid_operation_id", message = "A client-generated operationId (UUID) is required." });
            case CreditRejection.InvalidAmount:
                return Results.BadRequest(new { error = "invalid_amount", message = $"Credit amount must be between 1 and {maxCreditMillimes} millimes." });
            case CreditRejection.InvalidReason:
                return Results.BadRequest(new { error = "invalid_reason", message = $"An audit reason (1 to {JanusSchemaLimits.AuditReasonMaxLength} characters) is required." });
        }

        var reason = request.Reason!.Trim();
        var normalized = uid.Trim().ToUpperInvariant();
        var actor = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "ADMIN";
        var idempotencyKey = LedgerIdempotencyKeys.AdminCredit(request.OperationId);

        IResult BuildReplayOrConflict(WalletLedgerEntry existing)
        {
            if (existing.EntryType != LedgerEntryTypes.AdminCredit || existing.CardUid != normalized || existing.AmountMillimes != request.AmountMillimes)
            {
                return Results.Conflict(new { error = "idempotency_conflict", message = "operationId already used with a different credit." });
            }

            return Results.Ok(new {
                uid = existing.CardUid,
                operationId = request.OperationId,
                amountMillimes = existing.AmountMillimes,
                balanceAfterMillimes = existing.BalanceAfterMillimes,
                duplicate = true,
                createdAt = existing.CreatedAt
            });
        }

        async Task<IResult?> FindCommittedAsync(JanusDbContext db) =>
            await db.WalletLedgerEntries.AsNoTracking().SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey) is { } existing
                ? BuildReplayOrConflict(existing)
                : null;

        return await runner.ExecuteAsync(new FinancialOperation
        {
            Name = CreditOperationName,
            CorrelationKey = $"card={normalized}, operationId={request.OperationId}",
            Messages = CreditMessages,
            FindCommittedAsync = FindCommittedAsync,

            ExecuteAsync = async (db, _) =>
            {
                var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {normalized} FOR UPDATE").SingleOrDefaultAsync();
                if (card is null)
                {
                    return FinancialStep.Abort(Results.NotFound(new { error = "card_not_found" }));
                }

                switch (CreditPolicy.ValidateAgainstCard(card, request.AmountMillimes))
                {
                    case CreditRejection.CardRevoked:
                        return FinancialStep.Abort(Results.Conflict(new { error = "card_revoked", message = "Revoked cards cannot be credited. Reactivate the card first." }));
                    case CreditRejection.BalanceLimitExceeded:
                        return FinancialStep.Abort(Results.UnprocessableEntity(new { error = "balance_limit_exceeded", message = "Resulting balance would exceed the supported maximum." }));
                }

                var previousBalance = card.BalanceMillimes;
                var now = timeProvider.GetUtcNow();
                card.Credit(request.AmountMillimes);

                db.WalletLedgerEntries.Add(new WalletLedgerEntry {
                    CardId = card.Id,
                    CardUid = card.Uid,
                    EntryType = LedgerEntryTypes.AdminCredit,
                    AmountMillimes = request.AmountMillimes,
                    BalanceAfterMillimes = card.BalanceMillimes,
                    IdempotencyKey = idempotencyKey,
                    Actor = actor,
                    Reason = reason,
                    CreatedAt = now
                });
                db.CardAuditLogs.Add(new CardAuditLog {
                    CardUid = card.Uid,
                    Action = "ADMIN_CARD_CREDITED",
                    PreviousBalanceMillimes = previousBalance,
                    NewBalanceMillimes = card.BalanceMillimes,
                    PreviousStatus = card.Status.ToString(),
                    NewStatus = card.Status.ToString(),
                    Reason = reason,
                    Actor = actor,
                    CreatedAt = now
                });

                return FinancialStep.Commit(() =>
                {
                    if (logger.IsEnabled(LogLevel.Information))
                    {
                        logger.LogInformation("Admin card credit: uid={CardUid}, amount={Amount}, balance={OldBal}->{NewBal}, operationId={OperationId}, actor={Actor}",
                            card.Uid, request.AmountMillimes, previousBalance, card.BalanceMillimes, request.OperationId, actor);
                    }

                    return Results.Ok(new {
                        uid = card.Uid,
                        operationId = request.OperationId,
                        amountMillimes = request.AmountMillimes,
                        balanceAfterMillimes = card.BalanceMillimes,
                        duplicate = false,
                        createdAt = now
                    });
                });
            },

            ReconcileAmbiguousCommitAsync = async (db, _) =>
            {
                var committed = await FindCommittedAsync(db);
                if (committed is not null)
                {
                    JanusMetrics.RecordDbTransactionEvent(CreditOperationName, "ambiguous_commit_recovered");
                }

                return committed;
            }
        });
    }
}
