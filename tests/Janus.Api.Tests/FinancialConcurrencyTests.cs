using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Janus.Api.Tests;

public class FinancialConcurrencyTests : IAsyncLifetime
{
    private readonly JanusApiFactory _factory = new();
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string TerminalToken => JanusApiFactory.TerminalToken;
    private static string Terminal02Token => JanusApiFactory.Terminal02Token;
    private static string AdminToken => JanusApiFactory.AdminToken;

    private async Task<string> PrepareCardAsync(int balanceMillimes, CardStatus status = CardStatus.Active, string? label = null)
    {
        var uid = "04FIN" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var db = _factory.CreateDbContext();
        var card = new TransitCard
        {
            Uid = uid,
            Status = status,
            Label = label ?? $"Test Card {uid}",
            BalanceMillimes = balanceMillimes
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return uid;
    }

    private static HttpRequestMessage CreateValidationRequest(string cardUid, Guid? eventId = null, string? token = null, DateTimeOffset? occurredAt = null)
    {
        var id = eventId ?? Guid.NewGuid();
        var time = occurredAt ?? DateTimeOffset.UtcNow;
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(new
            {
                eventId = id,
                cardUid,
                occurredAt = time,
                eventType = "TRANSPORT_VALIDATION"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? TerminalToken);
        return req;
    }

    private static HttpRequestMessage CreateCardUpsertRequest(string cardUid, int balanceMillimes, string? status = "Active", string? label = null, string? reason = null, string? token = null)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{cardUid}")
        {
            Content = JsonContent.Create(new
            {
                balanceMillimes,
                status,
                label = label ?? $"Card {cardUid}",
                reason = reason ?? "Admin financial balance test"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token ?? AdminToken);
        return req;
    }

    // --- Scénario 1 : Validation concurrente avec recharge / mise à jour administrative ---
    [Fact]
    public async Task Test01_Concurrent_AdminUpdate_And_TurnstileValidation_NoDeadlock_And_ConsistentLedger()
    {
        var uid = await PrepareCardAsync(10000);

        // Lancement concurrent d'un passage portillon (débit de 1500 m) et d'une recharge admin (fixe solde à 15000 m)
        var valTask = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid));
        });

        var adminTask = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateCardUpsertRequest(uid, balanceMillimes: 15000, reason: "Recharge guichet concurrent"));
        });

        await Task.WhenAll(valTask, adminTask);

        var valResp = await valTask;
        var adminResp = await adminTask;

        Assert.Equal(HttpStatusCode.OK, valResp.StatusCode);
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        using var db = _factory.CreateDbContext();
        var finalCard = await db.Cards.SingleAsync(c => c.Uid == uid);
        var fareTxs = await db.FareTransactions.Where(t => t.CardUid == uid).ToListAsync();
        var audits = await db.CardAuditLogs.Where(a => a.CardUid == uid).ToListAsync();

        // 1. Exactement un débit de validation enregistré
        Assert.Single(fareTxs);
        Assert.Equal(1500, fareTxs[0].AmountMillimes);

        // 2. Exactement une trace d'audit administratif enregistrée
        Assert.Single(audits);
        Assert.Equal(15000, audits[0].NewBalanceMillimes);

        // 3. Le solde final doit être cohérent et strictement non négatif
        Assert.True(finalCard.BalanceMillimes >= 0);
    }

    // --- Scénario 2 : Deux validations concurrentes lorsque le solde est proche du tarif ---
    [Fact]
    public async Task Test02_Concurrent_Validations_ExceedingBalance_AllowsOnlyOneDebit_NoNegativeBalance()
    {
        // Solde initial = 2000 m (tarif = 1500 m). Deux validations concurrentes ne peuvent pas toutes deux passer.
        var uid = await PrepareCardAsync(2000);

        // Utilise Terminal 1 et Terminal 2 pour éviter le blocage anti-passback local et tester l'épuisement financier
        var task1 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid, token: TerminalToken));
        });

        var task2 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid, token: Terminal02Token));
        });

        await Task.WhenAll(task1, task2);

        var resp1 = await task1;
        var resp2 = await task2;

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        var decisions = new[] { body1.GetProperty("decision").GetString(), body2.GetProperty("decision").GetString() };
        Assert.Contains("ACCEPTED", decisions);
        Assert.Contains("REJECTED", decisions);

        using var db = _factory.CreateDbContext();
        var finalCard = await db.Cards.SingleAsync(c => c.Uid == uid);
        var fareTxs = await db.FareTransactions.Where(t => t.CardUid == uid).ToListAsync();

        // Exactement un débit financier de 1500 m
        Assert.Single(fareTxs);
        Assert.Equal(1500, fareTxs[0].AmountMillimes);

        // Solde final = 500 m (2000 - 1500), jamais négatif
        Assert.Equal(500, finalCard.BalanceMillimes);
    }

    // --- Scénario 3 : Deux requêtes identiques avec le même identifiant d'événement (rejeu concurrent) ---
    [Fact]
    public async Task Test03_Concurrent_DuplicateEventId_ExactReplay_SingleDebit()
    {
        var uid = await PrepareCardAsync(10000);
        var sharedEventId = Guid.NewGuid();
        var sharedTime = DateTimeOffset.UtcNow;

        var task1 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid, eventId: sharedEventId, occurredAt: sharedTime));
        });

        var task2 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid, eventId: sharedEventId, occurredAt: sharedTime));
        });

        await Task.WhenAll(task1, task2);

        var resp1 = await task1;
        var resp2 = await task2;

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ACCEPTED", body1.GetProperty("decision").GetString());
        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());

        var duplicates = new[] { body1.GetProperty("duplicate").GetBoolean(), body2.GetProperty("duplicate").GetBoolean() };
        Assert.Contains(false, duplicates);
        Assert.Contains(true, duplicates);

        using var db = _factory.CreateDbContext();
        var fareTxs = await db.FareTransactions.Where(t => t.CardUid == uid).ToListAsync();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);

        // Exactement un seul débit financier pour l'événement
        Assert.Single(fareTxs);
        Assert.Equal(8500, card.BalanceMillimes);
    }

    // --- Scénario 4 : Même identifiant d'événement avec payload différent (conflit concurrent) ---
    [Fact]
    public async Task Test04_Concurrent_DuplicateEventId_ConflictingPayload_OneDebitOneConflict()
    {
        var uid1 = await PrepareCardAsync(10000);
        var uid2 = await PrepareCardAsync(10000);
        var sharedEventId = Guid.NewGuid();

        var task1 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid1, eventId: sharedEventId));
        });

        var task2 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid2, eventId: sharedEventId));
        });

        await Task.WhenAll(task1, task2);

        var resp1 = await task1;
        var resp2 = await task2;

        var statuses = new[] { resp1.StatusCode, resp2.StatusCode };
        Assert.Contains(HttpStatusCode.OK, statuses);
        Assert.Contains(HttpStatusCode.Conflict, statuses);

        using var db = _factory.CreateDbContext();
        var totalTxs = await db.FareTransactions.Where(t => t.EventId == sharedEventId).ToListAsync();
        Assert.Single(totalTxs);
    }

    // --- Scénario 5 : Carte révoquée validée simultanément : aucun débit financier ---
    [Fact]
    public async Task Test05_RevokedCard_ConcurrentValidation_RejectedWithZeroDebit()
    {
        var uid = await PrepareCardAsync(10000, status: CardStatus.Revoked);

        var task1 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid));
        });

        var task2 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateValidationRequest(uid));
        });

        await Task.WhenAll(task1, task2);

        var resp1 = await task1;
        var resp2 = await task2;

        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("REJECTED", body1.GetProperty("decision").GetString());
        Assert.Equal("CARD_REVOKED", body1.GetProperty("reason").GetString());
        Assert.Equal(0, body1.GetProperty("amountDebited").GetInt32());

        using var db = _factory.CreateDbContext();
        var fareTxs = await db.FareTransactions.Where(t => t.CardUid == uid).ToListAsync();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);

        Assert.Empty(fareTxs);
        Assert.Equal(10000, card.BalanceMillimes);
    }

    // --- Scénario 6 : Deux recharges administratives simultanées sur la même carte ---
    [Fact]
    public async Task Test06_Concurrent_AdminRecharges_BothSerializedUnderLock()
    {
        var uid = await PrepareCardAsync(5000);

        var task1 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateCardUpsertRequest(uid, balanceMillimes: 12000, reason: "Recharge A"));
        });

        var task2 = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateCardUpsertRequest(uid, balanceMillimes: 18000, reason: "Recharge B"));
        });

        await Task.WhenAll(task1, task2);

        var resp1 = await task1;
        var resp2 = await task2;

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        using var db = _factory.CreateDbContext();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);
        var audits = await db.CardAuditLogs.Where(a => a.CardUid == uid).OrderBy(a => a.CreatedAt).ToListAsync();

        // Exactement deux audits créés, solde final correspond à la dernière écriture sérialisée
        Assert.Equal(2, audits.Count);
        Assert.True(card.BalanceMillimes == 12000 || card.BalanceMillimes == 18000);
    }

    private async Task WaitForPostgresRowLockAsync(string uid, TimeSpan timeout)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource(timeout);
        cts.Token.Register(() => tcs.TrySetException(new TimeoutException($"Timed out after {timeout.TotalSeconds}s waiting for PostgreSQL row lock on card {uid}.")));

        _ = Task.Run(async () =>
        {
            var connStr = _factory.CreateDbContext().Database.GetConnectionString();
            while (!tcs.Task.IsCompleted)
            {
                try
                {
                    await using var conn = new NpgsqlConnection(connStr);
                    await conn.OpenAsync(cts.Token);
                    await using var cmd = conn.CreateCommand();
                    cmd.CommandText = @"
                        SELECT EXISTS (
                            SELECT 1
                            FROM pg_locks l
                            JOIN pg_stat_activity a ON l.pid = a.pid
                            WHERE NOT l.granted
                              AND l.locktype = 'transactionid'
                              AND a.datname = current_database()
                              AND a.pid <> pg_backend_pid()
                              AND a.state = 'active'
                              AND a.query ILIKE '%Cards%'
                        )";
                    var isBlocked = (bool)(await cmd.ExecuteScalarAsync(cts.Token) ?? false);

                    if (isBlocked)
                    {
                        tcs.TrySetResult(true);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cts.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Propagate unexpected database errors instead of masking them
                    if (ex is not NpgsqlException)
                    {
                        tcs.TrySetException(ex);
                        return;
                    }
                }

                await Task.Delay(10, cts.Token);
            }
        }, cts.Token);

        await tcs.Task;
    }

    // --- Scénario 7 : Reproduction du défaut CONC-01 (lecture non verrouillée produisant un audit désynchronisé) ---
    [Fact]
    public async Task Test07_AdminUpsert_UnderConcurrentDebit_MustReflectCommittedBalanceInAuditLog()
    {
        var uid = await PrepareCardAsync(10000);

        // 1. Simule une validation en cours qui acquiert le verrou pessimiste FOR UPDATE et débite 1500 m
        using var validationDb = _factory.CreateDbContext();
        await using var validationTx = await validationDb.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);

        var cardInTx = await validationDb.Cards
            .FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {uid} FOR UPDATE")
            .SingleAsync();

        cardInTx.BalanceMillimes -= 1500;
        validationDb.FareTransactions.Add(new FareTransaction
        {
            CardId = cardInTx.Id,
            CardUid = uid,
            TerminalId = "TERMINAL-DEMO-01",
            EventId = Guid.NewGuid(),
            AmountMillimes = 1500,
            BalanceAfterMillimes = cardInTx.BalanceMillimes,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await validationDb.SaveChangesAsync();

        // 2. Pendant que le verrou est actif, la requête administrative PUT /api/cards/{uid} est lancée
        var adminTask = Task.Run(async () =>
        {
            using var localClient = _factory.CreateClient();
            return await localClient.SendAsync(CreateCardUpsertRequest(uid, balanceMillimes: 20000, reason: "Recharge guichet"));
        });

        // 3. Synchronisation déterministe via TaskCompletionSource :
        // Attend que PostgreSQL signale formellement que PUT est en attente du verrou FOR UPDATE
        await WaitForPostgresRowLockAsync(uid, TimeSpan.FromSeconds(5));

        // 4. Commit de la validation (libère le verrou FOR UPDATE et débloque PUT)
        await validationTx.CommitAsync();

        var adminResp = await adminTask;
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);

        using var verifyDb = _factory.CreateDbContext();
        var finalCard = await verifyDb.Cards.SingleAsync(c => c.Uid == uid);
        var fareTx = await verifyDb.FareTransactions.Where(t => t.CardUid == uid).SingleAsync();
        var audit = await verifyDb.CardAuditLogs
            .Where(a => a.CardUid == uid && a.Reason == "Recharge guichet")
            .SingleAsync();

        // CONTRÔLE DES INVARIANTS FINANCIERS ET DES TRACES D'AUDIT :
        // 1. Solde final de la carte (20000 m fixé par l'admin)
        Assert.Equal(20000, finalCard.BalanceMillimes);

        // 2. Trace financière de la validation (débit de 1500 m, solde intermédiaire à 8500 m)
        Assert.Equal(1500, fareTx.AmountMillimes);
        Assert.Equal(8500, fareTx.BalanceAfterMillimes);

        // 3. Trace d'audit administratif : PreviousBalance DOIT valoir 8500 m (solde validé engagé)
        // et NON 10000 m (solde antérieur lu hors verrou).
        Assert.Equal(8500, audit.PreviousBalanceMillimes);
        Assert.Equal(20000, audit.NewBalanceMillimes);
        Assert.Equal("Recharge guichet", audit.Reason);
    }

    // --- Scénario 8 : Contrainte SQL PostgreSQL CK_Cards_BalanceMillimes_NonNegative ---
    [Fact]
    public async Task Test08_CheckConstraint_NegativeBalance_RejectedByPostgreSQL()
    {
        var uid = await PrepareCardAsync(1000);

        using var db = _factory.CreateDbContext();

        // 1. Tentative d'écriture directe d'un solde négatif
        var postgresEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Cards\" SET \"BalanceMillimes\" = -500 WHERE \"Uid\" = {uid}");
        });

        Assert.Equal("23514", postgresEx.SqlState);
        Assert.Equal("CK_Cards_BalanceMillimes_NonNegative", postgresEx.ConstraintName);

        // 2. Vérification que le solde en base est resté inchangé
        using var verifyDb = _factory.CreateDbContext();
        var card = await verifyDb.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(1000, card.BalanceMillimes);
    }
}
