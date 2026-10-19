using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Janus.Api.Tests;

public class PaginationTests : IAsyncLifetime
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
    private static string AdminToken => JanusApiFactory.AdminToken;

    private async Task<List<string>> PrepareBatchCardsAsync(int count, string prefix, CardStatus status = CardStatus.Active)
    {
        var uids = new List<string>();
        using var db = _factory.CreateDbContext();
        for (int i = 1; i <= count; i++)
        {
            var uid = $"{prefix}{i:D4}";
            uids.Add(uid);
            db.Cards.Add(new TransitCard
            {
                Uid = uid,
                Label = $"Batch User {prefix} {i}",
                Status = status,
                BalanceMillimes = i * 1000
            });
        }
        await db.SaveChangesAsync();
        return uids;
    }

    private async Task<string> PrepareCardWithAuditsAsync(string prefix, int auditCount)
    {
        var uid = $"{prefix}AUD001";
        using var db = _factory.CreateDbContext();
        var card = new TransitCard
        {
            Uid = uid,
            Label = $"Card for Audit {prefix}",
            Status = CardStatus.Active,
            BalanceMillimes = 10000
        };
        db.Cards.Add(card);

        var baseTime = DateTimeOffset.UtcNow.AddMinutes(-auditCount * 5);
        for (int i = 1; i <= auditCount; i++)
        {
            db.CardAuditLogs.Add(new CardAuditLog
            {
                CardUid = uid,
                Action = i == 1 ? "ADMIN_CARD_CREATED" : "ADMIN_CARD_UPDATED",
                PreviousBalanceMillimes = i == 1 ? null : (i - 1) * 1000,
                NewBalanceMillimes = i * 1000,
                PreviousStatus = i == 1 ? null : "Active",
                NewStatus = "Active",
                Reason = $"Audit event {i}",
                Actor = "ADMIN",
                CreatedAt = baseTime.AddMinutes(i * 5)
            });
        }
        await db.SaveChangesAsync();
        return uid;
    }

    [Fact]
    public async Task PAG01_Cards_First_Middle_And_Last_Page_Navigation()
    {
        var prefix = "04PAG" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await PrepareBatchCardsAsync(25, prefix);

        // 1. Première page (page=1, pageSize=10)
        using var req1 = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?page=1&pageSize=10&q={prefix}");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var resp1 = await _client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);

        var doc1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, doc1.GetProperty("page").GetInt32());
        Assert.Equal(10, doc1.GetProperty("pageSize").GetInt32());
        Assert.Equal(25, doc1.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, doc1.GetProperty("totalPages").GetInt32());
        var items1 = doc1.GetProperty("items");
        Assert.Equal(10, items1.GetArrayLength());
        Assert.Equal($"{prefix}0001", items1[0].GetProperty("uid").GetString());
        Assert.Equal(1000, items1[0].GetProperty("balanceMillimes").GetInt32());

        // 2. Page intermédiaire (page=2, pageSize=10)
        using var req2 = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?page=2&pageSize=10&q={prefix}");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var resp2 = await _client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);

        var doc2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, doc2.GetProperty("page").GetInt32());
        var items2 = doc2.GetProperty("items");
        Assert.Equal(10, items2.GetArrayLength());
        Assert.Equal($"{prefix}0011", items2[0].GetProperty("uid").GetString());

        // 3. Dernière page (page=3, pageSize=10)
        using var req3 = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?page=3&pageSize=10&q={prefix}");
        req3.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var resp3 = await _client.SendAsync(req3);
        Assert.Equal(HttpStatusCode.OK, resp3.StatusCode);

        var doc3 = await resp3.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, doc3.GetProperty("page").GetInt32());
        var items3 = doc3.GetProperty("items");
        Assert.Equal(5, items3.GetArrayLength());
        Assert.Equal($"{prefix}0021", items3[0].GetProperty("uid").GetString());
        Assert.Equal($"{prefix}0025", items3[4].GetProperty("uid").GetString());
    }

    [Fact]
    public async Task PAG02_Cards_Empty_Result_And_Out_Of_Range_Page()
    {
        // 1. Jeu de résultat vide
        using var reqEmpty = new HttpRequestMessage(HttpMethod.Get, "/api/cards?q=NON_EXISTENT_UID_99999");
        reqEmpty.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respEmpty = await _client.SendAsync(reqEmpty);
        Assert.Equal(HttpStatusCode.OK, respEmpty.StatusCode);

        var docEmpty = await respEmpty.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(0, docEmpty.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, docEmpty.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, docEmpty.GetProperty("items").GetArrayLength());

        // 2. Page au-delà de la dernière page existante
        var prefix = "04EMP" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await PrepareBatchCardsAsync(5, prefix);

        using var reqOutOfRange = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?page=999&pageSize=10&q={prefix}");
        reqOutOfRange.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respOutOfRange = await _client.SendAsync(reqOutOfRange);
        Assert.Equal(HttpStatusCode.OK, respOutOfRange.StatusCode);

        var docOutOfRange = await respOutOfRange.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(5, docOutOfRange.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, docOutOfRange.GetProperty("totalPages").GetInt32());
        Assert.Equal(0, docOutOfRange.GetProperty("items").GetArrayLength());
    }

    [Fact]
    public async Task PAG03_Cards_Filtering_By_Status_And_Text_Query()
    {
        var prefix = "04FLT" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await PrepareBatchCardsAsync(4, prefix + "A", CardStatus.Active);
        await PrepareBatchCardsAsync(3, prefix + "R", CardStatus.Revoked);

        // 1. Filtre Active uniquement
        using var reqActive = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?q={prefix}&status=Active");
        reqActive.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respActive = await _client.SendAsync(reqActive);
        Assert.Equal(HttpStatusCode.OK, respActive.StatusCode);
        var docActive = await respActive.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(4, docActive.GetProperty("totalCount").GetInt32());
        foreach (var item in docActive.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Active", item.GetProperty("status").GetString());
        }

        // 2. Filtre Revoked uniquement
        using var reqRevoked = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?q={prefix}&status=Revoked");
        reqRevoked.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respRevoked = await _client.SendAsync(reqRevoked);
        Assert.Equal(HttpStatusCode.OK, respRevoked.StatusCode);
        var docRevoked = await respRevoked.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(3, docRevoked.GetProperty("totalCount").GetInt32());
        foreach (var item in docRevoked.GetProperty("items").EnumerateArray())
        {
            Assert.Equal("Revoked", item.GetProperty("status").GetString());
        }

        // 3. Statut invalide -> 400 Bad Request
        using var reqInvalidStatus = new HttpRequestMessage(HttpMethod.Get, "/api/cards?status=INVALID_STATUS");
        reqInvalidStatus.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respInvalidStatus = await _client.SendAsync(reqInvalidStatus);
        Assert.Equal(HttpStatusCode.BadRequest, respInvalidStatus.StatusCode);
    }

    [Fact]
    public async Task PAG04_Cards_Invalid_Parameters_Normalized_And_MaxPageSize_Enforced()
    {
        var prefix = "04NRM" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        await PrepareBatchCardsAsync(5, prefix);

        // Page négative et pageSize négatif normalisés
        using var reqNorm = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?page=-10&pageSize=-5&q={prefix}");
        reqNorm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respNorm = await _client.SendAsync(reqNorm);
        Assert.Equal(HttpStatusCode.OK, respNorm.StatusCode);
        var docNorm = await respNorm.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(1, docNorm.GetProperty("page").GetInt32());
        Assert.Equal(1, docNorm.GetProperty("pageSize").GetInt32()); // clampé à 1 minimum

        // PageSize excessif clampé au maximum serveur (100)
        using var reqMax = new HttpRequestMessage(HttpMethod.Get, $"/api/cards?page=1&pageSize=500&q={prefix}");
        reqMax.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respMax = await _client.SendAsync(reqMax);
        Assert.Equal(HttpStatusCode.OK, respMax.StatusCode);
        var docMax = await respMax.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(100, docMax.GetProperty("pageSize").GetInt32());
    }

    [Fact]
    public async Task PAG05_Cards_Security_Isolation_And_RBAC()
    {
        // 1. Anonyme -> 401 Unauthorized
        using var reqAnon = new HttpRequestMessage(HttpMethod.Get, "/api/cards");
        var respAnon = await _client.SendAsync(reqAnon);
        Assert.Equal(HttpStatusCode.Unauthorized, respAnon.StatusCode);

        // 2. Terminal (sans rôle Admin) -> 403 Forbidden (séparation stricte : pas d'exposition administrative)
        using var reqTerm = new HttpRequestMessage(HttpMethod.Get, "/api/cards");
        reqTerm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var respTerm = await _client.SendAsync(reqTerm);
        Assert.Equal(HttpStatusCode.Forbidden, respTerm.StatusCode);

        // 3. Admin -> 200 OK
        using var reqAdmin = new HttpRequestMessage(HttpMethod.Get, "/api/cards");
        reqAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respAdmin = await _client.SendAsync(reqAdmin);
        Assert.Equal(HttpStatusCode.OK, respAdmin.StatusCode);
    }

    [Fact]
    public async Task PAG06_Audit_Server_Pagination_And_Dual_Contract_Compatibility()
    {
        var prefix = "04ADT" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var cardUid = await PrepareCardWithAuditsAsync(prefix, 12);

        // 1. Appel moderne (page=1, pageSize=5)
        using var reqModern = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit?page=1&pageSize=5");
        reqModern.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respModern = await _client.SendAsync(reqModern);
        Assert.Equal(HttpStatusCode.OK, respModern.StatusCode);

        var docModern = await respModern.Content.ReadFromJsonAsync<JsonElement>();
        // Propriétés modernes
        Assert.Equal(1, docModern.GetProperty("page").GetInt32());
        Assert.Equal(5, docModern.GetProperty("pageSize").GetInt32());
        Assert.Equal(12, docModern.GetProperty("totalCount").GetInt32());
        Assert.Equal(3, docModern.GetProperty("totalPages").GetInt32());
        Assert.Equal(5, docModern.GetProperty("items").GetArrayLength());

        // Propriétés historiques rétrocompatibles
        Assert.Equal(cardUid, docModern.GetProperty("cardUid").GetString());
        Assert.Equal(5, docModern.GetProperty("limit").GetInt32());
        Assert.Equal(0, docModern.GetProperty("offset").GetInt32());
        Assert.Equal(5, docModern.GetProperty("auditLogs").GetArrayLength());

        // 2. Appel historique avec limit et offset uniquement (?limit=5&offset=5)
        using var reqLegacy = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit?limit=5&offset=5");
        reqLegacy.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respLegacy = await _client.SendAsync(reqLegacy);
        Assert.Equal(HttpStatusCode.OK, respLegacy.StatusCode);

        var docLegacy = await respLegacy.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(cardUid, docLegacy.GetProperty("cardUid").GetString());
        Assert.Equal(5, docLegacy.GetProperty("limit").GetInt32());
        Assert.Equal(5, docLegacy.GetProperty("offset").GetInt32());
        Assert.Equal(2, docLegacy.GetProperty("page").GetInt32());
        Assert.Equal(5, docLegacy.GetProperty("pageSize").GetInt32());
        Assert.Equal(5, docLegacy.GetProperty("auditLogs").GetArrayLength());

        // 3. Priorité : si paramètres modernes et historiques sont fournis, les modernes prévalent
        using var reqMixed = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit?page=2&pageSize=4&limit=50&offset=0");
        reqMixed.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respMixed = await _client.SendAsync(reqMixed);
        Assert.Equal(HttpStatusCode.OK, respMixed.StatusCode);

        var docMixed = await respMixed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(2, docMixed.GetProperty("page").GetInt32());
        Assert.Equal(4, docMixed.GetProperty("pageSize").GetInt32());
        Assert.Equal(4, docMixed.GetProperty("limit").GetInt32());
        Assert.Equal(4, docMixed.GetProperty("offset").GetInt32());
    }

    [Fact]
    public async Task PAG07_Audit_Deterministic_Ordering_With_Identical_Timestamps()
    {
        var prefix = "04DTM" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var cardUid = $"{prefix}SAME_TIME";
        var sameInstant = new DateTimeOffset(2026, 10, 19, 12, 0, 0, TimeSpan.Zero);

        using var db = _factory.CreateDbContext();
        db.Cards.Add(new TransitCard
        {
            Uid = cardUid,
            Label = "Test Identical Timestamp",
            Status = CardStatus.Active,
            BalanceMillimes = 5000
        });

        // 3 événements à la même seconde exacte
        db.CardAuditLogs.AddRange(
            new CardAuditLog { CardUid = cardUid, Action = "ADMIN_CARD_CREATED", NewBalanceMillimes = 5000, NewStatus = "Active", Actor = "ADMIN", CreatedAt = sameInstant },
            new CardAuditLog { CardUid = cardUid, Action = "ADMIN_CARD_UPDATED", NewBalanceMillimes = 6000, NewStatus = "Active", Actor = "ADMIN", CreatedAt = sameInstant },
            new CardAuditLog { CardUid = cardUid, Action = "ADMIN_CARD_UPDATED", NewBalanceMillimes = 7000, NewStatus = "Active", Actor = "ADMIN", CreatedAt = sameInstant }
        );
        await db.SaveChangesAsync();

        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit?page=1&pageSize=10");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        var items = doc.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(3, items.Count);

        // Tri déterministe par CreatedAt DESC puis Id DESC
        var id0 = items[0].GetProperty("id").GetInt64();
        var id1 = items[1].GetProperty("id").GetInt64();
        var id2 = items[2].GetProperty("id").GetInt64();

        Assert.True(id0 > id1);
        Assert.True(id1 > id2);
        Assert.Equal(7000, items[0].GetProperty("newBalanceMillimes").GetInt32());
    }

    [Fact]
    public async Task PAG08_Audit_Security_Isolation_And_RBAC()
    {
        var prefix = "04RBAC" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
        var cardUid = await PrepareCardWithAuditsAsync(prefix, 1);

        // 1. Anonyme -> 401
        using var reqAnon = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit");
        var respAnon = await _client.SendAsync(reqAnon);
        Assert.Equal(HttpStatusCode.Unauthorized, respAnon.StatusCode);

        // 2. Terminal -> 403 Forbidden
        using var reqTerm = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit");
        reqTerm.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var respTerm = await _client.SendAsync(reqTerm);
        Assert.Equal(HttpStatusCode.Forbidden, respTerm.StatusCode);

        // 3. Admin -> 200 OK
        using var reqAdmin = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit");
        reqAdmin.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var respAdmin = await _client.SendAsync(reqAdmin);
        Assert.Equal(HttpStatusCode.OK, respAdmin.StatusCode);
    }

    [Fact]
    public async Task PAG09_Legacy_ApiDemoCards_Returns_Raw_JSON_Array()
    {
        // Vérifie que GET /api/demo/cards retourne toujours un JsonValueKind.Array
        // afin de garantir la non-régression des tests historiques C# et Python
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/demo/cards");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
    }

    [Fact]
    public async Task PAG10_SimulatorCards_Does_Not_Expose_Financial_Balance()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/simulator/cards");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp = await _client.SendAsync(req);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, doc.ValueKind);
        foreach (var item in doc.EnumerateArray())
        {
            Assert.False(item.TryGetProperty("balanceMillimes", out _));
        }
    }
}
