using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Janus.Api.Configuration;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;
using Janus.Domain;
using Janus.Domain.Cards;
using Janus.Domain.Wallet;

namespace Janus.Api.Features.Wallet;

/// <summary>Portefeuille : crédits idempotents et consultation du journal comptable.</summary>
public static class WalletEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapWalletEndpoints(this IEndpointRouteBuilder endpoints)
    {
        // LOT-3B / FIN-07 : crédit administratif idempotent (recharge). Contrairement au PUT (affectation absolue),
        // le crédit est un delta appliqué sous verrou de ligne : il ne peut jamais écraser un débit concurrent.
        // Idempotence : clé "credit:{operationId}" unique dans le journal ; rejeu identique -> 200 duplicate, payload différent -> 409.
        endpoints.MapPost("/api/cards/{uid}/credits", async (string uid, CardCreditRequest request, ClaimsPrincipal user, IConfiguration config, IServiceScopeFactory scopeFactory, TimeProvider timeProvider, ILogger<Program> logger) => {
            if (string.IsNullOrWhiteSpace(uid) || uid.Length > JanusSchemaLimits.CardUidMaxLength)
                return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

            if (request.OperationId == Guid.Empty)
                return Results.BadRequest(new { error = "invalid_operation_id", message = "A client-generated operationId (UUID) is required." });

            var maxCreditMillimes = ConfigurationReader.GetClampedInt(config, "Wallet:MaxCreditMillimes", "WALLET_MAX_CREDIT_MILLIMES", 1_000_000, 1, int.MaxValue);
            if (request.AmountMillimes <= 0 || request.AmountMillimes > maxCreditMillimes)
                return Results.BadRequest(new { error = "invalid_amount", message = $"Credit amount must be between 1 and {maxCreditMillimes} millimes." });

            var reason = request.Reason?.Trim();
            if (string.IsNullOrEmpty(reason) || reason.Length > JanusSchemaLimits.AuditReasonMaxLength)
                return Results.BadRequest(new { error = "invalid_reason", message = $"An audit reason (1 to {JanusSchemaLimits.AuditReasonMaxLength} characters) is required." });

            var normalized = uid.Trim().ToUpperInvariant();
            var actor = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "ADMIN";
            var idempotencyKey = $"credit:{request.OperationId:N}";
            var lockTimeoutMs = ConfigurationReader.GetClampedInt(config, "Database:LockTimeoutMs", "DATABASE_LOCK_TIMEOUT_MS", 5000, 50, 60000);
            var statementTimeoutMs = ConfigurationReader.GetClampedInt(config, "Database:StatementTimeoutMs", "DATABASE_STATEMENT_TIMEOUT_MS", 10000, 100, 120000);
            const int maxTransientRetries = 2;

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

            async Task<WalletLedgerEntry?> FindExistingInFreshScopeAsync()
            {
                await using var lookupScope = scopeFactory.CreateAsyncScope();
                var lookupDb = lookupScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                return await lookupDb.WalletLedgerEntries.AsNoTracking().SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey);
            }

            for (int attempt = 0; attempt <= maxTransientRetries; attempt++)
            {
                await using var attemptScope = scopeFactory.CreateAsyncScope();
                var db = attemptScope.ServiceProvider.GetRequiredService<JanusDbContext>();

                var alreadyApplied = await db.WalletLedgerEntries.AsNoTracking().SingleOrDefaultAsync(e => e.IdempotencyKey == idempotencyKey);
                if (alreadyApplied is not null)
                {
                    return BuildReplayOrConflict(alreadyApplied);
                }

                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                bool commitAttempted = false;
                try {
                    await PostgresTransactions.ApplyTimeoutsAsync(db, lockTimeoutMs, statementTimeoutMs);

                    var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {normalized} FOR UPDATE").SingleOrDefaultAsync();
                    if (card is null)
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                        return Results.NotFound(new { error = "card_not_found" });
                    }

                    // Règle métier : aucun mouvement de valeur sur une carte en opposition (réactivation préalable obligatoire).
                    if (card.Status == CardStatus.Revoked)
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                        return Results.Conflict(new { error = "card_revoked", message = "Revoked cards cannot be credited. Reactivate the card first." });
                    }

                    if ((long)card.BalanceMillimes + request.AmountMillimes > int.MaxValue)
                    {
                        await tx.RollbackAsync(CancellationToken.None);
                        return Results.UnprocessableEntity(new { error = "balance_limit_exceeded", message = "Resulting balance would exceed the supported maximum." });
                    }

                    var previousBalance = card.BalanceMillimes;
                    var now = timeProvider.GetUtcNow();
                    card.BalanceMillimes += request.AmountMillimes;

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

                    await db.SaveChangesAsync();
                    commitAttempted = true;
                    await tx.CommitAsync(CancellationToken.None);

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
                } catch (Exception ex) {
                    if (commitAttempted)
                    {
                        // Commit ambigu : ne jamais rejouer le crédit à l'aveugle, vérifier dans un scope neuf s'il a été engagé.
                        WalletLedgerEntry? committed;
                        try
                        {
                            committed = await FindExistingInFreshScopeAsync();
                        }
                        catch (Exception reconcileEx)
                        {
                            logger.LogError(reconcileEx, "Reconciliation failed after ambiguous credit commit: operationId={OperationId}", request.OperationId);
                            return Results.Json(new { error = "database_unavailable", message = "Credit commit state is ambiguous. Please retry with the same operationId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                        }

                        if (committed is not null)
                        {
                            JanusMetrics.RecordDbTransactionEvent("card_credit", "ambiguous_commit_recovered");
                            return BuildReplayOrConflict(committed);
                        }

                        await PostgresTransactions.SafeRollbackAsync(tx, logger, "card_credit");
                        if (attempt == 0)
                        {
                            JanusMetrics.RecordDbTransactionEvent("card_credit", "transient_retry");
                            continue;
                        }

                        return Results.Json(new { error = "database_unavailable", message = "Credit could not be committed. Please retry with the same operationId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    await PostgresTransactions.SafeRollbackAsync(tx, logger, "card_credit");
                    var sqlState = PostgresErrors.TryGetSqlState(ex);

                    if (string.Equals(sqlState, "23505", StringComparison.Ordinal))
                    {
                        var winner = await FindExistingInFreshScopeAsync();
                        if (winner is not null)
                        {
                            JanusMetrics.RecordDbTransactionEvent("card_credit", "conflict_recovered");
                            return BuildReplayOrConflict(winner);
                        }
                    }

                    if (string.Equals(sqlState, "55P03", StringComparison.Ordinal) || string.Equals(sqlState, "57014", StringComparison.Ordinal))
                    {
                        JanusMetrics.RecordDbTransactionEvent("card_credit", "lock_timeout");
                        return Results.Json(new { error = "database_busy", message = "The card record is temporarily locked. Please retry with the same operationId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    if ((string.Equals(sqlState, "40001", StringComparison.Ordinal) || string.Equals(sqlState, "40P01", StringComparison.Ordinal)) && attempt < maxTransientRetries)
                    {
                        JanusMetrics.RecordDbTransactionEvent("card_credit", "transient_retry");
                        await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)));
                        continue;
                    }

                    logger.LogError(ex, "Failed to persist admin card credit for {CardUid} (sqlState={SqlState}, attempt={Attempt})", normalized, sqlState, attempt);
                    throw;
                }
            }

            return Results.Json(new { error = "database_busy", message = "Unable to complete credit after bounded retries." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization(JanusPolicies.AdminOnly).RequireRateLimiting(JanusRateLimitPolicies.Admin);

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
}
