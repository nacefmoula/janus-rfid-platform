using System.Diagnostics;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Janus.Api.Configuration;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;
using Janus.Domain.Cards;
using Janus.Domain.Validations;
using Janus.Domain.Wallet;

namespace Janus.Api.Features.Validations;

/// <summary>Validation d'un passage de carte par un terminal (débit, idempotence, anti-passback).</summary>
public static class ValidationEndpoints
{
    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapValidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/validations", async (ValidationRequest request, ClaimsPrincipal user, IConfiguration config, IServiceScopeFactory scopeFactory, JanusTransactionCommitHook commitHook, TimeProvider timeProvider, ILogger<Program> logger) => {
            // SEC-02: Identité du terminal déterminée côté serveur par le jeton authentifié
            var terminalId = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "TERMINAL-DEMO-01";

            var validationStartTime = Stopwatch.GetTimestamp();
            var serverNow = timeProvider.GetUtcNow();

            if (request.EventId == Guid.Empty || string.IsNullOrWhiteSpace(request.CardUid) || request.CardUid.Length > 64 || request.EventType != "TRANSPORT_VALIDATION") {
                var elapsedBadReqSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
                JanusMetrics.ValidationDurationSeconds.Record(elapsedBadReqSeconds,
                    new KeyValuePair<string, object?>("decision", "REJECTED"));
                JanusMetrics.ValidationsTotal.Add(1,
                    new KeyValuePair<string, object?>("decision", "REJECTED"),
                    new KeyValuePair<string, object?>("reason", "INVALID_REQUEST"),
                    new KeyValuePair<string, object?>("terminal_id", terminalId),
                    new KeyValuePair<string, object?>("duplicate", "false"));
                return Results.BadRequest(new { error = "invalid_request", message = "eventId, cardUid and supported eventType are required." });
            }

            var fareConfig = config["Fare:AmountMillimes"];
            if (!int.TryParse(fareConfig, out var fareAmount) || fareAmount <= 0)
                return Results.Json(new { error = "invalid_fare_configuration", message = "Fare amount must be configured and strictly positive." }, statusCode: 500);

            var apbEnabledConfig = config["AntiPassback:Enabled"];
            bool isApbEnabled = !string.Equals(apbEnabledConfig, "false", StringComparison.OrdinalIgnoreCase);
            int apbWindowSeconds = 30;
            if (int.TryParse(config["AntiPassback:WindowSeconds"], out var parsedWindow) && parsedWindow > 0) {
                apbWindowSeconds = parsedWindow;
            }

            var uid = request.CardUid.Trim().ToUpperInvariant();
            var canonical = JsonSerializer.Serialize(new { eventId = request.EventId, cardUid = uid, occurredAt = request.OccurredAt?.ToUniversalTime(), eventType = request.EventType });
            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
            var lockTimeoutMs = ConfigurationReader.GetClampedInt(config, "Database:LockTimeoutMs", "DATABASE_LOCK_TIMEOUT_MS", 5000, 50, 60000);
            var statementTimeoutMs = ConfigurationReader.GetClampedInt(config, "Database:StatementTimeoutMs", "DATABASE_STATEMENT_TIMEOUT_MS", 10000, 100, 120000);
            const int maxTransientRetries = 2;

            IResult BuildIdempotentOrConflictResponse(ValidationEvent existingRecord, int? reconciledBalanceMillimes)
            {
                var elapsedSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
                if (existingRecord.PayloadHash != hash) {
                    JanusMetrics.RecordConflictValidation();
                    JanusMetrics.ValidationDurationSeconds.Record(elapsedSeconds,
                        new KeyValuePair<string, object?>("decision", "CONFLICT"));
                    JanusMetrics.ValidationsTotal.Add(1,
                        new KeyValuePair<string, object?>("decision", "CONFLICT"),
                        new KeyValuePair<string, object?>("reason", "IDEMPOTENCY_CONFLICT"),
                        new KeyValuePair<string, object?>("terminal_id", terminalId),
                        new KeyValuePair<string, object?>("duplicate", "false"));
                    return Results.Conflict(new { error = "idempotency_conflict", message = "eventId already used with different payload." });
                }

                JanusMetrics.ValidationDurationSeconds.Record(elapsedSeconds,
                    new KeyValuePair<string, object?>("decision", existingRecord.Decision.ToString().ToUpperInvariant()));
                JanusMetrics.ValidationsTotal.Add(1,
                    new KeyValuePair<string, object?>("decision", existingRecord.Decision.ToString().ToUpperInvariant()),
                    new KeyValuePair<string, object?>("reason", existingRecord.Reason),
                    new KeyValuePair<string, object?>("terminal_id", terminalId),
                    new KeyValuePair<string, object?>("duplicate", "true"));

                if (reconciledBalanceMillimes.HasValue)
                {
                    return Results.Ok(new {
                        eventId = existingRecord.EventId,
                        terminalId = existingRecord.TerminalId,
                        cardUid = existingRecord.CardUid,
                        decision = existingRecord.Decision.ToString().ToUpperInvariant(),
                        reason = existingRecord.Reason,
                        balanceMillimes = reconciledBalanceMillimes.Value,
                        amountDebited = (existingRecord.Decision == ValidationDecision.Accepted ? fareAmount : 0),
                        duplicate = true,
                        receivedAt = existingRecord.ReceivedAt
                    });
                }

                return Results.Ok(new {
                    eventId = existingRecord.EventId,
                    terminalId = existingRecord.TerminalId,
                    cardUid = existingRecord.CardUid,
                    decision = existingRecord.Decision.ToString().ToUpperInvariant(),
                    reason = existingRecord.Reason,
                    duplicate = true,
                    receivedAt = existingRecord.ReceivedAt
                });
            }

            for (int attempt = 0; attempt <= maxTransientRetries; attempt++)
            {
                await using var attemptScope = scopeFactory.CreateAsyncScope();
                var db = attemptScope.ServiceProvider.GetRequiredService<JanusDbContext>();

                // Situation 3 : Pré-vérification d'idempotence sur une connexion saine
                var existing = await db.Validations.AsNoTracking().SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);
                if (existing is not null) {
                    return BuildIdempotentOrConflictResponse(existing, null);
                }

                await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
                bool commitAttempted = false;
                try {
                    await PostgresTransactions.ApplyTimeoutsAsync(db, lockTimeoutMs, statementTimeoutMs);

                    var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {uid} FOR UPDATE").SingleOrDefaultAsync();

                    ValidationDecision decision;
                    string reason;
                    if (card is null) {
                        decision = ValidationDecision.Rejected;
                        reason = "UNKNOWN_CARD";
                    } else if (card.Status == CardStatus.Revoked) {
                        decision = ValidationDecision.Rejected;
                        reason = "CARD_REVOKED";
                    } else {
                        bool isPassback = false;
                        if (isApbEnabled) {
                            var cutoff = serverNow - TimeSpan.FromSeconds(apbWindowSeconds);
                            isPassback = await db.Validations.AnyAsync(v =>
                                v.TerminalId == terminalId &&
                                v.CardUid == uid &&
                                v.Decision == ValidationDecision.Accepted &&
                                v.ReceivedAt >= cutoff);
                        }

                        if (isPassback) {
                            decision = ValidationDecision.Rejected;
                            reason = "PASSBACK_VIOLATION";
                        } else if (card.BalanceMillimes < fareAmount) {
                            decision = ValidationDecision.Rejected;
                            reason = "INSUFFICIENT_FUNDS";
                        } else {
                            card.BalanceMillimes -= fareAmount;
                            decision = ValidationDecision.Accepted;
                            reason = "ACTIVE_CARD";
                            db.FareTransactions.Add(new FareTransaction {
                                CardId = card.Id,
                                CardUid = card.Uid,
                                TerminalId = terminalId,
                                EventId = request.EventId,
                                AmountMillimes = fareAmount,
                                BalanceAfterMillimes = card.BalanceMillimes,
                                CreatedAt = serverNow
                            });
                            // LOT-3B / FIN-06 : débit inscrit au journal comptable, avec la même clé d'idempotence (terminal, événement)
                            db.WalletLedgerEntries.Add(new WalletLedgerEntry {
                                CardId = card.Id,
                                CardUid = card.Uid,
                                EntryType = LedgerEntryTypes.FareDebit,
                                AmountMillimes = -fareAmount,
                                BalanceAfterMillimes = card.BalanceMillimes,
                                IdempotencyKey = $"fare:{terminalId}:{request.EventId:N}",
                                Actor = terminalId,
                                CreatedAt = serverNow
                            });
                        }
                    }

                    var record = new ValidationEvent {
                        TerminalId = terminalId,
                        EventId = request.EventId,
                        CardUid = uid,
                        PayloadHash = hash,
                        EventType = request.EventType,
                        Decision = decision,
                        Reason = reason,
                        OccurredAt = request.OccurredAt?.ToUniversalTime(),
                        ReceivedAt = serverNow
                    };
                    db.Validations.Add(record);

                    await db.SaveChangesAsync();
                    commitAttempted = true;
                    await tx.CommitAsync(CancellationToken.None);

                    if (commitHook.OnAfterValidationDatabaseCommitAsync is not null)
                    {
                        await commitHook.OnAfterValidationDatabaseCommitAsync(terminalId, request.EventId, CancellationToken.None);
                    }

                    var elapsedSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
                    JanusMetrics.ValidationDurationSeconds.Record(elapsedSeconds,
                        new KeyValuePair<string, object?>("decision", record.Decision.ToString().ToUpperInvariant()));

                    JanusMetrics.ValidationsTotal.Add(1,
                        new KeyValuePair<string, object?>("decision", record.Decision.ToString().ToUpperInvariant()),
                        new KeyValuePair<string, object?>("reason", record.Reason),
                        new KeyValuePair<string, object?>("terminal_id", terminalId),
                        new KeyValuePair<string, object?>("duplicate", "false"));

                    if (decision == ValidationDecision.Accepted) {
                        JanusMetrics.FareAmountDebitedMillimesTotal.Add(fareAmount,
                            new KeyValuePair<string, object?>("terminal_id", terminalId));
                    }

                    return Results.Ok(new {
                        eventId = record.EventId,
                        terminalId = record.TerminalId,
                        cardUid = record.CardUid,
                        decision = record.Decision.ToString().ToUpperInvariant(),
                        reason = record.Reason,
                        balanceMillimes = card?.BalanceMillimes,
                        amountDebited = (decision == ValidationDecision.Accepted ? fareAmount : 0),
                        duplicate = false,
                        receivedAt = record.ReceivedAt
                    });
                } catch (Exception ex) {
                    if (commitAttempted)
                    {
                        // Situation 2 (P1-1 / Lot 2C) : Échec pendant ou immédiatement après CommitAsync() -> résultat d'engagement AMBIGU.
                        // Ne jamais réutiliser le DbContext potentiellement corrompu ni déclencher de retry financier aveugle.
                        // Ouvrir un nouveau scope DI indépendant pour vérifier si PostgreSQL a engagé (TerminalId, EventId).
                        try
                        {
                            await using var reconcileScope = scopeFactory.CreateAsyncScope();
                            var reconcileDb = reconcileScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                            var committed = await reconcileDb.Validations.AsNoTracking()
                                .SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);

                            if (committed is not null)
                            {
                                if (committed.PayloadHash != hash)
                                {
                                    JanusMetrics.RecordDbTransactionEvent("validation", "conflict_recovered");
                                    logger.LogWarning(ex,
                                        "Ambiguous commit reconciliation found conflicting payload: operation=validation, outcome=conflict_recovered, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                        terminalId, request.EventId, attempt);
                                    return BuildIdempotentOrConflictResponse(committed, null);
                                }

                                var reconciledBalance = await reconcileDb.Cards.AsNoTracking()
                                    .Where(c => c.Uid == uid)
                                    .Select(c => (int?)c.BalanceMillimes)
                                    .SingleOrDefaultAsync();

                                JanusMetrics.RecordDbTransactionEvent("validation", "ambiguous_commit_recovered");
                                if (committed.Decision == ValidationDecision.Accepted)
                                {
                                    JanusMetrics.FareAmountDebitedMillimesTotal.Add(fareAmount,
                                        new KeyValuePair<string, object?>("terminal_id", terminalId));
                                }

                                logger.LogWarning(ex,
                                    "Ambiguous commit reconciled via fresh scope (transaction was committed in PostgreSQL): operation=validation, outcome=ambiguous_commit_recovered, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}, exceptionType={ExceptionType}",
                                    terminalId, request.EventId, attempt, ex.GetType().Name);

                                return BuildIdempotentOrConflictResponse(committed, reconciledBalance);
                            }
                        }
                        catch (Exception reconcileEx)
                        {
                            logger.LogError(reconcileEx,
                                "Reconciliation query failed after ambiguous commit: operation=validation, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                terminalId, request.EventId, attempt);
                            return Results.Json(new { error = "database_unavailable", message = "Validation commit state is ambiguous and reconciliation failed. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                        }

                        // L'événement n'a pas été trouvé dans un scope sain : le commit n'a pas abouti côté PostgreSQL.
                        await PostgresTransactions.SafeRollbackAsync(tx, logger, "validation");
                        if (attempt == 0)
                        {
                            JanusMetrics.RecordDbTransactionEvent("validation", "transient_retry");
                            logger.LogWarning(ex,
                                "Commit did not persist in PostgreSQL; retrying transaction once in a new scope: operation=validation, outcome=transient_retry, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                terminalId, request.EventId, attempt);
                            await Task.Delay(TimeSpan.FromMilliseconds(20));
                            continue;
                        }

                        return Results.Json(new { error = "database_unavailable", message = "Validation transaction could not be committed. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    // Situation 1 (P1-1 / P1-2) : Échec AVANT CommitAsync() -> aucun débit engagé par cette tentative.
                    await PostgresTransactions.SafeRollbackAsync(tx, logger, "validation");
                    var sqlState = PostgresErrors.TryGetSqlState(ex);

                    if (string.Equals(sqlState, "23505", StringComparison.Ordinal) || ex is DbUpdateException)
                    {
                        await using var reconcileScope = scopeFactory.CreateAsyncScope();
                        var reconcileDb = reconcileScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                        var winner = await reconcileDb.Validations.AsNoTracking()
                            .SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);

                        if (winner is not null)
                        {
                            JanusMetrics.RecordDbTransactionEvent("validation", "conflict_recovered");
                            if (logger.IsEnabled(LogLevel.Warning))
                            {
                                logger.LogWarning("Concurrent validation race resolved via fresh scope: operation=validation, sqlState={SqlState}, outcome=conflict_recovered, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                    sqlState ?? "DbUpdateException", terminalId, request.EventId, attempt);
                            }
                            return BuildIdempotentOrConflictResponse(winner, null);
                        }
                    }

                    if (string.Equals(sqlState, "55P03", StringComparison.Ordinal) || string.Equals(sqlState, "57014", StringComparison.Ordinal))
                    {
                        JanusMetrics.RecordDbTransactionEvent("validation", "lock_timeout");
                        if (logger.IsEnabled(LogLevel.Warning))
                        {
                            logger.LogWarning("Database lock or statement timeout during validation (no automatic retry): operation=validation, sqlState={SqlState}, outcome=lock_timeout, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                sqlState, terminalId, request.EventId, attempt);
                        }
                        return Results.Json(new { error = "database_busy", message = "Validation resource is temporarily locked by a concurrent operation. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    if (string.Equals(sqlState, "40001", StringComparison.Ordinal) || string.Equals(sqlState, "40P01", StringComparison.Ordinal))
                    {
                        if (attempt < maxTransientRetries)
                        {
                            JanusMetrics.RecordDbTransactionEvent("validation", "transient_retry");
                            if (logger.IsEnabled(LogLevel.Warning))
                            {
                                logger.LogWarning("Transient serialization or deadlock conflict during validation; retrying in a new scope: operation=validation, sqlState={SqlState}, outcome=transient_retry, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                    sqlState, terminalId, request.EventId, attempt);
                            }
                            await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)));
                            continue;
                        }

                        return Results.Json(new { error = "database_busy", message = "Temporary transactional conflict persisted after retries. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                    }

                    logger.LogError(ex, "Unrecoverable error during validation: terminalId={TerminalId}, eventId={EventId}, sqlState={SqlState}, attempt={Attempt}",
                        terminalId, request.EventId, sqlState, attempt);
                    throw;
                }
            }

            return Results.Json(new { error = "database_busy", message = "Unable to complete validation after bounded retries." }, statusCode: StatusCodes.Status503ServiceUnavailable);
        }).RequireAuthorization(JanusPolicies.TerminalOnly).RequireRateLimiting(JanusRateLimitPolicies.Validations);

        return endpoints;
    }
}
