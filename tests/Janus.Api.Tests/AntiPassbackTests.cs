using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Janus.Api.Tests;

public class AntiPassbackTests : IAsyncLifetime
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

    private static readonly string[] ExpectedConcurrentDecisions = ["ACCEPTED", "REJECTED"];
    private static readonly string[] ExpectedConcurrentReasons = ["ACTIVE_CARD", "PASSBACK_VIOLATION"];

    private static string TerminalToken => JanusApiFactory.TerminalToken;
    private static string Terminal02Token => JanusApiFactory.Terminal02Token;

    private async Task<string> PrepareCardAsync(int balanceMillimes, CardStatus status = CardStatus.Active)
    {
        var uid = "04TEST" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var db = _factory.CreateDbContext();
        var card = new TransitCard
        {
            Uid = uid,
            Status = status,
            Label = $"Test Card {uid}",
            BalanceMillimes = balanceMillimes
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return uid;
    }

    private static HttpRequestMessage CreateValidationRequest(string cardUid, Guid? eventId = null, DateTimeOffset? occurredAt = null, string? token = null)
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

    // --- 1. Première validation d'une carte active : acceptée, débit normal ---
    [Fact]
    public async Task Test01_ActiveCard_FirstTap_AcceptedAndDebited()
    {
        var uid = await PrepareCardAsync(5000);
        var resp = await _client.SendAsync(CreateValidationRequest(uid));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCEPTED", body.GetProperty("decision").GetString());
        Assert.Equal("ACTIVE_CARD", body.GetProperty("reason").GetString());
        Assert.Equal(1500, body.GetProperty("amountDebited").GetInt32());
        Assert.Equal(3500, body.GetProperty("balanceMillimes").GetInt32());
    }

    // --- 2. Nouvelle validation avec un nouvel événement sur le même terminal pendant la fenêtre : refus PASSBACK_VIOLATION ---
    [Fact]
    public async Task Test02_SameTerminal_WithinWindow_RejectedPassbackViolation()
    {
        var uid = await PrepareCardAsync(5000);

        // Premier passage à T
        var resp1 = await _client.SendAsync(CreateValidationRequest(uid));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Avance le temps de 10 secondes (dans la fenêtre de 30s)
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(10));

        // Deuxième passage avec un nouvel EventId
        var resp2 = await _client.SendAsync(CreateValidationRequest(uid));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("REJECTED", body2.GetProperty("decision").GetString());
        Assert.Equal("PASSBACK_VIOLATION", body2.GetProperty("reason").GetString());
        Assert.Equal(0, body2.GetProperty("amountDebited").GetInt32());
        Assert.Equal(3500, body2.GetProperty("balanceMillimes").GetInt32());
    }

    // --- 3. Nouvelle validation après expiration de la fenêtre : traitement normal ---
    [Fact]
    public async Task Test03_SameTerminal_AfterWindowExpiry_AcceptedAndDebited()
    {
        var uid = await PrepareCardAsync(5000);

        // Premier passage à T
        var resp1 = await _client.SendAsync(CreateValidationRequest(uid));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Avance le temps de 31 secondes (fenêtre de 30s expirée !)
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(31));

        // Deuxième passage avec un nouvel EventId
        var resp2 = await _client.SendAsync(CreateValidationRequest(uid));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());
        Assert.Equal("ACTIVE_CARD", body2.GetProperty("reason").GetString());
        Assert.Equal(1500, body2.GetProperty("amountDebited").GetInt32());
        Assert.Equal(2000, body2.GetProperty("balanceMillimes").GetInt32());
    }

    // --- 4. Validation sur un autre terminal : pas de blocage par la règle locale ---
    [Fact]
    public async Task Test04_DifferentTerminal_WithinWindow_Accepted()
    {
        var uid = await PrepareCardAsync(5000);

        // Premier passage sur TERMINAL-DEMO-01
        var resp1 = await _client.SendAsync(CreateValidationRequest(uid, token: TerminalToken));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Passage immédiat sur TERMINAL-DEMO-02 avec son jeton dédié
        var resp2 = await _client.SendAsync(CreateValidationRequest(uid, token: Terminal02Token));
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("TERMINAL-DEMO-02", body2.GetProperty("terminalId").GetString());
        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());
        Assert.Equal("ACTIVE_CARD", body2.GetProperty("reason").GetString());
        Assert.Equal(1500, body2.GetProperty("amountDebited").GetInt32());
        Assert.Equal(2000, body2.GetProperty("balanceMillimes").GetInt32());
    }

    // --- 5. Carte révoquée : comportement existant préservé ---
    [Fact]
    public async Task Test05_RevokedCard_RejectedCardRevoked_NoPassbackViolation()
    {
        var uid = await PrepareCardAsync(5000, status: CardStatus.Revoked);
        var resp = await _client.SendAsync(CreateValidationRequest(uid));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REJECTED", body.GetProperty("decision").GetString());
        Assert.Equal("CARD_REVOKED", body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("amountDebited").GetInt32());
    }

    // --- 6. Solde insuffisant : comportement existant préservé ---
    [Fact]
    public async Task Test06_InsufficientFunds_RejectedInsufficientFunds_NoPassbackViolation()
    {
        var uid = await PrepareCardAsync(500); // 500 < 1500
        var resp = await _client.SendAsync(CreateValidationRequest(uid));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REJECTED", body.GetProperty("decision").GetString());
        Assert.Equal("INSUFFICIENT_FUNDS", body.GetProperty("reason").GetString());
        Assert.Equal(0, body.GetProperty("amountDebited").GetInt32());
    }

    // --- 7. Tentative refusée : ne démarre pas une nouvelle fenêtre d'anti-passback ---
    [Fact]
    public async Task Test07_FailedAttempt_DoesNotStartAntiPassbackWindow()
    {
        var uid = await PrepareCardAsync(500); // Solde insuffisant

        // Tentative refusée à T
        var resp1 = await _client.SendAsync(CreateValidationRequest(uid));
        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("INSUFFICIENT_FUNDS", body1.GetProperty("reason").GetString());

        // Recharge administrative de la carte à T + 2s
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(2));
        using (var db = _factory.CreateDbContext())
        {
            var card = await db.Cards.SingleAsync(c => c.Uid == uid);
            card.BalanceMillimes = 5000;
            await db.SaveChangesAsync();
        }

        // Nouvelle tentative à T + 4s : doit être ACCEPTÉE car la précédente était rejetée
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(2));
        var resp2 = await _client.SendAsync(CreateValidationRequest(uid));
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());
        Assert.Equal("ACTIVE_CARD", body2.GetProperty("reason").GetString());
        Assert.Equal(1500, body2.GetProperty("amountDebited").GetInt32());
    }

    // --- 8. Configuration désactivée : comportement précédent préservé ---
    [Fact]
    public async Task Test08_AntiPassbackDisabled_SuccessiveTapsAccepted()
    {
        await using var disabledFactory = new JanusApiFactory { AntiPassbackEnabledOverride = false };
        using var client = disabledFactory.CreateClient();

        var uid = "04DIS" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using (var db = disabledFactory.CreateDbContext())
        {
            db.Cards.Add(new TransitCard { Uid = uid, Status = CardStatus.Active, Label = "Disabled APB", BalanceMillimes = 10000 });
            await db.SaveChangesAsync();
        }

        // Premier passage
        var resp1 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(new { eventId = Guid.NewGuid(), cardUid = uid, occurredAt = DateTimeOffset.UtcNow, eventType = "TRANSPORT_VALIDATION" }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", JanusApiFactory.TerminalToken) }
        });
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Deuxième passage immédiat : doit être accepté car APB est désactivé
        var resp2 = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(new { eventId = Guid.NewGuid(), cardUid = uid, occurredAt = DateTimeOffset.UtcNow, eventType = "TRANSPORT_VALIDATION" }),
            Headers = { Authorization = new AuthenticationHeaderValue("Bearer", JanusApiFactory.TerminalToken) }
        });
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());
        Assert.Equal(1500, body2.GetProperty("amountDebited").GetInt32());
        Assert.Equal(7000, body2.GetProperty("balanceMillimes").GetInt32());
    }

    // --- 9. Même événement et même payload : pas de second débit (Idempotence) ---
    [Fact]
    public async Task Test09_SameEvent_ExactReplay_SingleDebit()
    {
        var uid = await PrepareCardAsync(5000);
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        var resp1 = await _client.SendAsync(CreateValidationRequest(uid, eventId, occurredAt));
        var resp2 = await _client.SendAsync(CreateValidationRequest(uid, eventId, occurredAt));

        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body2.GetProperty("duplicate").GetBoolean());

        using var db = _factory.CreateDbContext();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(3500, card.BalanceMillimes); // 1 seul débit
    }

    // --- 10. Même événement avec payload modifié : conflit 409 ---
    [Fact]
    public async Task Test10_SameEvent_ChangedPayload_Conflict409()
    {
        var uid1 = await PrepareCardAsync(5000);
        var uid2 = await PrepareCardAsync(5000);
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        var resp1 = await _client.SendAsync(CreateValidationRequest(uid1, eventId, occurredAt));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var resp2 = await _client.SendAsync(CreateValidationRequest(uid2, eventId, occurredAt));
        Assert.Equal(HttpStatusCode.Conflict, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("idempotency_conflict", body2.GetProperty("error").GetString());
    }

    // --- 11. Rejeu d'un événement initialement accepté : l'idempotence prévaut sur l'anti-passback ---
    [Fact]
    public async Task Test11_Idempotence_PrevailsOverAntiPassback()
    {
        var uid = await PrepareCardAsync(5000);
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        // Passage à T
        var resp1 = await _client.SendAsync(CreateValidationRequest(uid, eventId, occurredAt));
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        // Rejeu exact à T + 5s (dans la fenêtre de passback)
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(5));
        var resp2 = await _client.SendAsync(CreateValidationRequest(uid, eventId, occurredAt));

        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();

        // Doit renvoyer le résultat initial (ACCEPTED / duplicate=true), et NON PASSBACK_VIOLATION
        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());
        Assert.True(body2.GetProperty("duplicate").GetBoolean());
    }

    // --- 12. Concurrence : deux événements différents envoyés simultanément pour la même carte ---
    [Fact]
    public async Task Test12_Concurrency_TwoDistinctEventsSimultaneously_OnlyOneAccepted()
    {
        var uid = await PrepareCardAsync(10000);

        var event1 = Guid.NewGuid();
        var event2 = Guid.NewGuid();

        // Envoi parallèle simultané sous PostgreSQL
        var task1 = Task.Run(() => _client.SendAsync(CreateValidationRequest(uid, event1)));
        var task2 = Task.Run(() => _client.SendAsync(CreateValidationRequest(uid, event2)));

        var responses = await Task.WhenAll(task1, task2);
        var bodies = new List<JsonElement>();
        foreach (var r in responses)
        {
            Assert.Equal(HttpStatusCode.OK, r.StatusCode);
            bodies.Add(await r.Content.ReadFromJsonAsync<JsonElement>());
        }

        var decisions = bodies.Select(b => b.GetProperty("decision").GetString()).OrderBy(d => d).ToList();
        var reasons = bodies.Select(b => b.GetProperty("reason").GetString()).OrderBy(r => r).ToList();

        // Exactement 1 ACCEPTED et 1 REJECTED (PASSBACK_VIOLATION)
        Assert.Equal(ExpectedConcurrentDecisions, decisions);
        Assert.Equal(ExpectedConcurrentReasons, reasons);

        // Vérification directe des invariants en base
        using var db = _factory.CreateDbContext();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(8500, card.BalanceMillimes); // 10000 - 1500 = 8500 (un seul débit !)

        var txCount = await db.FareTransactions.CountAsync(t => t.CardUid == uid);
        Assert.Equal(1, txCount); // Exactement 1 transaction financière créée
    }

    // --- 13. Concurrence : deux requêtes concurrentes portant le même événement ---
    [Fact]
    public async Task Test13_Concurrency_TwoIdenticalEvents_SingleDebit()
    {
        var uid = await PrepareCardAsync(10000);
        var eventId = Guid.NewGuid();
        var occurredAt = DateTimeOffset.UtcNow;

        var task1 = Task.Run(() => _client.SendAsync(CreateValidationRequest(uid, eventId, occurredAt)));
        var task2 = Task.Run(() => _client.SendAsync(CreateValidationRequest(uid, eventId, occurredAt)));

        var responses = await Task.WhenAll(task1, task2);
        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));

        using var db = _factory.CreateDbContext();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(8500, card.BalanceMillimes);

        var txCount = await db.FareTransactions.CountAsync(t => t.CardUid == uid);
        Assert.Equal(1, txCount);
    }

    // --- 14. Vérification formelle des invariants directement en base PostgreSQL ---
    [Fact]
    public async Task Test14_DatabaseInvariantsVerifiedDirectly()
    {
        var uid = await PrepareCardAsync(10000);

        // 1. Passage valide
        await _client.SendAsync(CreateValidationRequest(uid));

        // 2. Passages passback (rejetés)
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(5));
        await _client.SendAsync(CreateValidationRequest(uid));
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(5));
        await _client.SendAsync(CreateValidationRequest(uid));

        // 3. Passage après expiration
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(35));
        await _client.SendAsync(CreateValidationRequest(uid));

        using var db = _factory.CreateDbContext();
        var card = await db.Cards.SingleAsync(c => c.Uid == uid);

        // 2 débits de 1500 appliqués au total
        Assert.Equal(7000, card.BalanceMillimes);

        var totalValidations = await db.Validations.CountAsync(v => v.CardUid == uid);
        var acceptedValidations = await db.Validations.CountAsync(v => v.CardUid == uid && v.Decision == ValidationDecision.Accepted);
        var rejectedValidations = await db.Validations.CountAsync(v => v.CardUid == uid && v.Decision == ValidationDecision.Rejected);
        var passbackRejections = await db.Validations.CountAsync(v => v.CardUid == uid && v.Reason == "PASSBACK_VIOLATION");
        var financialTransactions = await db.FareTransactions.CountAsync(t => t.CardUid == uid);

        Assert.Equal(4, totalValidations);
        Assert.Equal(2, acceptedValidations);
        Assert.Equal(2, rejectedValidations);
        Assert.Equal(2, passbackRejections);
        Assert.Equal(2, financialTransactions);
    }
}
