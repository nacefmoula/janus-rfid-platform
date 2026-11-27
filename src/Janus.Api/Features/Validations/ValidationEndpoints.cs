using System.Diagnostics;
using System.Security.Claims;
using Janus.Api.Infrastructure.Persistence;
using Janus.Api.Observability;
using Janus.Api.Security;
using Janus.Domain.Validations;
using Janus.Domain.Wallet;
using Microsoft.EntityFrameworkCore;

namespace Janus.Api.Features.Validations;

/// <summary>Validation d'un passage de carte par un terminal (débit, idempotence, anti-passback).</summary>
public static class ValidationEndpoints
{
    private const string OperationName = "validation";

    private static readonly FinancialOperationMessages Messages = new(
        Busy: "Validation resource is temporarily locked by a concurrent operation. Please retry with the same eventId.",
        TransientConflictPersisted: "Temporary transactional conflict persisted after retries. Please retry with the same eventId.",
        RetriesExhausted: "Unable to complete validation after bounded retries.",
        CommitNotPersisted: "Validation transaction could not be committed. Please retry with the same eventId.",
        ReconciliationFailed: "Validation commit state is ambiguous and reconciliation failed. Please retry with the same eventId.");

    /// <summary>Enregistre les routes de la fonctionnalité.</summary>
    public static IEndpointRouteBuilder MapValidationEndpoints(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/api/validations", ValidateAsync)
            .RequireAuthorization(JanusPolicies.TerminalOnly)
            .RequireRateLimiting(JanusRateLimitPolicies.Validations);

        return endpoints;
    }

    private static async Task<IResult> ValidateAsync(
        ValidationRequest request,
        ClaimsPrincipal user,
        IConfiguration config,
        FinancialTransactionRunner runner,
        JanusTransactionCommitHook commitHook,
        TimeProvider timeProvider)
    {
        // SEC-02: Identité du terminal déterminée côté serveur par le jeton authentifié
        var terminalId = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "TERMINAL-DEMO-01";
        var metrics = new ValidationMetrics(terminalId, Stopwatch.GetTimestamp());
        var serverNow = timeProvider.GetUtcNow();

        if (!ValidationPolicy.IsWellFormed(request.EventId, request.CardUid, request.EventType))
        {
            metrics.RecordRejectedInvalidRequest();
            return Results.BadRequest(new { error = "invalid_request", message = "eventId, cardUid and supported eventType are required." });
        }

        var fareConfig = config["Fare:AmountMillimes"];
        if (!int.TryParse(fareConfig, out var fareAmount) || fareAmount <= 0)
            return Results.Json(new { error = "invalid_fare_configuration", message = "Fare amount must be configured and strictly positive." }, statusCode: 500);

        var isApbEnabled = !string.Equals(config["AntiPassback:Enabled"], "false", StringComparison.OrdinalIgnoreCase);
        var apbWindowSeconds = int.TryParse(config["AntiPassback:WindowSeconds"], out var parsedWindow) && parsedWindow > 0 ? parsedWindow : 30;

        var uid = ValidationPolicy.NormalizeCardUid(request.CardUid);
        var hash = ValidationPayloadFingerprint.Compute(request.EventId, uid, request.OccurredAt, request.EventType);

        IResult BuildIdempotentOrConflictResponse(ValidationEvent existingRecord, int? reconciledBalanceMillimes)
        {
            if (existingRecord.PayloadHash != hash)
            {
                metrics.RecordConflict();
                return Results.Conflict(new { error = "idempotency_conflict", message = "eventId already used with different payload." });
            }

            metrics.RecordOutcome(existingRecord.Decision, existingRecord.Reason, duplicate: true);
            var decision = existingRecord.Decision.ToString().ToUpperInvariant();

            if (reconciledBalanceMillimes.HasValue)
            {
                return Results.Ok(new {
                    eventId = existingRecord.EventId,
                    terminalId = existingRecord.TerminalId,
                    cardUid = existingRecord.CardUid,
                    decision,
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
                decision,
                reason = existingRecord.Reason,
                duplicate = true,
                receivedAt = existingRecord.ReceivedAt
            });
        }

        Task<ValidationEvent?> FindExistingAsync(JanusDbContext db) =>
            db.Validations.AsNoTracking().SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);

        return await runner.ExecuteAsync(new FinancialOperation
        {
            Name = OperationName,
            CorrelationKey = $"terminal={terminalId}, eventId={request.EventId}",
            Messages = Messages,
            // Une DbUpdateException avant COMMIT peut masquer la violation d'unicité (TerminalId, EventId) d'un concurrent.
            IsIdempotencyRace = static (ex, sqlState) =>
                string.Equals(sqlState, PostgresErrors.UniqueViolation, StringComparison.Ordinal) || ex is DbUpdateException,

            FindCommittedAsync = async db =>
                await FindExistingAsync(db) is { } existing ? BuildIdempotentOrConflictResponse(existing, null) : null,

            ExecuteAsync = async (db, _) =>
            {
                var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {uid} FOR UPDATE").SingleOrDefaultAsync();

                var isPassback = false;
                if (ValidationPolicy.RequiresPassbackCheck(card?.Status, isApbEnabled))
                {
                    var cutoff = serverNow - TimeSpan.FromSeconds(apbWindowSeconds);
                    isPassback = await db.Validations.AnyAsync(v =>
                        v.TerminalId == terminalId &&
                        v.CardUid == uid &&
                        v.Decision == ValidationDecision.Accepted &&
                        v.ReceivedAt >= cutoff);
                }

                var outcome = ValidationPolicy.Decide(card?.Status, card?.BalanceMillimes ?? 0, fareAmount, isPassback);

                if (outcome.IsAccepted)
                {
                    card!.Debit(fareAmount);
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
                        IdempotencyKey = LedgerIdempotencyKeys.FareDebit(terminalId, request.EventId),
                        Actor = terminalId,
                        CreatedAt = serverNow
                    });
                }

                var record = new ValidationEvent {
                    TerminalId = terminalId,
                    EventId = request.EventId,
                    CardUid = uid,
                    PayloadHash = hash,
                    EventType = request.EventType,
                    Decision = outcome.Decision,
                    Reason = outcome.Reason,
                    OccurredAt = request.OccurredAt?.ToUniversalTime(),
                    ReceivedAt = serverNow
                };
                db.Validations.Add(record);

                return FinancialStep.Commit(() =>
                {
                    metrics.RecordOutcome(record.Decision, record.Reason, duplicate: false);
                    if (outcome.IsAccepted)
                    {
                        metrics.RecordDebit(fareAmount);
                    }

                    return Results.Ok(new {
                        eventId = record.EventId,
                        terminalId = record.TerminalId,
                        cardUid = record.CardUid,
                        decision = record.Decision.ToString().ToUpperInvariant(),
                        reason = record.Reason,
                        balanceMillimes = card?.BalanceMillimes,
                        amountDebited = (outcome.IsAccepted ? fareAmount : 0),
                        duplicate = false,
                        receivedAt = record.ReceivedAt
                    });
                });
            },

            // Lu au moment du COMMIT (et non à la construction) : le point d'injection peut être armé par un test à tout moment.
            AfterCommitAsync = () => commitHook.OnAfterValidationDatabaseCommitAsync is { } hook
                ? hook(terminalId, request.EventId, CancellationToken.None)
                : Task.CompletedTask,

            ReconcileAmbiguousCommitAsync = async (db, _) =>
            {
                var committed = await FindExistingAsync(db);
                if (committed is null)
                {
                    return null;
                }

                if (committed.PayloadHash != hash)
                {
                    JanusMetrics.RecordDbTransactionEvent(OperationName, "conflict_recovered");
                    return BuildIdempotentOrConflictResponse(committed, null);
                }

                var reconciledBalance = await db.Cards.AsNoTracking()
                    .Where(c => c.Uid == uid)
                    .Select(c => (int?)c.BalanceMillimes)
                    .SingleOrDefaultAsync();

                JanusMetrics.RecordDbTransactionEvent(OperationName, "ambiguous_commit_recovered");
                if (committed.Decision == ValidationDecision.Accepted)
                {
                    metrics.RecordDebit(fareAmount);
                }

                return BuildIdempotentOrConflictResponse(committed, reconciledBalance);
            }
        });
    }

    /// <summary>Enregistrement des métriques d'une validation (labels à cardinalité bornée, jamais d'UID de carte).</summary>
    private sealed class ValidationMetrics(string terminalId, long startTimestamp)
    {
        public void RecordRejectedInvalidRequest() =>
            Record("REJECTED", "INVALID_REQUEST", duplicate: false);

        public void RecordConflict()
        {
            JanusMetrics.RecordConflictValidation();
            Record("CONFLICT", "IDEMPOTENCY_CONFLICT", duplicate: false);
        }

        public void RecordOutcome(ValidationDecision decision, string reason, bool duplicate) =>
            Record(decision.ToString().ToUpperInvariant(), reason, duplicate);

        public void RecordDebit(int fareAmount) =>
            JanusMetrics.FareAmountDebitedMillimesTotal.Add(fareAmount,
                new KeyValuePair<string, object?>("terminal_id", terminalId));

        private void Record(string decision, string reason, bool duplicate)
        {
            JanusMetrics.ValidationDurationSeconds.Record(Stopwatch.GetElapsedTime(startTimestamp).TotalSeconds,
                new KeyValuePair<string, object?>("decision", decision));
            JanusMetrics.ValidationsTotal.Add(1,
                new KeyValuePair<string, object?>("decision", decision),
                new KeyValuePair<string, object?>("reason", reason),
                new KeyValuePair<string, object?>("terminal_id", terminalId),
                new KeyValuePair<string, object?>("duplicate", duplicate ? "true" : "false"));
        }
    }
}
