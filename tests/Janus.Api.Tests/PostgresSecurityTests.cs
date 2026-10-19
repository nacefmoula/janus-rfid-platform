using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Janus.Api.Tests;

public class PostgresSecurityTests : IClassFixture<JanusApiFactory>, IAsyncLifetime
{
    private readonly JanusApiFactory _factory;
    private HttpClient _client = null!;

    public PostgresSecurityTests(JanusApiFactory factory)
    {
        _factory = factory;
    }

    public async Task InitializeAsync()
    {
        _client = _factory.CreateClient();
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private static string AdminToken => JanusApiFactory.AdminToken;
    private static string TerminalToken => JanusApiFactory.TerminalToken;

    [Fact]
    public async Task SEC_LOT2B_01_RuntimeRole_CanPerformLegitimateApplicationOperations()
    {
        // Vérifie que le rôle runtime restreint (janus_runtime) peut accomplir toutes les opérations DML légitimes
        using var db = _factory.CreateDbContext();

        var uid = $"04SEC{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var card = new TransitCard
        {
            Uid = uid,
            Status = CardStatus.Active,
            Label = "Test Runtime Role",
            BalanceMillimes = 5000
        };

        // 1. Insertion TransitCard
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        // 2. Lecture TransitCard
        var fetchedCard = await db.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(5000, fetchedCard.BalanceMillimes);

        // 3. Mise à jour TransitCard
        fetchedCard.BalanceMillimes = 3500;
        fetchedCard.Label = "Updated by Runtime";
        await db.SaveChangesAsync();

        // 4. Insertion ValidationEvent
        var eventId = Guid.NewGuid();
        var valEvent = new ValidationEvent
        {
            TerminalId = "TERM-TEST-01",
            EventId = eventId,
            CardUid = uid,
            PayloadHash = "hash123",
            EventType = "PASSAGE",
            Decision = ValidationDecision.Accepted,
            Reason = "OK",
            ReceivedAt = DateTimeOffset.UtcNow
        };
        db.Validations.Add(valEvent);
        await db.SaveChangesAsync();

        // 5. Insertion FareTransaction
        var tx = new FareTransaction
        {
            CardId = fetchedCard.Id,
            CardUid = uid,
            TerminalId = "TERM-TEST-01",
            EventId = eventId,
            AmountMillimes = 1500,
            BalanceAfterMillimes = 3500,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.FareTransactions.Add(tx);
        await db.SaveChangesAsync();

        // 6. Insertion CardAuditLog
        var audit = new CardAuditLog
        {
            CardUid = uid,
            Action = "TEST_AUDIT",
            PreviousBalanceMillimes = 5000,
            NewBalanceMillimes = 3500,
            PreviousStatus = "Active",
            NewStatus = "Active",
            Reason = "Legitimate Audit Test",
            Actor = "TEST_RUNNER",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CardAuditLogs.Add(audit);
        await db.SaveChangesAsync();

        // 7. Lecture de vérification
        var auditCount = await db.CardAuditLogs.CountAsync(a => a.CardUid == uid);
        Assert.Equal(1, auditCount);
    }

    [Fact]
    public async Task SEC_LOT2B_02_RuntimeRole_CannotExecuteUnauthorizedDDL()
    {
        // Vérifie que le rôle runtime ne dispose d'aucun droit DDL (CREATE, DROP, ALTER, TRUNCATE)
        using var db = _factory.CreateDbContext();

        // 1. Tentative de CREATE TABLE arbitraire
        var createEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("CREATE TABLE \"MaliciousTable\" (id int);");
        });
        Assert.Equal("42501", createEx.SqlState); // insufficient_privilege

        // 2. Tentative de DROP TABLE sur une table existante
        var dropEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("DROP TABLE \"Cards\";");
        });
        Assert.Equal("42501", dropEx.SqlState); // must be owner of table

        // 3. Tentative de ALTER TABLE
        var alterEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE \"Cards\" ADD COLUMN \"MaliciousCol\" text;");
        });
        Assert.Equal("42501", alterEx.SqlState); // must be owner of table

        // 4. Tentative de TRUNCATE TABLE
        var truncateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"Cards\";");
        });
        Assert.Equal("42501", truncateEx.SqlState); // must be owner of table
    }

    [Fact]
    public async Task SEC_LOT2B_03_RuntimeRole_CannotUpdateOrDelete_CardAuditLogs()
    {
        // Vérifie que le rôle runtime ne peut ni modifier ni supprimer des entrées de CardAuditLogs (bloqué par DAC PostgreSQL)
        using var db = _factory.CreateDbContext();

        var uid = $"04AUD{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var audit = new CardAuditLog
        {
            CardUid = uid,
            Action = "INITIAL_LOG",
            NewBalanceMillimes = 1000,
            NewStatus = "Active",
            Actor = "ADMIN",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CardAuditLogs.Add(audit);
        await db.SaveChangesAsync();

        var auditId = audit.Id;

        // 1. Tentative d'UPDATE direct sur CardAuditLogs
        var updateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"CardAuditLogs\" SET \"Reason\" = 'tampered' WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("42501", updateEx.SqlState); // insufficient_privilege (permission denied for table CardAuditLogs)

        // 2. Tentative de DELETE direct sur CardAuditLogs
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CardAuditLogs\" WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("42501", deleteEx.SqlState); // insufficient_privilege

        // 3. Vérification de l'intégrité de la ligne d'audit
        using var verifyDb = _factory.CreateDbContext();
        var untouched = await verifyDb.CardAuditLogs.SingleAsync(a => a.Id == auditId);
        Assert.Null(untouched.Reason);
        Assert.Equal("INITIAL_LOG", untouched.Action);
    }

    [Fact]
    public async Task SEC_LOT2B_04_CardAuditLogs_ImmutabilityTrigger_BlocksTampering_EvenForPrivilegedUser()
    {
        // Vérifie que le trigger de défense en profondeur bloque UPDATE et DELETE même pour un utilisateur disposant des privilèges sur la table
        using var migrationDb = _factory.CreateMigrationDbContext();

        var uid = $"04TRG{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var audit = new CardAuditLog
        {
            CardUid = uid,
            Action = "TRIGGER_TEST",
            NewBalanceMillimes = 2000,
            NewStatus = "Active",
            Actor = "AUDITOR",
            CreatedAt = DateTimeOffset.UtcNow
        };
        migrationDb.CardAuditLogs.Add(audit);
        await migrationDb.SaveChangesAsync();

        var auditId = audit.Id;

        // 1. Tentative d'UPDATE sous le rôle de migration (propriétaire)
        var updateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await migrationDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"CardAuditLogs\" SET \"Reason\" = 'tampered' WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("23000", updateEx.SqlState); // integrity_constraint_violation levé par le trigger
        Assert.Contains("append-only", updateEx.MessageText);

        // 2. Tentative de DELETE sous le rôle de migration
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await migrationDb.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CardAuditLogs\" WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("23000", deleteEx.SqlState); // integrity_constraint_violation levé par le trigger
        Assert.Contains("append-only", deleteEx.MessageText);
    }

    [Fact]
    public async Task SEC_LOT2B_05_RuntimeRole_CannotDeleteFromFinancialTables()
    {
        // Vérifie que le rôle runtime ne peut supprimer aucune ligne de Cards, Validations ou FareTransactions
        using var db = _factory.CreateDbContext();

        var uid = $"04DEL{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var card = new TransitCard { Uid = uid, Status = CardStatus.Active, Label = "Immutable Card", BalanceMillimes = 1000 };
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        // Tentative de DELETE sur Cards
        var deleteCardsEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"Cards\" WHERE \"Uid\" = {uid}");
        });
        Assert.Equal("42501", deleteCardsEx.SqlState);

        // Tentative de DELETE sur Validations
        var deleteValEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"Validations\";");
        });
        Assert.Equal("42501", deleteValEx.SqlState);

        // Tentative de DELETE sur FareTransactions
        var deleteTxEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"FareTransactions\";");
        });
        Assert.Equal("42501", deleteTxEx.SqlState);
    }

    [Fact]
    public async Task SEC_LOT2B_06_FinancialTransactions_PessimisticLock_And_NonNegativeConstraint_PreservedUnderRuntime()
    {
        // Vérifie que les validations financières fonctionnent avec transactions, verrous pessimistes FOR UPDATE et contrainte CK_Cards
        var uid = $"04FIN{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        using (var seedDb = _factory.CreateDbContext())
        {
            seedDb.Cards.Add(new TransitCard { Uid = uid, Status = CardStatus.Active, Label = "Financial Card", BalanceMillimes = 2000 });
            await seedDb.SaveChangesAsync();
        }

        // 1. Débit valide via l'API (1500 millimes)
        var request = new ValidationRequest(Guid.NewGuid(), uid, DateTimeOffset.UtcNow, "TRANSPORT_VALIDATION");
        var httpReq = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(request)
        };
        httpReq.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp = await _client.SendAsync(httpReq);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCEPTED", body.GetProperty("decision").GetString());
        Assert.Equal(500, body.GetProperty("balanceMillimes").GetInt32());

        // 2. Deuxième tentative de débit : après expiration de la fenêtre anti-passback (30s)
        _factory.SimulatedTime.Advance(TimeSpan.FromSeconds(35));
        var request2 = new ValidationRequest(Guid.NewGuid(), uid, DateTimeOffset.UtcNow, "TRANSPORT_VALIDATION");
        var httpReq2 = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(request2)
        };
        httpReq2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp2 = await _client.SendAsync(httpReq2);

        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("REJECTED", body2.GetProperty("decision").GetString());
        Assert.Equal("INSUFFICIENT_FUNDS", body2.GetProperty("reason").GetString());

        // 3. Contrainte SQL de non-négativité rejetée si tentative directe
        using (var db = _factory.CreateDbContext())
        {
            var ckEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Cards\" SET \"BalanceMillimes\" = -100 WHERE \"Uid\" = {uid}");
            });
            Assert.Equal("23514", ckEx.SqlState);
            Assert.Equal("CK_Cards_BalanceMillimes_NonNegative", ckEx.ConstraintName);
        }
    }

    [Fact]
    public async Task SEC_LOT2B_07_Idempotence_PreservedUnderRuntime()
    {
        // Vérifie que l'idempotence (TerminalId, EventId) fonctionne parfaitement sous le rôle runtime
        var uid = $"04IDEM{Guid.NewGuid():N}"[..14].ToUpperInvariant();

        using (var seedDb = _factory.CreateDbContext())
        {
            seedDb.Cards.Add(new TransitCard { Uid = uid, Status = CardStatus.Active, Label = "Idempotence Card", BalanceMillimes = 5000 });
            await seedDb.SaveChangesAsync();
        }

        var fixedEventId = Guid.NewGuid();
        var request = new ValidationRequest(fixedEventId, uid, DateTimeOffset.UtcNow, "TRANSPORT_VALIDATION");

        // Requête initiale
        var req1 = new HttpRequestMessage(HttpMethod.Post, "/api/validations") { Content = JsonContent.Create(request) };
        req1.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp1 = await _client.SendAsync(req1);
        Assert.Equal(HttpStatusCode.OK, resp1.StatusCode);
        var body1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCEPTED", body1.GetProperty("decision").GetString());
        Assert.Equal(3500, body1.GetProperty("balanceMillimes").GetInt32());

        // Rejeu identique
        var req2 = new HttpRequestMessage(HttpMethod.Post, "/api/validations") { Content = JsonContent.Create(request) };
        req2.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp2 = await _client.SendAsync(req2);
        Assert.Equal(HttpStatusCode.OK, resp2.StatusCode);
        var body2 = await resp2.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(body2.GetProperty("duplicate").GetBoolean());
        Assert.Equal("ACCEPTED", body2.GetProperty("decision").GetString());

        // Vérification en base : une seule transaction financière persistée
        using var verifyDb = _factory.CreateDbContext();
        var txCount = await verifyDb.FareTransactions.CountAsync(t => t.EventId == fixedEventId);
        Assert.Equal(1, txCount);
        var card = await verifyDb.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(3500, card.BalanceMillimes);
    }

    [Fact]
    public async Task SEC_LOT2B_08_MigrationRole_CanApplyMigrationsAndAccessHistoryTable()
    {
        // Vérifie que le rôle de migration peut lire et écrire la table __EFMigrationsHistory
        using var migrationDb = _factory.CreateMigrationDbContext();
        var historyRows = await migrationDb.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"").ToListAsync();
        Assert.Contains(historyRows, m => m.Contains("AddCardAuditLogsProtectionTrigger"));

        // Vérifie que le rôle runtime ne peut pas altérer la table __EFMigrationsHistory
        using var runtimeDb = _factory.CreateDbContext();
        var alterEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await runtimeDb.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";");
        });
        Assert.Equal("42501", alterEx.SqlState);
    }
}
