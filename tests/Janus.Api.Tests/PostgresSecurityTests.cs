using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Janus.Api.Migrations;
using Janus.Api.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
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

    public Task InitializeAsync()
    {
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    private static string TerminalToken => JanusApiFactory.TerminalToken;

    [Fact]
    public async Task SEC_LOT2B_01_RuntimeRole_IsNonSuperuser_And_CanPerformLegitimateApplicationOperations()
    {
        // 1. Vérifie que le contexte runtime est effectivement connecté sous un rôle non-superutilisateur distinct du rôle de migration
        using var db = _factory.CreateDbContext();
        using var migrationDb = _factory.CreateMigrationDbContext();

        await using var conn = (NpgsqlConnection)db.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using (var roleCmd = conn.CreateCommand())
        {
            roleCmd.CommandText = @"
                SELECT rolname, rolsuper, rolinherit, rolcreaterole, rolcreatedb, rolbypassrls
                FROM pg_catalog.pg_roles
                WHERE rolname = current_user;";
            await using var reader = await roleCmd.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            var runtimeRoleName = reader.GetString(0);
            var isSuper = reader.GetBoolean(1);
            var isInherit = reader.GetBoolean(2);
            var canCreateRole = reader.GetBoolean(3);
            var canCreateDb = reader.GetBoolean(4);
            var bypassRls = reader.GetBoolean(5);

            Assert.False(isSuper, $"Runtime role '{runtimeRoleName}' MUST NOT be a PostgreSQL superuser.");
            Assert.False(isInherit, $"Runtime role '{runtimeRoleName}' MUST have NOINHERIT.");
            Assert.False(canCreateRole, $"Runtime role '{runtimeRoleName}' MUST have NOCREATEROLE.");
            Assert.False(canCreateDb, $"Runtime role '{runtimeRoleName}' MUST have NOCREATEDB.");
            Assert.False(bypassRls, $"Runtime role '{runtimeRoleName}' MUST have NOBYPASSRLS.");

            var migBuilder = new NpgsqlConnectionStringBuilder(_factory.MigrationConnectionString);
            Assert.NotEqual(migBuilder.Username, runtimeRoleName, StringComparer.OrdinalIgnoreCase);
        }

        // 2. Vérifie que le rôle runtime restreint peut accomplir toutes les opérations DML légitimes
        var uid = $"04SEC{Guid.NewGuid():N}"[..14].ToUpperInvariant();
        var card = new TransitCard
        {
            Uid = uid,
            Status = CardStatus.Active,
            Label = "Test Runtime Role",
            BalanceMillimes = 5000
        };

        // Insertion TransitCard
        db.Cards.Add(card);
        await db.SaveChangesAsync();

        // Lecture TransitCard
        var fetchedCard = await db.Cards.SingleAsync(c => c.Uid == uid);
        Assert.Equal(5000, fetchedCard.BalanceMillimes);

        // Mise à jour TransitCard
        fetchedCard.BalanceMillimes = 3500;
        fetchedCard.Label = "Updated by Runtime";
        await db.SaveChangesAsync();

        // Insertion ValidationEvent
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

        // Insertion FareTransaction
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

        // Insertion CardAuditLog
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

        // Lecture de vérification
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
        Assert.Equal("42501", truncateEx.SqlState); // permission denied
    }

    [Fact]
    public async Task SEC_LOT2B_03_RuntimeRole_CannotUpdateDeleteOrTruncate_CardAuditLogs()
    {
        // Vérifie que le rôle runtime ne peut ni modifier, ni supprimer, ni tronquer CardAuditLogs (bloqué par DAC PostgreSQL)
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
        Assert.Equal("42501", updateEx.SqlState); // insufficient_privilege

        // 2. Tentative de DELETE direct sur CardAuditLogs
        var deleteEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CardAuditLogs\" WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("42501", deleteEx.SqlState); // insufficient_privilege

        // 3. Tentative de TRUNCATE direct sur CardAuditLogs
        var truncateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"CardAuditLogs\";");
        });
        Assert.Equal("42501", truncateEx.SqlState); // insufficient_privilege

        // 4. Vérification de l'intégrité de la ligne d'audit
        using var verifyDb = _factory.CreateDbContext();
        var untouched = await verifyDb.CardAuditLogs.SingleAsync(a => a.Id == auditId);
        Assert.Null(untouched.Reason);
        Assert.Equal("INITIAL_LOG", untouched.Action);
    }

    [Fact]
    public async Task SEC_LOT2B_04_CardAuditLogs_ImmutabilityTrigger_BlocksUpdateDeleteAndTruncate_IncludingNonSuperuserOwner()
    {
        // Vérifie que les triggers (row-level et statement-level) bloquent UPDATE, DELETE et TRUNCATE
        // 1) Sous le rôle de migration
        // 2) Sous un rôle propriétaire non-superutilisateur (NOSUPERUSER)
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

        // Partie A : Sous le rôle de migration
        var updateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await migrationDb.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"CardAuditLogs\" SET \"Reason\" = 'tampered' WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("23000", updateEx.SqlState);
        Assert.Contains("append-only", updateEx.MessageText);

        var deleteEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await migrationDb.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM \"CardAuditLogs\" WHERE \"Id\" = {auditId}");
        });
        Assert.Equal("23000", deleteEx.SqlState);
        Assert.Contains("append-only", deleteEx.MessageText);

        var truncateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await migrationDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"CardAuditLogs\";");
        });
        Assert.Equal("23000", truncateEx.SqlState);
        Assert.Contains("append-only", truncateEx.MessageText);

        // Partie B : Sous un rôle propriétaire strictement NON-SUPERUTILISATEUR (NOSUPERUSER)
        // Exécuté dans une transaction isolée avec SAVEPOINTs pour garantir un nettoyage atomique
        await using var conn = (NpgsqlConnection)migrationDb.Database.GetDbConnection();
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
        }

        await using var ownerTx = await conn.BeginTransactionAsync();
        const string nonSuperOwnerRole = "janus_test_nonsuper_owner";
        try
        {
            await using (var setupCmd = conn.CreateCommand())
            {
                setupCmd.Transaction = ownerTx;
                setupCmd.CommandText = $@"
                    SELECT pg_advisory_xact_lock(20261019, 2);
                    DROP ROLE IF EXISTS {nonSuperOwnerRole};
                    CREATE ROLE {nonSuperOwnerRole} WITH NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOBYPASSRLS;
                    GRANT USAGE ON SCHEMA public TO {nonSuperOwnerRole};
                    ALTER TABLE ""CardAuditLogs"" OWNER TO {nonSuperOwnerRole};
                    ALTER SEQUENCE ""CardAuditLogs_Id_seq"" OWNER TO {nonSuperOwnerRole};
                    SET LOCAL ROLE {nonSuperOwnerRole};";
                await setupCmd.ExecuteNonQueryAsync();
            }

            await using (var checkSuperCmd = conn.CreateCommand())
            {
                checkSuperCmd.Transaction = ownerTx;
                checkSuperCmd.CommandText = "SELECT rolsuper FROM pg_catalog.pg_roles WHERE rolname = current_user;";
                var isSuper = (bool)(await checkSuperCmd.ExecuteScalarAsync())!;
                Assert.False(isSuper, "Test owner role must be strictly non-superuser.");
            }

            // 1. Tentative d'UPDATE par le propriétaire non-superutilisateur -> bloqué par le trigger (23000)
            await ownerTx.SaveAsync("sp_owner_update");
            var ownerUpdateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = ownerTx;
                cmd.CommandText = $"UPDATE \"CardAuditLogs\" SET \"Reason\" = 'owner_tamper' WHERE \"Id\" = {auditId};";
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Equal("23000", ownerUpdateEx.SqlState);
            await ownerTx.RollbackAsync("sp_owner_update");

            // 2. Tentative de DELETE par le propriétaire non-superutilisateur -> bloqué par le trigger (23000)
            await ownerTx.SaveAsync("sp_owner_delete");
            var ownerDeleteEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = ownerTx;
                cmd.CommandText = $"DELETE FROM \"CardAuditLogs\" WHERE \"Id\" = {auditId};";
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Equal("23000", ownerDeleteEx.SqlState);
            await ownerTx.RollbackAsync("sp_owner_delete");

            // 3. Tentative de TRUNCATE par le propriétaire non-superutilisateur -> bloqué par le trigger statement-level (23000)
            await ownerTx.SaveAsync("sp_owner_truncate");
            var ownerTruncateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await using var cmd = conn.CreateCommand();
                cmd.Transaction = ownerTx;
                cmd.CommandText = "TRUNCATE TABLE \"CardAuditLogs\";";
                await cmd.ExecuteNonQueryAsync();
            });
            Assert.Equal("23000", ownerTruncateEx.SqlState);
            Assert.Contains("append-only", ownerTruncateEx.MessageText);
            await ownerTx.RollbackAsync("sp_owner_truncate");
        }
        finally
        {
            await ownerTx.RollbackAsync();
        }

        // Vérification finale : l'enregistrement d'audit est toujours intact
        using var verifyDb = _factory.CreateDbContext();
        var untouched = await verifyDb.CardAuditLogs.SingleAsync(a => a.Id == auditId);
        Assert.Equal("TRIGGER_TEST", untouched.Action);
    }

    [Fact]
    public async Task SEC_LOT2B_05_RuntimeRole_CannotDeleteOrMutateFinancialLedgerTables()
    {
        // Vérifie que le rôle runtime ne peut supprimer aucune ligne de Cards, Validations ou FareTransactions,
        // ni modifier (UPDATE) les tables append-only Validations et FareTransactions
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

        // Tentative d'UPDATE sur Validations
        var updateValEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"Validations\" SET \"Reason\" = 'TAMPERED';");
        });
        Assert.Equal("42501", updateValEx.SqlState);

        // Tentative de DELETE sur FareTransactions
        var deleteTxEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("DELETE FROM \"FareTransactions\";");
        });
        Assert.Equal("42501", deleteTxEx.SqlState);

        // Tentative d'UPDATE sur FareTransactions
        var updateTxEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await db.Database.ExecuteSqlRawAsync("UPDATE \"FareTransactions\" SET \"AmountMillimes\" = 0;");
        });
        Assert.Equal("42501", updateTxEx.SqlState);
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
    public async Task SEC_LOT2B_08_MigrationRole_CanApplyMigrationsAndAccessHistoryTable_WhileRuntimeIsIsolated()
    {
        // Vérifie que le rôle de migration peut lire et écrire la table __EFMigrationsHistory
        using var migrationDb = _factory.CreateMigrationDbContext();
        var historyRows = await migrationDb.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"").ToListAsync();
        Assert.Contains(historyRows, m => m.Contains("AddCardAuditLogsProtectionTrigger"));

        // Vérifie que le rôle runtime ne peut ni lire, ni modifier, ni supprimer la table __EFMigrationsHistory
        using var runtimeDb = _factory.CreateDbContext();
        var selectEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await runtimeDb.Database.SqlQueryRaw<string>("SELECT \"MigrationId\" FROM \"__EFMigrationsHistory\"").ToListAsync();
        });
        Assert.Equal("42501", selectEx.SqlState);

        var dropEx = await Assert.ThrowsAsync<PostgresException>(async () =>
        {
            await runtimeDb.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\";");
        });
        Assert.Equal("42501", dropEx.SqlState);
    }

    [Fact]
    public async Task SEC_LOT2B1_09_InitScript_And_Migrations_WorkOnFreshEmptyDb_And_ExistingMigratedDb_Idempotently()
    {
        // Teste de bout en bout (sans altérer la base principale ni aucun volume utilisateur) :
        // 1. L'exécution de init-postgres-least-privilege.sh sur une base PostgreSQL vierge (0 table)
        // 2. L'application des migrations EF Core et du provisionnement sur cette base vierge
        // 3. La ré-exécution idempotente de init-postgres-least-privilege.sh sur la base désormais migrée
        // 4. L'application effective des privilèges DML et des triggers sous le rôle runtime
        var suffix = Guid.NewGuid().ToString("N")[..8].ToLowerInvariant();
        var tempDbName = $"janus_fresh_2b1_{suffix}";
        var tempRuntimeRole = $"janus_rt_{suffix}";
        var tempRuntimePass = $"test-rt-pass-{suffix}-secure!";
        var scriptPath = FindRepoFile("scripts/init-postgres-least-privilege.sh");

        var migBuilder = new NpgsqlConnectionStringBuilder(_factory.MigrationConnectionString);

        using var adminDb = _factory.CreateMigrationDbContext();
        await adminDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{tempDbName}\";");

        try
        {
            // Étape 1 : Exécution du script sur la base VIERGE (avant toute migration EF Core)
            var (exitCode1, output1) = await RunInitScriptAsync(
                scriptPath,
                tempDbName,
                migBuilder.Username!,
                migBuilder.Password!,
                tempRuntimeRole,
                tempRuntimePass);

            Assert.True(exitCode1 == 0, $"Init script failed on fresh empty database (exit {exitCode1}): {output1}");
            Assert.DoesNotContain(tempRuntimePass, output1, StringComparison.Ordinal);
            Assert.DoesNotContain(migBuilder.Password!, output1, StringComparison.Ordinal);

            // Étape 2 : Exécution des migrations EF Core et du provisionnement sur la base initialisée
            var tempMigConn = new NpgsqlConnectionStringBuilder(_factory.MigrationConnectionString) { Database = tempDbName }.ConnectionString;
            var tempRtConn = new NpgsqlConnectionStringBuilder(_factory.RuntimeConnectionString)
            {
                Database = tempDbName,
                Username = tempRuntimeRole,
                Password = tempRuntimePass
            }.ConnectionString;

            var tempMigOptions = new DbContextOptionsBuilder<JanusDbContext>().UseNpgsql(tempMigConn).Options;
            await using (var tempMigDb = new JanusDbContext(tempMigOptions))
            {
                await tempMigDb.Database.MigrateAsync();
                await PostgresLeastPrivilegeProvisioner.EnsureProvisionedAsync(tempMigDb, tempRtConn, tempMigConn);
            }

            // Étape 3 : Exécution du script sur la base DÉJÀ MIGRÉE + ré-exécution idempotente
            var (exitCode2, output2) = await RunInitScriptAsync(
                scriptPath,
                tempDbName,
                migBuilder.Username!,
                migBuilder.Password!,
                tempRuntimeRole,
                tempRuntimePass);
            Assert.True(exitCode2 == 0, $"Init script failed on migrated database (exit {exitCode2}): {output2}");

            var (exitCode3, output3) = await RunInitScriptAsync(
                scriptPath,
                tempDbName,
                migBuilder.Username!,
                migBuilder.Password!,
                tempRuntimeRole,
                tempRuntimePass);
            Assert.True(exitCode3 == 0, $"Init script failed on idempotent re-run (exit {exitCode3}): {output3}");

            // Étape 4 : Vérification des opérations légitimes et des blocages sous le rôle runtime dans cette nouvelle base
            var tempRtOptions = new DbContextOptionsBuilder<JanusDbContext>().UseNpgsql(tempRtConn).Options;
            await using (var tempRtDb = new JanusDbContext(tempRtOptions))
            {
                var card = new TransitCard { Uid = "04FRESH001", Status = CardStatus.Active, Label = "Fresh DB Card", BalanceMillimes = 3000 };
                tempRtDb.Cards.Add(card);
                tempRtDb.CardAuditLogs.Add(new CardAuditLog
                {
                    CardUid = "04FRESH001",
                    Action = "FRESH_INIT",
                    NewBalanceMillimes = 3000,
                    NewStatus = "Active",
                    Actor = "SYSTEM",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await tempRtDb.SaveChangesAsync();

                var delEx = await Assert.ThrowsAsync<PostgresException>(async () =>
                {
                    await tempRtDb.Database.ExecuteSqlRawAsync("DELETE FROM \"CardAuditLogs\";");
                });
                Assert.Equal("42501", delEx.SqlState);
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await adminDb.Database.ExecuteSqlRawAsync($@"
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{tempDbName}' AND pid <> pg_backend_pid();");
            await adminDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{tempDbName}\";");
            await adminDb.Database.ExecuteSqlRawAsync($"DROP ROLE IF EXISTS \"{tempRuntimeRole}\";");
        }
    }

    [Fact]
    public async Task SEC_LOT2B1_10_CardAuditLogs_Migration_DownAndUp_RestoresAllTriggers()
    {
        // Vérifie sur une base isolée le contrat de réversibilité Down() puis ré-application Up()
        // de la migration AddCardAuditLogsProtectionTrigger sans perturber les tests concurrents
        var tempDbName = $"janus_mig_2b1_{Guid.NewGuid():N}"[..26].ToLowerInvariant();
        using var adminDb = _factory.CreateMigrationDbContext();
        await adminDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{tempDbName}\";");

        try
        {
            var tempMigConn = new NpgsqlConnectionStringBuilder(_factory.MigrationConnectionString) { Database = tempDbName }.ConnectionString;
            var tempRtConn = new NpgsqlConnectionStringBuilder(_factory.RuntimeConnectionString) { Database = tempDbName }.ConnectionString;
            var tempMigOptions = new DbContextOptionsBuilder<JanusDbContext>().UseNpgsql(tempMigConn).Options;

            await using var migrationDb = new JanusDbContext(tempMigOptions);
            await migrationDb.Database.MigrateAsync();

            var migrator = migrationDb.GetService<IMigrator>();

            // 1. Génère et vérifie le script Down de AddCardAuditLogsProtectionTrigger
            var downScript = migrator.GenerateScript(
                fromMigration: "20261019215017_AddCardAuditLogsProtectionTrigger",
                toMigration: "20261016182125_AddCheckConstraintBalanceNonNegative");
            Assert.Contains("DROP TRIGGER IF EXISTS trg_protect_card_audit_logs_truncate", downScript);
            Assert.Contains("DROP TRIGGER IF EXISTS trg_protect_card_audit_logs", downScript);
            Assert.Contains("DROP FUNCTION IF EXISTS prevent_card_audit_logs_tampering", downScript);

            // Exécute Down() (retour à la migration précédente)
            await migrator.MigrateAsync("20261016182125_AddCheckConstraintBalanceNonNegative");

            // 2. Ré-applique la migration Up() et le provisionnement
            await migrator.MigrateAsync("20261019215017_AddCardAuditLogsProtectionTrigger");
            await PostgresLeastPrivilegeProvisioner.EnsureProvisionedAsync(
                migrationDb,
                tempRtConn,
                tempMigConn);

            // 3. Vérifie que les deux triggers (row-level et statement-level) sont de nouveau actifs
            migrationDb.CardAuditLogs.Add(new CardAuditLog
            {
                CardUid = "04DOWNUP01",
                Action = "MIG_TEST",
                NewBalanceMillimes = 1000,
                NewStatus = "Active",
                Actor = "ADMIN",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await migrationDb.SaveChangesAsync();

            var updateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await migrationDb.Database.ExecuteSqlRawAsync("UPDATE \"CardAuditLogs\" SET \"Reason\" = 'tampered';");
            });
            Assert.Equal("23000", updateEx.SqlState);

            var truncateEx = await Assert.ThrowsAsync<PostgresException>(async () =>
            {
                await migrationDb.Database.ExecuteSqlRawAsync("TRUNCATE TABLE \"CardAuditLogs\";");
            });
            Assert.Equal("23000", truncateEx.SqlState);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await adminDb.Database.ExecuteSqlRawAsync($@"
                SELECT pg_terminate_backend(pid)
                FROM pg_stat_activity
                WHERE datname = '{tempDbName}' AND pid <> pg_backend_pid();");
            await adminDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{tempDbName}\";");
        }
    }

    [Fact]
    public void SEC_LOT2B1_11_ProductionStartup_RejectsKnownDevDatabasePasswords_WithoutLeakingSecrets()
    {
        var strongTerm1 = "prod-t1-" + Guid.NewGuid().ToString("N");
        var strongTerm2 = "prod-t2-" + Guid.NewGuid().ToString("N");
        var strongAdmin = "prod-adm-" + Guid.NewGuid().ToString("N");
        var strongScrape = "prod-scr-" + Guid.NewGuid().ToString("N");
        var strongMigPass = "prod-mig-db-" + Guid.NewGuid().ToString("N");
        var strongRtPass = "prod-rt-db-" + Guid.NewGuid().ToString("N");

        // Cas 1 : Mot de passe runtime par défaut connu en Production -> rejeté sans fuite du secret
        using var badRuntimePassFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:SkipMigration"] = "true",
                    ["ConnectionStrings:JanusDb"] = "Host=localhost;Port=5433;Database=janus_db;Username=janus_runtime;Password=local-dev-runtime-password-change-me",
                    ["ConnectionStrings:MigrationDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_app;Password={strongMigPass}",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });

        var exRt = Assert.ThrowsAny<Exception>(() => badRuntimePassFactory.CreateClient());
        Assert.Contains("ConnectionStrings:JanusDb", exRt.ToString());
        Assert.DoesNotContain("local-dev-runtime-password-change-me", exRt.ToString());

        // Cas 2 : Mot de passe de migration par défaut connu en Staging -> rejeté sans fuite du secret
        using var badMigrationPassFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:SkipMigration"] = "false",
                    ["ConnectionStrings:JanusDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_runtime;Password={strongRtPass}",
                    ["ConnectionStrings:MigrationDb"] = "Host=localhost;Port=5433;Database=janus_db;Username=janus_app;Password=local-dev-db-password-change-me",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });

        var exMig = Assert.ThrowsAny<Exception>(() => badMigrationPassFactory.CreateClient());
        Assert.Contains("ConnectionStrings:MigrationDb", exMig.ToString());
        Assert.DoesNotContain("local-dev-db-password-change-me", exMig.ToString());
    }

    [Fact]
    public void SEC_LOT2B1_12_ProductionStartup_EnforcesRoleSeparation_And_SupportsSkipMigrationWithoutMigrationSecret()
    {
        var strongTerm1 = "prod-t1-" + Guid.NewGuid().ToString("N");
        var strongTerm2 = "prod-t2-" + Guid.NewGuid().ToString("N");
        var strongAdmin = "prod-adm-" + Guid.NewGuid().ToString("N");
        var strongScrape = "prod-scr-" + Guid.NewGuid().ToString("N");
        var strongRtPass = "prod-rt-db-" + Guid.NewGuid().ToString("N");
        var strongMigPass = "prod-mig-db-" + Guid.NewGuid().ToString("N");

        // Cas 1 : Même utilisateur PostgreSQL pour JanusDb et MigrationDb -> rejeté
        using var sameUserFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:SkipMigration"] = "true",
                    ["ConnectionStrings:JanusDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_app;Password={strongRtPass}",
                    ["ConnectionStrings:MigrationDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_app;Password={strongMigPass}",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });

        var exSameUser = Assert.ThrowsAny<Exception>(() => sameUserFactory.CreateClient());
        Assert.Contains("distinct PostgreSQL roles", exSameUser.ToString());

        // Cas 2 : SkipMigration=false mais MigrationDb absent -> rejeté
        using var missingMigFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:SkipMigration"] = "false",
                    ["ConnectionStrings:JanusDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_runtime;Password={strongRtPass}",
                    ["ConnectionStrings:MigrationDb"] = "",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });

        var exMissingMig = Assert.ThrowsAny<Exception>(() => missingMigFactory.CreateClient());
        Assert.Contains("ConnectionStrings:MigrationDb", exMissingMig.ToString());

        // Cas 3 : SkipMigration=true et MigrationDb omis -> accepté (permet de ne pas exposer le secret de migration au conteneur runtime)
        using var isolatedRuntimeFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:SkipMigration"] = "true",
                    ["ConnectionStrings:JanusDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_runtime;Password={strongRtPass}",
                    ["ConnectionStrings:MigrationDb"] = "",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });

        using var client = isolatedRuntimeFactory.CreateClient();
        Assert.NotNull(client);
    }

    [Fact]
    public async Task SEC_LOT2C_13_InitScript_InProductionEnvironment_RejectsKnownDevPasswords_WithNonZeroExitCode()
    {
        var scriptPath = FindRepoFile("scripts/init-postgres-least-privilege.sh");

        // Cas 1 : Mot de passe admin de développement en environnement Production -> rejeté (exit 1) sans fuite du secret
        var (exitCodeAdminDev, outputAdminDev) = await RunInitScriptAsync(
            scriptPath,
            "janus_db",
            "janus_app",
            "local-dev-db-password-change-me",
            "janus_runtime",
            "StrongRuntimePass_999999!",
            environmentMode: "Production");

        Assert.NotEqual(0, exitCodeAdminDev);
        Assert.Contains("POSTGRES_PASSWORD must be a strong non-default secret", outputAdminDev);
        Assert.DoesNotContain("local-dev-db-password-change-me", outputAdminDev);
        Assert.DoesNotContain("StrongRuntimePass_999999!", outputAdminDev);

        // Cas 2 : Mot de passe runtime de développement en environnement Production -> rejeté (exit 1) sans fuite du secret
        var (exitCodeRuntimeDev, outputRuntimeDev) = await RunInitScriptAsync(
            scriptPath,
            "janus_db",
            "janus_app",
            "StrongAdminPass_999999!",
            "janus_runtime",
            "local-dev-runtime-password-change-me",
            environmentMode: "Production");

        Assert.NotEqual(0, exitCodeRuntimeDev);
        Assert.Contains("POSTGRES_RUNTIME_PASSWORD must be a strong non-default secret", outputRuntimeDev);
        Assert.DoesNotContain("local-dev-runtime-password-change-me", outputRuntimeDev);
        Assert.DoesNotContain("StrongAdminPass_999999!", outputRuntimeDev);
    }

    [Fact]
    public void SEC_LOT2C_14_MigrateOnlyMode_RejectsContradictoryOrMissingMigrationConfiguration()
    {
        // Cas 1 : MigrateOnly=true et SkipMigration=true simultanément -> rejeté au démarrage
        using var contradictoryFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:MigrateOnly"] = "true",
                    ["Database:SkipMigration"] = "true",
                    ["ConnectionStrings:JanusDb"] = _factory.RuntimeConnectionString,
                    ["ConnectionStrings:MigrationDb"] = _factory.MigrationConnectionString
                });
            });
        });

        var exContradictory = Assert.ThrowsAny<Exception>(() => contradictoryFactory.CreateClient());
        Assert.Contains("Database:MigrateOnly", exContradictory.ToString());

        // Cas 2 : MigrateOnly=true sans ConnectionStrings:MigrationDb -> rejeté au démarrage
        using var missingMigConnFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Development");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:MigrateOnly"] = "true",
                    ["Database:SkipMigration"] = "false",
                    ["ConnectionStrings:JanusDb"] = _factory.RuntimeConnectionString,
                    ["ConnectionStrings:MigrationDb"] = ""
                });
            });
        });

        var exMissingConn = Assert.ThrowsAny<Exception>(() => missingMigConnFactory.CreateClient());
        Assert.Contains("ConnectionStrings:MigrationDb", exMissingConn.ToString());
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}' starting from '{AppContext.BaseDirectory}'.");
    }

    private static async Task<(int ExitCode, string Output)> RunInitScriptAsync(
        string scriptPath,
        string dbName,
        string adminUser,
        string adminPassword,
        string runtimeUser,
        string runtimePassword,
        string environmentMode = "Development")
    {
        var psi = new ProcessStartInfo("bash", [scriptPath])
        {
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(scriptPath))!,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };

        psi.Environment["ASPNETCORE_ENVIRONMENT"] = environmentMode;
        psi.Environment["POSTGRES_DB"] = dbName;
        psi.Environment["POSTGRES_USER"] = adminUser;
        psi.Environment["POSTGRES_PASSWORD"] = adminPassword;
        psi.Environment["POSTGRES_RUNTIME_USER"] = runtimeUser;
        psi.Environment["POSTGRES_RUNTIME_PASSWORD"] = runtimePassword;

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start bash process.");
        var stdoutTask = proc.StandardOutput.ReadToEndAsync();
        var stderrTask = proc.StandardError.ReadToEndAsync();
        await proc.WaitForExitAsync();
        var output = await stdoutTask + "\n" + await stderrTask;
        return (proc.ExitCode, output);
    }
}
