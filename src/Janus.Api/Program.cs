using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Janus.Api;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<JanusDbContext>(o => o.UseNpgsql(builder.Configuration.GetConnectionString("JanusDb")));

builder.Services.AddOpenTelemetry()
    .ConfigureResource(r => r.AddService("janus-api", serviceVersion: "1.0.0"))
    .WithMetrics(metrics => {
        metrics
            .AddAspNetCoreInstrumentation()
            .AddRuntimeInstrumentation()
            .AddMeter(JanusMetrics.MeterName)
            .AddMeter("Npgsql")
            .AddView(
                instrumentName: "janus_validation_duration_seconds",
                new ExplicitBucketHistogramConfiguration
                {
                    Boundaries = [0.001, 0.002, 0.005, 0.010, 0.025, 0.050, 0.075, 0.100, 0.250, 0.500, 1.0, 2.5, 5.0]
                })
            .AddPrometheusExporter();
    });

var app = builder.Build();

app.MapPrometheusScrapingEndpoint();

await using (var scope = app.Services.CreateAsyncScope()) {
    var db = scope.ServiceProvider.GetRequiredService<JanusDbContext>();
    for (int attempt = 0; ; attempt++) {
        try {
            await db.Database.MigrateAsync();
            if (!await db.Cards.AnyAsync()) {
                db.Cards.AddRange(
                    new TransitCard { Uid = "04A1B2C3D4E5F6", Status = CardStatus.Active, Label = "Carte démo active", BalanceMillimes = 10000 },
                    new TransitCard { Uid = "04DEAD00000001", Status = CardStatus.Revoked, Label = "Carte démo révoquée", BalanceMillimes = 5000 }
                );
                await db.SaveChangesAsync();
            }
            break;
        } catch when (attempt < 29) {
            await Task.Delay(TimeSpan.FromSeconds(2));
        }
    }
}

static string ExtractBearer(HttpRequest req) {
    var h = req.Headers.Authorization.ToString();
    return h.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? h[7..].Trim() : "";
}

static bool CheckToken(string? supplied, string? expected) {
    if (string.IsNullOrEmpty(supplied) || string.IsNullOrEmpty(expected)) return false;
    return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(supplied), Encoding.UTF8.GetBytes(expected));
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "janus-api" }));

app.MapGet("/api/demo/cards", async (JanusDbContext db) =>
    Results.Ok(await db.Cards.OrderBy(c => c.Uid).Select(c => new {
        c.Uid,
        status = c.Status.ToString(),
        c.Label,
        balanceMillimes = c.BalanceMillimes
    }).ToListAsync()));

app.MapGet("/api/cards/{uid}", async (string uid, HttpRequest http, IConfiguration config, JanusDbContext db) => {
    var terminalExpected = config["TerminalAuth:DemoToken"];
    var adminExpected = config["AdminAuth:DemoToken"];
    var supplied = ExtractBearer(http);

    if (!CheckToken(supplied, terminalExpected) && !CheckToken(supplied, adminExpected))
        return Results.Json(new { error = "unauthorized", message = "Valid terminal or admin credential required." }, statusCode: 401);

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
});

app.MapGet("/api/cards/{uid}/audit", async (string uid, HttpRequest http, IConfiguration config, JanusDbContext db) => {
    var adminExpected = config["AdminAuth:DemoToken"];
    var terminalExpected = config["TerminalAuth:DemoToken"];
    var supplied = ExtractBearer(http);

    // 1. Refus explicite si un terminal tente d'accéder aux journaux d'audit administratif
    if (!string.IsNullOrEmpty(terminalExpected) && CheckToken(supplied, terminalExpected))
        return Results.Json(new { error = "forbidden", message = "Terminal credentials cannot view administrative audit logs." }, statusCode: 403);

    // 2. Vérification stricte des droits administrateur
    if (string.IsNullOrEmpty(adminExpected) || !CheckToken(supplied, adminExpected))
        return Results.Json(new { error = "unauthorized", message = "Admin credential missing or invalid." }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
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

    var totalAudits = await db.CardAuditLogs.AsNoTracking().CountAsync(a => a.CardUid == normalized);

    var audits = await db.CardAuditLogs.AsNoTracking()
        .Where(a => a.CardUid == normalized)
        .OrderByDescending(a => a.CreatedAt)
        .ThenByDescending(a => a.Id)
        .Skip(offset)
        .Take(limit)
        .Select(a => new {
            a.Id,
            a.Action,
            a.PreviousBalanceMillimes,
            a.NewBalanceMillimes,
            a.PreviousStatus,
            a.NewStatus,
            a.Reason,
            a.Actor,
            a.CreatedAt
        })
        .ToListAsync();

    return Results.Ok(new {
        cardUid = normalized,
        totalCount = totalAudits,
        limit,
        offset,
        auditLogs = audits
    });
});

app.MapPut("/api/cards/{uid}", async (string uid, CardUpsertRequest request, HttpRequest http, IConfiguration config, IHostEnvironment env, JanusDbContext db, ILogger<Program> logger) => {
    var adminExpected = config["AdminAuth:DemoToken"];
    var terminalExpected = config["TerminalAuth:DemoToken"];
    var supplied = ExtractBearer(http);

    // 1. Refus explicite si un terminal tente d'effectuer une opération administrative
    if (!string.IsNullOrEmpty(terminalExpected) && CheckToken(supplied, terminalExpected))
        return Results.Json(new { error = "forbidden", message = "Terminal credentials cannot perform card administration." }, statusCode: 403);

    // 2. Vérification des droits administrateur
    if (string.IsNullOrEmpty(adminExpected) || !CheckToken(supplied, adminExpected))
        return Results.Json(new { error = "unauthorized", message = "Admin credential missing or invalid." }, statusCode: 401);

    if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
        return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

    if (request.BalanceMillimes < 0)
        return Results.BadRequest(new { error = "invalid_balance", message = "Balance cannot be negative." });

    // 3. Validation stricte du statut si fourni (uniquement Active ou Revoked, insensible à la casse)
    CardStatus? requestedStatus = null;
    if (!string.IsNullOrWhiteSpace(request.Status)) {
        var s = request.Status.Trim();
        if (s.Equals("Active", StringComparison.OrdinalIgnoreCase)) {
            requestedStatus = CardStatus.Active;
        } else if (s.Equals("Revoked", StringComparison.OrdinalIgnoreCase)) {
            requestedStatus = CardStatus.Revoked;
        } else {
            return Results.BadRequest(new { error = "invalid_status", message = "Status must be 'Active' or 'Revoked'." });
        }
    }

    var normalized = uid.Trim().ToUpperInvariant();
    var card = await db.Cards.SingleOrDefaultAsync(c => c.Uid == normalized);
    var prevBalance = card?.BalanceMillimes;
    var prevStatus = card?.Status.ToString();
    var action = card is null ? "ADMIN_CARD_CREATED" : "ADMIN_CARD_UPDATED";

    // 4. Détermination du statut effectif :
    // - Pour une nouvelle carte : Active par défaut si aucun statut n'est fourni.
    // - Pour une carte existante : conserve son statut actuel si aucun statut n'est fourni.
    CardStatus targetStatus = card is null
        ? (requestedStatus ?? CardStatus.Active)
        : (requestedStatus ?? card.Status);

    // 5. Motif d'audit distinct du libellé de la carte
    var auditReason = !string.IsNullOrWhiteSpace(request.Reason)
        ? request.Reason.Trim()
        : (card is null ? "Initial card provisioning" : "Admin balance/status update");

    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
    try {
        if (card is null) {
            card = new TransitCard {
                Uid = normalized,
                Status = targetStatus,
                Label = request.Label ?? $"Card {normalized}",
                BalanceMillimes = request.BalanceMillimes
            };
            db.Cards.Add(card);
        } else {
            card.BalanceMillimes = request.BalanceMillimes;
            card.Status = targetStatus;
            if (!string.IsNullOrEmpty(request.Label))
                card.Label = request.Label;
        }

        // Traçabilité d'audit avec statut réel persisté
        var audit = new CardAuditLog {
            CardUid = normalized,
            Action = action,
            PreviousBalanceMillimes = prevBalance,
            NewBalanceMillimes = card.BalanceMillimes,
            PreviousStatus = prevStatus,
            NewStatus = card.Status.ToString(),
            Reason = auditReason,
            Actor = "ADMIN",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CardAuditLogs.Add(audit);

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        // Journalisation sans secrets
        logger.LogInformation("Admin card update: action={Action}, uid={CardUid}, balance={OldBal}->{NewBal}, status={OldStat}->{NewStat}, reason={Reason}",
            action, normalized, prevBalance, request.BalanceMillimes, prevStatus, card.Status, auditReason);

        return Results.Ok(new {
            card.Uid,
            status = card.Status.ToString(),
            card.Label,
            card.BalanceMillimes
        });
    } catch (Exception ex) {
        await tx.RollbackAsync();
        logger.LogError(ex, "Failed to persist admin card upsert for {CardUid}", normalized);
        throw;
    }
});

app.MapPost("/api/validations", async (ValidationRequest request, HttpRequest http, IConfiguration config, JanusDbContext db, ILogger<Program> logger) => {
    var expected = config["TerminalAuth:DemoToken"];
    var supplied = ExtractBearer(http);
    if (string.IsNullOrEmpty(expected) || !CheckToken(supplied, expected))
        return Results.Json(new { error = "unauthorized", message = "Terminal credential missing or invalid." }, statusCode: 401);

    const string terminalId = "TERMINAL-DEMO-01";
    var validationStartTime = Stopwatch.GetTimestamp();

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

    var uid = request.CardUid.Trim().ToUpperInvariant();
    var canonical = JsonSerializer.Serialize(new { eventId = request.EventId, cardUid = uid, occurredAt = request.OccurredAt?.ToUniversalTime(), eventType = request.EventType });
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    var existing = await db.Validations.AsNoTracking().SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);
    if (existing is not null) {
        var elapsedDuplicateSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
        if (existing.PayloadHash != hash) {
            JanusMetrics.ValidationDurationSeconds.Record(elapsedDuplicateSeconds,
                new KeyValuePair<string, object?>("decision", "CONFLICT"));
            JanusMetrics.ValidationsTotal.Add(1,
                new KeyValuePair<string, object?>("decision", "CONFLICT"),
                new KeyValuePair<string, object?>("reason", "IDEMPOTENCY_CONFLICT"),
                new KeyValuePair<string, object?>("terminal_id", terminalId),
                new KeyValuePair<string, object?>("duplicate", "false"));
            return Results.Conflict(new { error = "idempotency_conflict", message = "eventId already used with different payload." });
        }

        JanusMetrics.ValidationDurationSeconds.Record(elapsedDuplicateSeconds,
            new KeyValuePair<string, object?>("decision", existing.Decision.ToString().ToUpperInvariant()));
        JanusMetrics.ValidationsTotal.Add(1,
            new KeyValuePair<string, object?>("decision", existing.Decision.ToString().ToUpperInvariant()),
            new KeyValuePair<string, object?>("reason", existing.Reason),
            new KeyValuePair<string, object?>("terminal_id", terminalId),
            new KeyValuePair<string, object?>("duplicate", "true"));

        return Results.Ok(new {
            eventId = existing.EventId,
            terminalId = existing.TerminalId,
            cardUid = existing.CardUid,
            decision = existing.Decision.ToString().ToUpperInvariant(),
            reason = existing.Reason,
            duplicate = true,
            receivedAt = existing.ReceivedAt
        });
    }

    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
    try {
        var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {uid} FOR UPDATE").SingleOrDefaultAsync();

        ValidationDecision decision;
        string reason;
        if (card is null) {
            decision = ValidationDecision.Rejected;
            reason = "UNKNOWN_CARD";
        } else if (card.Status == CardStatus.Revoked) {
            decision = ValidationDecision.Rejected;
            reason = "CARD_REVOKED";
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
                CreatedAt = DateTimeOffset.UtcNow
            });
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
            ReceivedAt = DateTimeOffset.UtcNow
        };
        db.Validations.Add(record);

        await db.SaveChangesAsync();
        await tx.CommitAsync();

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
    } catch (DbUpdateException ex) {
        await tx.RollbackAsync();
        db.ChangeTracker.Clear();
        var winner = await db.Validations.AsNoTracking().SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);
        if (winner is null) {
            logger.LogError(ex, "DbUpdateException occurred during validation but winner record was not found for terminalId={TerminalId}, eventId={EventId}", terminalId, request.EventId);
            throw;
        }
        var elapsedRaceSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
        if (winner.PayloadHash != hash) {
            JanusMetrics.ValidationDurationSeconds.Record(elapsedRaceSeconds,
                new KeyValuePair<string, object?>("decision", "CONFLICT"));
            JanusMetrics.ValidationsTotal.Add(1,
                new KeyValuePair<string, object?>("decision", "CONFLICT"),
                new KeyValuePair<string, object?>("reason", "IDEMPOTENCY_CONFLICT"),
                new KeyValuePair<string, object?>("terminal_id", terminalId),
                new KeyValuePair<string, object?>("duplicate", "false"));
            return Results.Conflict(new { error = "idempotency_conflict", message = "eventId already used with different payload." });
        }

        JanusMetrics.ValidationDurationSeconds.Record(elapsedRaceSeconds,
            new KeyValuePair<string, object?>("decision", winner.Decision.ToString().ToUpperInvariant()));
        JanusMetrics.ValidationsTotal.Add(1,
            new KeyValuePair<string, object?>("decision", winner.Decision.ToString().ToUpperInvariant()),
            new KeyValuePair<string, object?>("reason", winner.Reason),
            new KeyValuePair<string, object?>("terminal_id", terminalId),
            new KeyValuePair<string, object?>("duplicate", "true"));

        return Results.Ok(new {
            eventId = winner.EventId,
            terminalId = winner.TerminalId,
            cardUid = winner.CardUid,
            decision = winner.Decision.ToString().ToUpperInvariant(),
            reason = winner.Reason,
            duplicate = true,
            receivedAt = winner.ReceivedAt
        });
    }
});

app.Run();
