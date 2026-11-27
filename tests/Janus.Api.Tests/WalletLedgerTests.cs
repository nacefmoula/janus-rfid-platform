using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Janus.Api.Tests;

/// <summary>
/// LOT-3B — Cœur financier :
/// - FIN-06 : journal comptable append-only (tout mouvement de solde y est inscrit, invariant SUM == solde) ;
/// - FIN-07 : crédit administratif idempotent (delta sous verrou, jamais d'écrasement) ;
/// - FIN-08 : garde de concurrence optimiste sur le PUT (expectedBalanceMillimes) ;
/// - FIN-09 : consultation du journal avec contrôle de réconciliation.
/// </summary>
public class WalletLedgerTests : IAsyncLifetime
{
    private const int Fare = 1500;
    private readonly JanusApiFactory _factory = new() { AntiPassbackEnabledOverride = false };
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    private static string NewUid() => "04LG" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static HttpRequestMessage Authed(HttpMethod method, string url, string token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private async Task<string> CreateCardViaApiAsync(int balance, string status = "Active")
    {
        var uid = NewUid();
        using var resp = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { balanceMillimes = balance, status, reason = "ledger test provisioning" }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return uid;
    }

    private Task<HttpResponseMessage> CreditAsync(HttpClient client, string uid, Guid operationId, int amount, string? reason = "Recharge guichet", string? token = null)
        => client.SendAsync(Authed(HttpMethod.Post, $"/api/cards/{uid}/credits", token ?? JanusApiFactory.AdminToken,
            new { operationId, amountMillimes = amount, reason }));

    private Task<HttpResponseMessage> ValidateAsync(HttpClient client, string uid)
        => client.SendAsync(Authed(HttpMethod.Post, "/api/validations", JanusApiFactory.TerminalToken,
            new { eventId = Guid.NewGuid(), cardUid = uid, occurredAt = DateTimeOffset.UtcNow, eventType = "TRANSPORT_VALIDATION" }));

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private async Task AssertLedgerBalancedAsync(string uid)
    {
        using var db = _factory.CreateDbContext();
        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid);
        var sum = await db.WalletLedgerEntries.Where(e => e.CardId == card.Id).SumAsync(e => (long)e.AmountMillimes);
        Assert.Equal(card.BalanceMillimes, sum);
    }

    [Fact]
    public async Task LED01_Credit_AppliesDelta_WritesLedgerAndAudit_AndStaysBalanced()
    {
        var uid = await CreateCardViaApiAsync(3000);
        var operationId = Guid.NewGuid();

        using var resp = await CreditAsync(_client, uid, operationId, 5000);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonAsync(resp);
        Assert.Equal(8000, body.GetProperty("balanceAfterMillimes").GetInt32());
        Assert.False(body.GetProperty("duplicate").GetBoolean());

        using var db = _factory.CreateDbContext();
        var entries = await db.WalletLedgerEntries.AsNoTracking().Where(e => e.CardUid == uid).OrderBy(e => e.Id).ToListAsync();
        Assert.Collection(entries,
            e => { Assert.Equal(LedgerEntryTypes.OpeningBalance, e.EntryType); Assert.Equal(3000, e.AmountMillimes); },
            e => { Assert.Equal(LedgerEntryTypes.AdminCredit, e.EntryType); Assert.Equal(5000, e.AmountMillimes); Assert.Equal(8000, e.BalanceAfterMillimes); Assert.Equal("ADMIN", e.Actor); });
        Assert.True(await db.CardAuditLogs.AnyAsync(a => a.CardUid == uid && a.Action == "ADMIN_CARD_CREDITED" && a.PreviousBalanceMillimes == 3000 && a.NewBalanceMillimes == 8000));
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED02_Credit_ExactReplay_IsIdempotent_SingleMovement()
    {
        var uid = await CreateCardViaApiAsync(0);
        var operationId = Guid.NewGuid();

        using var first = await CreditAsync(_client, uid, operationId, 2000);
        using var replay = await CreditAsync(_client, uid, operationId, 2000);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayBody = await JsonAsync(replay);
        Assert.True(replayBody.GetProperty("duplicate").GetBoolean());
        Assert.Equal(2000, replayBody.GetProperty("balanceAfterMillimes").GetInt32());

        using var db = _factory.CreateDbContext();
        Assert.Equal(2000, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid)).BalanceMillimes);
        Assert.Equal(1, await db.WalletLedgerEntries.CountAsync(e => e.CardUid == uid && e.EntryType == LedgerEntryTypes.AdminCredit));
    }

    [Fact]
    public async Task LED03_Credit_SameOperationId_DifferentPayload_Returns409()
    {
        var uidA = await CreateCardViaApiAsync(0);
        var uidB = await CreateCardViaApiAsync(0);
        var operationId = Guid.NewGuid();

        using var first = await CreditAsync(_client, uidA, operationId, 2000);
        using var otherAmount = await CreditAsync(_client, uidA, operationId, 9000);
        using var otherCard = await CreditAsync(_client, uidB, operationId, 2000);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherAmount.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, otherCard.StatusCode);
        Assert.Equal("idempotency_conflict", (await JsonAsync(otherAmount)).GetProperty("error").GetString());

        using var db = _factory.CreateDbContext();
        Assert.Equal(2000, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uidA)).BalanceMillimes);
        Assert.Equal(0, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uidB)).BalanceMillimes);
    }

    [Fact]
    public async Task LED04_Credit_ConcurrentIdenticalRetries_CreditedExactlyOnce()
    {
        var uid = await CreateCardViaApiAsync(1000);
        var operationId = Guid.NewGuid();

        var tasks = Enumerable.Range(0, 8).Select(async _ =>
        {
            using var client = _factory.CreateClient();
            using var resp = await CreditAsync(client, uid, operationId, 4000);
            return resp.StatusCode;
        }).ToArray();
        var statuses = await Task.WhenAll(tasks);

        Assert.All(statuses, s => Assert.Equal(HttpStatusCode.OK, s));
        using var db = _factory.CreateDbContext();
        Assert.Equal(5000, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid)).BalanceMillimes);
        Assert.Equal(1, await db.WalletLedgerEntries.CountAsync(e => e.CardUid == uid && e.EntryType == LedgerEntryTypes.AdminCredit));
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED05_Credit_ConcurrentWithDebits_NoLostUpdate_LedgerReconciles()
    {
        var uid = await CreateCardViaApiAsync(6000);

        var debitTasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            using var client = _factory.CreateClient();
            using var resp = await ValidateAsync(client, uid);
            Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
            return (await JsonAsync(resp)).GetProperty("decision").GetString();
        }).ToArray();
        var creditTask = Task.Run(async () =>
        {
            using var client = _factory.CreateClient();
            using var resp = await CreditAsync(client, uid, Guid.NewGuid(), 3000);
            return resp.StatusCode;
        });

        var decisions = await Task.WhenAll(debitTasks);
        Assert.Equal(HttpStatusCode.OK, await creditTask);

        var accepted = decisions.Count(d => d == "ACCEPTED");
        using var db = _factory.CreateDbContext();
        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid);
        // Le crédit n'écrase jamais les débits : solde final = initial + crédit - débits acceptés
        Assert.Equal(6000 + 3000 - accepted * Fare, card.BalanceMillimes);
        Assert.Equal(accepted, await db.WalletLedgerEntries.CountAsync(e => e.CardUid == uid && e.EntryType == LedgerEntryTypes.FareDebit));
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED06_Upsert_WithStaleExpectedBalance_Returns409_AndDoesNotOverwriteDebit()
    {
        var uid = await CreateCardViaApiAsync(10000);
        // L'admin lit 10000, puis un terminal débite pendant que la fenêtre d'édition est ouverte
        using (var debit = await ValidateAsync(_client, uid))
        {
            Assert.Equal(HttpStatusCode.OK, debit.StatusCode);
        }

        using var stale = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { balanceMillimes = 10000, status = "Revoked", reason = "Mise en opposition", expectedBalanceMillimes = 10000 }));

        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        var body = await JsonAsync(stale);
        Assert.Equal("stale_balance", body.GetProperty("error").GetString());
        Assert.Equal(10000 - Fare, body.GetProperty("currentBalanceMillimes").GetInt32());

        using (var db = _factory.CreateDbContext())
        {
            var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid);
            Assert.Equal(10000 - Fare, card.BalanceMillimes);
            Assert.Equal(CardStatus.Active, card.Status);
        }

        using var fresh = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { balanceMillimes = 10000 - Fare, status = "Revoked", reason = "Mise en opposition", expectedBalanceMillimes = 10000 - Fare }));
        Assert.Equal(HttpStatusCode.OK, fresh.StatusCode);
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED07_Upsert_BalanceChange_IsRecordedAsAdjustment()
    {
        var uid = await CreateCardViaApiAsync(4000);

        using var resp = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { balanceMillimes = 2500, reason = "Correction erreur de saisie", expectedBalanceMillimes = 4000 }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var db = _factory.CreateDbContext();
        var adjustment = await db.WalletLedgerEntries.AsNoTracking().SingleAsync(e => e.CardUid == uid && e.EntryType == LedgerEntryTypes.AdminAdjustment);
        Assert.Equal(-1500, adjustment.AmountMillimes);
        Assert.Equal(2500, adjustment.BalanceAfterMillimes);
        Assert.Equal("Correction erreur de saisie", adjustment.Reason);
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED13_Upsert_AbsoluteBalanceChange_WithoutGuard_Returns428_AndLeavesCardUntouched()
    {
        var uid = await CreateCardViaApiAsync(4000);

        using var resp = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { balanceMillimes = 9000, reason = "Correction sans garde" }));

        Assert.Equal((HttpStatusCode)428, resp.StatusCode);
        var body = await JsonAsync(resp);
        Assert.Equal("expected_balance_required", body.GetProperty("error").GetString());
        Assert.Equal(4000, body.GetProperty("currentBalanceMillimes").GetInt32());

        using var db = _factory.CreateDbContext();
        Assert.Equal(4000, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid)).BalanceMillimes);
        Assert.Equal(1, await db.CardAuditLogs.CountAsync(a => a.CardUid == uid));
        Assert.Equal(1, await db.WalletLedgerEntries.CountAsync(e => e.CardUid == uid));
    }

    [Fact]
    public async Task LED14_Upsert_MetadataOnly_WithoutBalance_DoesNotRequireGuard_AndKeepsBalance()
    {
        var uid = await CreateCardViaApiAsync(4000);
        // Un débit change le solde : une mise à jour de statut sans solde ne doit ni échouer ni écraser ce débit
        using (var debit = await ValidateAsync(_client, uid)) Assert.Equal(HttpStatusCode.OK, debit.StatusCode);

        using var resp = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { status = "Revoked", label = "Carte perdue", reason = "Déclaration de perte" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var db = _factory.CreateDbContext();
        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid);
        Assert.Equal(4000 - Fare, card.BalanceMillimes);
        Assert.Equal(CardStatus.Revoked, card.Status);
        Assert.Equal("Carte perdue", card.Label);
        Assert.Equal(0, await db.WalletLedgerEntries.CountAsync(e => e.CardUid == uid && e.EntryType == LedgerEntryTypes.AdminAdjustment));
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED15_Upsert_NewCardWithoutBalance_OpensAtZero()
    {
        var uid = NewUid();

        using var resp = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken,
            new { label = "Carte vierge", reason = "Émission" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var db = _factory.CreateDbContext();
        Assert.Equal(0, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid)).BalanceMillimes);
        Assert.False(await db.WalletLedgerEntries.AnyAsync(e => e.CardUid == uid));
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED08_ValidationDebit_IsRecordedInLedger()
    {
        var uid = await CreateCardViaApiAsync(5000);

        using var resp = await ValidateAsync(_client, uid);

        Assert.Equal("ACCEPTED", (await JsonAsync(resp)).GetProperty("decision").GetString());
        using var db = _factory.CreateDbContext();
        var debit = await db.WalletLedgerEntries.AsNoTracking().SingleAsync(e => e.CardUid == uid && e.EntryType == LedgerEntryTypes.FareDebit);
        Assert.Equal(-Fare, debit.AmountMillimes);
        Assert.Equal(5000 - Fare, debit.BalanceAfterMillimes);
        Assert.Equal("TERMINAL-DEMO-01", debit.Actor);
        await AssertLedgerBalancedAsync(uid);
    }

    [Fact]
    public async Task LED09_Credit_BusinessRules_RevokedUnknownAndInvalidInputs()
    {
        var revokedUid = await CreateCardViaApiAsync(1000, status: "Revoked");

        using var revoked = await CreditAsync(_client, revokedUid, Guid.NewGuid(), 1000);
        using var unknown = await CreditAsync(_client, NewUid(), Guid.NewGuid(), 1000);
        using var zero = await CreditAsync(_client, revokedUid, Guid.NewGuid(), 0);
        using var negative = await CreditAsync(_client, revokedUid, Guid.NewGuid(), -500);
        using var aboveMax = await CreditAsync(_client, revokedUid, Guid.NewGuid(), 1_000_001);
        using var noReason = await CreditAsync(_client, revokedUid, Guid.NewGuid(), 1000, reason: "  ");
        using var emptyOperation = await CreditAsync(_client, revokedUid, Guid.Empty, 1000);

        Assert.Equal(HttpStatusCode.Conflict, revoked.StatusCode);
        Assert.Equal("card_revoked", (await JsonAsync(revoked)).GetProperty("error").GetString());
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("invalid_amount", (await JsonAsync(zero)).GetProperty("error").GetString());
        Assert.Equal("invalid_amount", (await JsonAsync(negative)).GetProperty("error").GetString());
        Assert.Equal("invalid_amount", (await JsonAsync(aboveMax)).GetProperty("error").GetString());
        Assert.Equal("invalid_reason", (await JsonAsync(noReason)).GetProperty("error").GetString());
        Assert.Equal("invalid_operation_id", (await JsonAsync(emptyOperation)).GetProperty("error").GetString());

        using var db = _factory.CreateDbContext();
        Assert.Equal(1000, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == revokedUid)).BalanceMillimes);
    }

    [Fact]
    public async Task LED10_TerminalCannotCreditOrReadLedger_AnonymousRejected()
    {
        var uid = await CreateCardViaApiAsync(1000);

        using var terminalCredit = await CreditAsync(_client, uid, Guid.NewGuid(), 1000, token: JanusApiFactory.TerminalToken);
        using var terminalLedger = await _client.SendAsync(Authed(HttpMethod.Get, $"/api/cards/{uid}/ledger", JanusApiFactory.TerminalToken));
        using var anonymousLedger = await _client.GetAsync($"/api/cards/{uid}/ledger");

        Assert.Equal(HttpStatusCode.Forbidden, terminalCredit.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, terminalLedger.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousLedger.StatusCode);
    }

    [Fact]
    public async Task LED11_LedgerEndpoint_ReturnsPagedEntries_AndReconciliationFlag()
    {
        var uid = await CreateCardViaApiAsync(5000);
        using (var c = await CreditAsync(_client, uid, Guid.NewGuid(), 1000)) Assert.Equal(HttpStatusCode.OK, c.StatusCode);
        using (var v = await ValidateAsync(_client, uid)) Assert.Equal(HttpStatusCode.OK, v.StatusCode);

        using var resp = await _client.SendAsync(Authed(HttpMethod.Get, $"/api/cards/{uid}/ledger?page=1&pageSize=2", JanusApiFactory.AdminToken));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonAsync(resp);
        Assert.True(body.GetProperty("isBalanced").GetBoolean());
        Assert.Equal(5000 + 1000 - Fare, body.GetProperty("balanceMillimes").GetInt32());
        Assert.Equal(3, body.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, body.GetProperty("totalPages").GetInt32());
        var items = body.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(LedgerEntryTypes.FareDebit, items[0].GetProperty("entryType").GetString());
    }

    [Fact]
    public async Task LED12_Ledger_IsAppendOnly_ForRuntimeRole_AndForOwnerRole()
    {
        var uid = await CreateCardViaApiAsync(2000);

        // Rôle runtime : aucun droit UPDATE/DELETE (42501)
        using (var runtimeDb = _factory.CreateDbContext())
        {
            var exUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                runtimeDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"WalletLedgerEntries\" SET \"AmountMillimes\" = 999999 WHERE \"CardUid\" = {uid}"));
            Assert.Equal("42501", exUpdate.SqlState);

            var exDelete = await Assert.ThrowsAsync<PostgresException>(() =>
                runtimeDb.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"WalletLedgerEntries\" WHERE \"CardUid\" = {uid}"));
            Assert.Equal("42501", exDelete.SqlState);
        }

        // Rôle propriétaire (migration) : bloqué par les triggers d'immuabilité (23000)
        await using (var ownerDb = _factory.CreateMigrationDbContext())
        {
            var exOwnerUpdate = await Assert.ThrowsAsync<PostgresException>(() =>
                ownerDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"WalletLedgerEntries\" SET \"AmountMillimes\" = 999999 WHERE \"CardUid\" = {uid}"));
            Assert.Equal("23000", exOwnerUpdate.SqlState);

            var exOwnerDelete = await Assert.ThrowsAsync<PostgresException>(() =>
                ownerDb.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"WalletLedgerEntries\" WHERE \"CardUid\" = {uid}"));
            Assert.Equal("23000", exOwnerDelete.SqlState);
        }

        await AssertLedgerBalancedAsync(uid);
    }
}
