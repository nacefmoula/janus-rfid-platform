using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Janus.Api.Tests;

/// <summary>
/// LOT-5A — Organisations et bornes en base :
/// migration sans UPDATE (triggers append-only intacts), jetons de borne hachés révocables et expirants, organisation suspendue,
/// audit append-only des bornes, droits PostgreSQL par colonne, isolation des cartes entre organisations, cohérence d'organisation
/// garantie par la base, organisation obligatoire hors Development, labels Prometheus bornés.
/// </summary>
public class OrganizationsAndTerminalsTests : IAsyncLifetime
{
    private readonly JanusApiFactory _factory = new() { AntiPassbackEnabledOverride = false };
    private WebApplicationFactory<Program> _app = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        // Limiteurs relevés : ces tests enchaînent des appels administrateur et des rafales de validations (CON01) ;
        // le rate limiting lui-même est couvert par SecurityHardeningTests. Même SimulatedTime que _factory.
        _app = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RateLimiting:ValidationsPermitLimit"] = "10000",
            ["RateLimiting:AdminPermitLimit"] = "10000",
            ["RateLimiting:CardsReadPermitLimit"] = "10000",
            ["RateLimiting:AnonymousPermitLimit"] = "10000"
        })));
        _client = _app.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        await _factory.DisposeAsync();
    }

    // ---------------------------------------------------------------- helpers

    private static string Suffix() => Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private static string NewUid() => "04OR" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static HttpRequestMessage Authed(HttpMethod method, string url, string? token, object? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (body is not null) req.Content = JsonContent.Create(body);
        if (token is not null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return req;
    }

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.Clone();
    }

    private static async Task<string?> ErrorAsync(HttpResponseMessage resp) =>
        (await JsonAsync(resp)).TryGetProperty("error", out var e) ? e.GetString() : null;

    private async Task<string> CreateOrganizationAsync(HttpClient? client = null, string? adminToken = null)
    {
        var code = "ORG-" + Suffix();
        using var resp = await (client ?? _client).SendAsync(Authed(HttpMethod.Post, "/api/organizations", adminToken ?? JanusApiFactory.AdminToken,
            new { code, name = "Organisation " + code }));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return code;
    }

    private async Task<(string Code, string Token)> EnrollAsync(string organizationCode, string? terminalCode = null)
    {
        var code = terminalCode ?? "BORNE-" + Suffix();
        using var resp = await _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{organizationCode}/terminals", JanusApiFactory.AdminToken,
            new { code, label = "Borne " + code }));
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await JsonAsync(resp);
        return (code, body.GetProperty("token").GetString()!);
    }

    private Task<HttpResponseMessage> RevokeAsync(string terminalCode, string reason = "Borne volée")
        => _client.SendAsync(Authed(HttpMethod.Post, $"/api/terminals/{terminalCode}/revocation", JanusApiFactory.AdminToken, new { reason }));

    private async Task<string> CreateCardAsync(string? organizationCode, int balance = 5000, HttpClient? client = null, string? adminToken = null)
    {
        var uid = NewUid();
        using var resp = await (client ?? _client).SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", adminToken ?? JanusApiFactory.AdminToken,
            new { balanceMillimes = balance, status = "Active", reason = "lot 5A provisioning", organizationCode }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        return uid;
    }

    private Task<HttpResponseMessage> ValidateAsync(string token, string uid, Guid? eventId = null)
        => _client.SendAsync(Authed(HttpMethod.Post, "/api/validations", token,
            new { eventId = eventId ?? Guid.NewGuid(), cardUid = uid, occurredAt = DateTimeOffset.UtcNow, eventType = "TRANSPORT_VALIDATION" }));

    private async Task<int> OrganizationIdAsync(string code)
    {
        using var db = _factory.CreateDbContext();
        return await db.Organizations.AsNoTracking().Where(o => o.Code == code).Select(o => o.Id).SingleAsync();
    }

    private static DateTimeOffset TruncateToMicroseconds(DateTimeOffset value) =>
        new(value.Ticks - value.Ticks % 10, value.Offset);

    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<PostgresException> SqlFailsAsync(string connectionString, string sql)
    {
        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        return await Assert.ThrowsAsync<PostgresException>(() => cmd.ExecuteNonQueryAsync());
    }

    // ---------------------------------------------------------------- migration

    [Fact]
    public async Task MIG01_MIG02_Migration_AttachesExistingRowsWithoutUpdate_AndKeepsAppendOnlyTriggersActive()
    {
        var tempDbName = $"janus_mig_5a_{Guid.NewGuid():N}"[..26].ToLowerInvariant();
        using var adminDb = _factory.CreateMigrationDbContext();
#pragma warning disable EF1002
        await adminDb.Database.ExecuteSqlRawAsync($"CREATE DATABASE \"{tempDbName}\";");
#pragma warning restore EF1002
        var tempMigConn = new NpgsqlConnectionStringBuilder(_factory.MigrationConnectionString) { Database = tempDbName }.ConnectionString;

        try
        {
            await using (var migrationDb = new JanusDbContext(new DbContextOptionsBuilder<JanusDbContext>().UseNpgsql(tempMigConn).Options))
            {
                var migrator = migrationDb.GetService<IMigrator>();
                await migrator.MigrateAsync("20261127110313_AddWalletLedger");

                // Données « antérieures au Lot 5A » sur les 5 tables, dont les deux tables append-only.
                await migrationDb.Database.ExecuteSqlRawAsync(@"
                    INSERT INTO ""Cards"" (""Uid"", ""Status"", ""Label"", ""BalanceMillimes"") VALUES ('04PRE5A0000001', 0, 'Avant lot 5A', 3500);
                    INSERT INTO ""WalletLedgerEntries"" (""CardId"", ""CardUid"", ""EntryType"", ""AmountMillimes"", ""BalanceAfterMillimes"", ""IdempotencyKey"", ""Actor"", ""CreatedAt"")
                        SELECT ""Id"", ""Uid"", 'OPENING_BALANCE', 5000, 5000, 'opening:pre5a:1', 'ADMIN', now() FROM ""Cards"" WHERE ""Uid"" = '04PRE5A0000001';
                    INSERT INTO ""WalletLedgerEntries"" (""CardId"", ""CardUid"", ""EntryType"", ""AmountMillimes"", ""BalanceAfterMillimes"", ""IdempotencyKey"", ""Actor"", ""CreatedAt"")
                        SELECT ""Id"", ""Uid"", 'FARE_DEBIT', -1500, 3500, 'fare:pre5a:1', 'TERMINAL-DEMO-01', now() FROM ""Cards"" WHERE ""Uid"" = '04PRE5A0000001';
                    INSERT INTO ""FareTransactions"" (""CardId"", ""CardUid"", ""TerminalId"", ""EventId"", ""AmountMillimes"", ""BalanceAfterMillimes"", ""CreatedAt"")
                        SELECT ""Id"", ""Uid"", 'TERMINAL-DEMO-01', gen_random_uuid(), 1500, 3500, now() FROM ""Cards"" WHERE ""Uid"" = '04PRE5A0000001';
                    INSERT INTO ""Validations"" (""TerminalId"", ""EventId"", ""CardUid"", ""PayloadHash"", ""EventType"", ""Decision"", ""Reason"", ""ReceivedAt"")
                        VALUES ('TERMINAL-DEMO-01', gen_random_uuid(), '04PRE5A0000001', repeat('A', 64), 'TRANSPORT_VALIDATION', 0, 'ACTIVE_CARD', now());
                    INSERT INTO ""CardAuditLogs"" (""CardUid"", ""Action"", ""NewBalanceMillimes"", ""NewStatus"", ""Actor"", ""CreatedAt"")
                        VALUES ('04PRE5A0000001', 'ADMIN_CARD_CREATED', 5000, 'Active', 'ADMIN', now());");

                await migrator.MigrateAsync();
            }

            // MIG01 : toutes les lignes existantes rattachées à DEMO-TRANSIT, et plus aucun DEFAULT sur la colonne.
            foreach (var table in new[] { "Cards", "Validations", "FareTransactions", "WalletLedgerEntries", "CardAuditLogs" })
            {
                Assert.True(Convert.ToInt64(await ScalarAsync(tempMigConn, $"SELECT count(*) FROM \"{table}\"")) > 0, table);
                Assert.Equal(0L, Convert.ToInt64(await ScalarAsync(tempMigConn, $"SELECT count(*) FROM \"{table}\" WHERE \"OrganizationId\" <> {OrganizationDefaults.DemoOrganizationId}")));
                var columnDefault = await ScalarAsync(tempMigConn,
                    $"SELECT column_default FROM information_schema.columns WHERE table_schema = 'public' AND table_name = '{table}' AND column_name = 'OrganizationId'");
                Assert.True(columnDefault is null or DBNull, $"{table}.OrganizationId still has a DEFAULT");
            }

            // MIG02 : triggers append-only toujours actifs (jamais désactivés par la migration) et effectifs.
            foreach (var trigger in new[] { "trg_protect_card_audit_logs", "trg_protect_card_audit_logs_truncate", "trg_protect_wallet_ledger", "trg_protect_wallet_ledger_truncate" })
            {
                Assert.Equal("O", Convert.ToString(await ScalarAsync(tempMigConn, $"SELECT tgenabled::text FROM pg_trigger WHERE tgname = '{trigger}'")));
            }
            Assert.Equal("23000", (await SqlFailsAsync(tempMigConn, "UPDATE \"CardAuditLogs\" SET \"Reason\" = 'tampered'")).SqlState);
            Assert.Equal("23000", (await SqlFailsAsync(tempMigConn, "UPDATE \"WalletLedgerEntries\" SET \"Reason\" = 'tampered'")).SqlState);

            // MIG03 : organisation de reprise à Id fixe, et séquence positionnée après elle.
            Assert.Equal("DEMO-TRANSIT", Convert.ToString(await ScalarAsync(tempMigConn, "SELECT \"Code\" FROM \"Organizations\" WHERE \"Id\" = 1")));
            Assert.Equal(2, Convert.ToInt32(await ScalarAsync(tempMigConn,
                "INSERT INTO \"Organizations\" (\"Code\", \"Name\", \"Status\", \"CreatedAt\") VALUES ('NEXT-ORG', 'Next', 0, now()) RETURNING \"Id\"")));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await adminDb.Database.ExecuteSqlAsync($"SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = {tempDbName} AND pid <> pg_backend_pid();");
#pragma warning disable EF1002
            await adminDb.Database.ExecuteSqlRawAsync($"DROP DATABASE IF EXISTS \"{tempDbName}\";");
#pragma warning restore EF1002
        }
    }

    // ---------------------------------------------------------------- enrollment and authentication

    [Fact]
    public async Task TRM01_Enrollment_ReturnsTokenOnce_NotCacheable_AndListingNeverExposesTokenOrHash()
    {
        var org = await CreateOrganizationAsync();
        var code = "BORNE-" + Suffix();

        using var resp = await _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken, new { code, label = "Quai 1" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        Assert.Contains("no-store", resp.Headers.CacheControl?.ToString() ?? "");
        var body = await JsonAsync(resp);
        var token = body.GetProperty("token").GetString()!;
        Assert.StartsWith("jt_", token, StringComparison.Ordinal);
        Assert.Equal(org, body.GetProperty("organizationCode").GetString());

        using var list = await _client.SendAsync(Authed(HttpMethod.Get, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken));
        var listJson = await list.Content.ReadAsStringAsync();
        Assert.Contains(code, listJson);
        Assert.DoesNotContain(token, listJson);
        Assert.DoesNotContain(TerminalToken.ComputeHash(token), listJson);
        Assert.DoesNotContain("tokenHash", listJson, StringComparison.OrdinalIgnoreCase);

        using var db = _factory.CreateDbContext();
        var stored = await db.Terminals.AsNoTracking().SingleAsync(t => t.Code == code);
        Assert.Equal(TerminalToken.ComputeHash(token), stored.TokenHash);
    }

    [Fact]
    public async Task TRM02_EnrolledToken_ValidatesCardOfItsOrganization_WithOrganizationOnEveryRow()
    {
        var org = await CreateOrganizationAsync();
        var (code, token) = await EnrollAsync(org);
        var uid = await CreateCardAsync(org, 5000);
        var eventId = Guid.NewGuid();

        using var resp = await ValidateAsync(token, uid, eventId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonAsync(resp);
        Assert.Equal("ACCEPTED", body.GetProperty("decision").GetString());
        Assert.Equal(code, body.GetProperty("terminalId").GetString());

        var orgId = await OrganizationIdAsync(org);
        using var db = _factory.CreateDbContext();
        Assert.Equal(orgId, (await db.Validations.AsNoTracking().SingleAsync(v => v.TerminalId == code && v.EventId == eventId)).OrganizationId);
        Assert.Equal(orgId, (await db.FareTransactions.AsNoTracking().SingleAsync(f => f.TerminalId == code && f.EventId == eventId)).OrganizationId);
        Assert.Equal(orgId, (await db.WalletLedgerEntries.AsNoTracking().SingleAsync(e => e.IdempotencyKey == LedgerIdempotencyKeys.FareDebit(code, eventId))).OrganizationId);
    }

    [Fact]
    public async Task TRM03_RevokedTerminal_IsRefusedImmediately_WithExplicitReason()
    {
        var org = await CreateOrganizationAsync();
        var (code, token) = await EnrollAsync(org);
        var uid = await CreateCardAsync(org);
        using (var before = await ValidateAsync(token, uid)) Assert.Equal(HttpStatusCode.OK, before.StatusCode);

        using (var revoke = await RevokeAsync(code)) Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var after = await ValidateAsync(token, uid);
        Assert.Equal(HttpStatusCode.Unauthorized, after.StatusCode);
        Assert.Equal("revoked_token", await ErrorAsync(after));
    }

    [Fact]
    public async Task TRM04_ExpiredToken_IsRefused_WithExplicitReason()
    {
        var org = await CreateOrganizationAsync();
        var (_, token) = await EnrollAsync(org);
        var uid = await CreateCardAsync(org);

        _factory.SimulatedTime.Advance(TimeSpan.FromDays(TerminalTokenLifetime.DefaultDays));

        using var resp = await ValidateAsync(token, uid);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("expired_token", await ErrorAsync(resp));
    }

    [Fact]
    public async Task TRM05_TRM09_SuspendedOrganization_RefusesValidTokens_AndNewEnrollments()
    {
        var org = await CreateOrganizationAsync();
        var (_, token) = await EnrollAsync(org);
        var uid = await CreateCardAsync(org);

        // Pas d'endpoint de suspension en 5A (décision B) : suspension par le rôle propriétaire.
        await ScalarAsync(_factory.MigrationConnectionString, $"UPDATE \"Organizations\" SET \"Status\" = 1 WHERE \"Code\" = '{org}'");

        using var resp = await ValidateAsync(token, uid);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal("organization_suspended", await ErrorAsync(resp));

        using var enroll = await _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken, new { code = "BORNE-" + Suffix(), label = "x" }));
        Assert.Equal(HttpStatusCode.Conflict, enroll.StatusCode);
        Assert.Equal("organization_suspended", await ErrorAsync(enroll));
    }

    [Fact]
    public async Task TRM06_Revocation_IsIrreversible_ThroughApiAndDatabase()
    {
        var org = await CreateOrganizationAsync();
        var (code, _) = await EnrollAsync(org);
        using (var first = await RevokeAsync(code)) Assert.Equal(HttpStatusCode.OK, first.StatusCode);

        using var second = await RevokeAsync(code);
        Assert.Equal(HttpStatusCode.Conflict, second.StatusCode);
        Assert.Equal("terminal_already_revoked", await ErrorAsync(second));

        // Même le propriétaire de la table ne peut pas réactiver la borne (trigger), sauf à désactiver le trigger.
        var ex = await SqlFailsAsync(_factory.MigrationConnectionString,
            $"UPDATE \"Terminals\" SET \"Status\" = 0, \"RevokedAt\" = NULL, \"RevokedBy\" = NULL, \"RevocationReason\" = NULL WHERE \"Code\" = '{code}'");
        Assert.Equal("23000", ex.SqlState);
    }

    [Fact]
    public async Task TRM07_ConcurrentEnrollmentWithSameCode_ExactlyOneSucceeds()
    {
        var org = await CreateOrganizationAsync();
        var code = "BORNE-" + Suffix();

        var responses = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
            _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken, new { code, label = "Concurrence" }))));

        Assert.Equal(1, responses.Count(r => r.StatusCode == HttpStatusCode.Created));
        foreach (var conflict in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
            Assert.Equal("terminal_code_taken", await ErrorAsync(conflict));
        }

        using var db = _factory.CreateDbContext();
        Assert.Equal(1, await db.Terminals.CountAsync(t => t.Code == code));
        var terminalId = await db.Terminals.Where(t => t.Code == code).Select(t => t.Id).SingleAsync();
        Assert.Equal(1, await db.TerminalAuditLogs.CountAsync(a => a.TerminalId == terminalId));
        foreach (var r in responses) r.Dispose();
    }

    [Fact]
    public async Task TRM08_TerminalCodes_AreUniqueAcrossOrganizations()
    {
        var orgA = await CreateOrganizationAsync();
        var orgB = await CreateOrganizationAsync();
        var (code, _) = await EnrollAsync(orgA);

        using var resp = await _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{orgB}/terminals", JanusApiFactory.AdminToken, new { code, label = "Doublon" }));

        Assert.Equal(HttpStatusCode.Conflict, resp.StatusCode);
        Assert.Equal("terminal_code_taken", await ErrorAsync(resp));
    }

    [Fact]
    public async Task TRM09_EnrollmentAndRevocation_InputValidation()
    {
        var org = await CreateOrganizationAsync();

        using (var badCode = await _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken, new { code = "borne 1", label = "x" })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badCode.StatusCode);
            Assert.Equal("invalid_terminal_code", await ErrorAsync(badCode));
        }
        using (var badLabel = await _client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken, new { code = "BORNE-" + Suffix(), label = new string('L', 121) })))
        {
            Assert.Equal("invalid_label", await ErrorAsync(badLabel));
        }
        using (var unknownOrg = await _client.SendAsync(Authed(HttpMethod.Post, "/api/organizations/NOPE-" + Suffix() + "/terminals", JanusApiFactory.AdminToken, new { code = "BORNE-" + Suffix(), label = "x" })))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownOrg.StatusCode);
            Assert.Equal("organization_not_found", await ErrorAsync(unknownOrg));
        }
        using (var noReason = await _client.SendAsync(Authed(HttpMethod.Post, "/api/terminals/TERMINAL-DEMO-01/revocation", JanusApiFactory.AdminToken, new { reason = " " })))
        {
            Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
            Assert.Equal("invalid_reason", await ErrorAsync(noReason));
        }
        using (var unknownTerminal = await RevokeAsync("BORNE-" + Suffix()))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknownTerminal.StatusCode);
        }
        using (var badOrg = await _client.SendAsync(Authed(HttpMethod.Post, "/api/organizations", JanusApiFactory.AdminToken, new { code = "x", name = "x" })))
        {
            Assert.Equal("invalid_organization_code", await ErrorAsync(badOrg));
        }
        using (var duplicateOrg = await _client.SendAsync(Authed(HttpMethod.Post, "/api/organizations", JanusApiFactory.AdminToken, new { code = org, name = "Doublon" })))
        {
            Assert.Equal(HttpStatusCode.Conflict, duplicateOrg.StatusCode);
            Assert.Equal("organization_code_taken", await ErrorAsync(duplicateOrg));
        }
    }

    [Fact]
    public async Task TRM10_ExpiresAt_FollowsConfiguredLifetime_AndTimeProvider()
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Terminals:TokenLifetimeDays"] = "7" })));
        using var client = factory.CreateClient();
        var org = await CreateOrganizationAsync(client);
        var code = "BORNE-" + Suffix();

        using var resp = await client.SendAsync(Authed(HttpMethod.Post, $"/api/organizations/{org}/terminals", JanusApiFactory.AdminToken, new { code, label = "x" }));

        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var body = await JsonAsync(resp);
        var now = _factory.SimulatedTime.GetUtcNow();
        Assert.Equal(now.AddDays(7), body.GetProperty("expiresAt").GetDateTimeOffset());
        Assert.Equal(now, body.GetProperty("createdAt").GetDateTimeOffset());
    }

    [Fact]
    public async Task TRM11_OrganizationAndTerminalAdministration_IsAdminOnly()
    {
        using (var anonymous = await _client.SendAsync(Authed(HttpMethod.Get, "/api/organizations", null)))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var terminal = await _client.SendAsync(Authed(HttpMethod.Post, "/api/organizations", JanusApiFactory.TerminalToken, new { code = "ORG-" + Suffix(), name = "x" })))
            Assert.Equal(HttpStatusCode.Forbidden, terminal.StatusCode);
        using (var terminalEnroll = await _client.SendAsync(Authed(HttpMethod.Post, "/api/organizations/DEMO-TRANSIT/terminals", JanusApiFactory.TerminalToken, new { code = "BORNE-" + Suffix(), label = "x" })))
            Assert.Equal(HttpStatusCode.Forbidden, terminalEnroll.StatusCode);
        using (var terminalRevoke = await _client.SendAsync(Authed(HttpMethod.Post, "/api/terminals/TERMINAL-DEMO-02/revocation", JanusApiFactory.TerminalToken, new { reason = "x" })))
            Assert.Equal(HttpStatusCode.Forbidden, terminalRevoke.StatusCode);
        using (var unknownToken = await _client.SendAsync(Authed(HttpMethod.Post, "/api/validations", "jt_unknown-" + Suffix(), new { eventId = Guid.NewGuid(), cardUid = "04X", eventType = "TRANSPORT_VALIDATION" })))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, unknownToken.StatusCode);
            Assert.Equal("unauthorized", await ErrorAsync(unknownToken));
        }
    }

    // ---------------------------------------------------------------- audit and privileges

    [Fact]
    public async Task AUD01_EnrollmentAndRevocation_AreAudited_WithoutTokenOrHash()
    {
        var org = await CreateOrganizationAsync();
        var (code, token) = await EnrollAsync(org);
        _factory.SimulatedTime.Advance(TimeSpan.FromMinutes(3));
        using (var revoke = await RevokeAsync(code, "Remplacement matériel")) Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);

        using var db = _factory.CreateDbContext();
        var terminal = await db.Terminals.AsNoTracking().SingleAsync(t => t.Code == code);
        var audit = await db.TerminalAuditLogs.AsNoTracking().Where(a => a.TerminalId == terminal.Id).OrderBy(a => a.Id).ToListAsync();

        Assert.Equal([TerminalAuditActions.Enrolled, TerminalAuditActions.Revoked], audit.Select(a => a.Action));
        Assert.All(audit, a => Assert.Equal("ADMIN", a.Actor));
        Assert.All(audit, a => Assert.Equal(terminal.OrganizationId, a.OrganizationId));
        Assert.Equal(terminal.ExpiresAt, audit[0].TokenExpiresAt);
        Assert.Equal("Remplacement matériel", audit[1].Reason);
        // PostgreSQL stocke les horodatages à la microseconde (.NET : 100 ns).
        Assert.Equal(TruncateToMicroseconds(_factory.SimulatedTime.GetUtcNow()), audit[1].CreatedAt);
        Assert.Equal(audit[1].CreatedAt, terminal.RevokedAt);

        var columns = Convert.ToString(await ScalarAsync(_factory.MigrationConnectionString,
            "SELECT string_agg(column_name, ',') FROM information_schema.columns WHERE table_schema = 'public' AND table_name = 'TerminalAuditLogs'"))!;
        Assert.DoesNotContain("Token\"", columns);
        Assert.DoesNotContain("Hash", columns, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(audit, a => (a.Reason ?? "").Contains(token, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AUD02_TerminalAuditLogs_IsAppendOnly_ForRuntimeAndOwner()
    {
        var org = await CreateOrganizationAsync();
        await EnrollAsync(org);
        var mig = _factory.MigrationConnectionString;

        foreach (var privilege in new[] { "SELECT", "INSERT" })
            Assert.True((bool)(await ScalarAsync(mig, $"SELECT has_table_privilege('janus_runtime', 'public.\"TerminalAuditLogs\"', '{privilege}')"))!);
        foreach (var privilege in new[] { "UPDATE", "DELETE", "TRUNCATE" })
            Assert.False((bool)(await ScalarAsync(mig, $"SELECT has_table_privilege('janus_runtime', 'public.\"TerminalAuditLogs\"', '{privilege}')"))!);

        Assert.Equal("42501", (await SqlFailsAsync(_factory.RuntimeConnectionString, "UPDATE \"TerminalAuditLogs\" SET \"Reason\" = 'x'")).SqlState);
        // Le propriétaire est bloqué par les triggers (protection contre l'erreur et l'accident ; un propriétaire malveillant
        // pourrait désactiver le trigger : d'où l'export externe WORM/SIEM recommandé au README).
        Assert.Equal("23000", (await SqlFailsAsync(mig, "UPDATE \"TerminalAuditLogs\" SET \"Reason\" = 'x'")).SqlState);
        Assert.Equal("23000", (await SqlFailsAsync(mig, "DELETE FROM \"TerminalAuditLogs\"")).SqlState);
        Assert.Equal("23000", (await SqlFailsAsync(mig, "TRUNCATE TABLE \"TerminalAuditLogs\" CASCADE")).SqlState);
    }

    [Fact]
    public async Task PRV01_PRV02_ColumnLevelPrivileges_OnTerminals_AndNoUpdateOnOrganizations()
    {
        var mig = _factory.MigrationConnectionString;
        foreach (var column in new[] { "Status", "RevokedAt", "RevokedBy", "RevocationReason" })
            Assert.True((bool)(await ScalarAsync(mig, $"SELECT has_column_privilege('janus_runtime', 'public.\"Terminals\"', '{column}', 'UPDATE')"))!, column);
        foreach (var column in new[] { "TokenHash", "OrganizationId", "Code", "ExpiresAt", "CreatedBy" })
            Assert.False((bool)(await ScalarAsync(mig, $"SELECT has_column_privilege('janus_runtime', 'public.\"Terminals\"', '{column}', 'UPDATE')"))!, column);
        Assert.False((bool)(await ScalarAsync(mig, "SELECT has_table_privilege('janus_runtime', 'public.\"Terminals\"', 'DELETE')"))!);

        var rt = _factory.RuntimeConnectionString;
        Assert.Equal("42501", (await SqlFailsAsync(rt, "UPDATE \"Terminals\" SET \"TokenHash\" = repeat('B', 64) WHERE \"Code\" = 'TERMINAL-DEMO-01'")).SqlState);
        Assert.Equal("42501", (await SqlFailsAsync(rt, "UPDATE \"Terminals\" SET \"OrganizationId\" = 1 WHERE \"Code\" = 'TERMINAL-DEMO-01'")).SqlState);
        Assert.Equal("42501", (await SqlFailsAsync(rt, "DELETE FROM \"Terminals\" WHERE \"Code\" = 'TERMINAL-DEMO-01'")).SqlState);

        // PRV02 : décision B — aucune modification des organisations par le runtime en 5A.
        Assert.False((bool)(await ScalarAsync(mig, "SELECT has_any_column_privilege('janus_runtime', 'public.\"Organizations\"', 'UPDATE')"))!);
        Assert.Equal("42501", (await SqlFailsAsync(rt, "UPDATE \"Organizations\" SET \"Status\" = 1 WHERE \"Id\" = 1")).SqlState);
    }

    [Fact]
    public async Task PRV03_CardIdentity_IsImmutable_ForRuntimeAndOwner()
    {
        var uid = await CreateCardAsync(null);
        var mig = _factory.MigrationConnectionString;
        var rt = _factory.RuntimeConnectionString;

        foreach (var column in new[] { "Status", "Label", "BalanceMillimes" })
            Assert.True((bool)(await ScalarAsync(mig, $"SELECT has_column_privilege('janus_runtime', 'public.\"Cards\"', '{column}', 'UPDATE')"))!, column);
        Assert.False((bool)(await ScalarAsync(mig, "SELECT has_table_privilege('janus_runtime', 'public.\"Cards\"', 'UPDATE')"))!);

        var otherOrg = await CreateOrganizationAsync();
        var otherOrgId = await OrganizationIdAsync(otherOrg);

        // Runtime : refus par les droits (42501).
        Assert.Equal("42501", (await SqlFailsAsync(rt, $"UPDATE \"Cards\" SET \"OrganizationId\" = {otherOrgId} WHERE \"Uid\" = '{uid}'")).SqlState);
        Assert.Equal("42501", (await SqlFailsAsync(rt, $"UPDATE \"Cards\" SET \"Uid\" = '{uid}X' WHERE \"Uid\" = '{uid}'")).SqlState);
        // Propriétaire : refus par le trigger trg_protect_card_identity (23000), y compris pour une carte sans mouvement.
        Assert.Equal("23000", (await SqlFailsAsync(mig, $"UPDATE \"Cards\" SET \"OrganizationId\" = {otherOrgId} WHERE \"Uid\" = '{uid}'")).SqlState);
        Assert.Equal("23000", (await SqlFailsAsync(mig, $"UPDATE \"Cards\" SET \"Uid\" = '{uid}X' WHERE \"Uid\" = '{uid}'")).SqlState);
        // Les colonnes modifiables le restent.
        await ScalarAsync(rt, $"UPDATE \"Cards\" SET \"Label\" = 'ok' WHERE \"Uid\" = '{uid}'");
    }

    // ---------------------------------------------------------------- isolation and consistency

    [Fact]
    public async Task ISO01_TerminalOfAnotherOrganization_SeesForeignCardAsUnknown_WithoutDebit()
    {
        var foreignUid = await CreateCardAsync(null, 5000);
        var orgB = await CreateOrganizationAsync();
        var (code, token) = await EnrollAsync(orgB);
        var eventId = Guid.NewGuid();

        using var resp = await ValidateAsync(token, foreignUid, eventId);

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await JsonAsync(resp);
        Assert.Equal("REJECTED", body.GetProperty("decision").GetString());
        Assert.Equal("UNKNOWN_CARD", body.GetProperty("reason").GetString());

        using var db = _factory.CreateDbContext();
        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == foreignUid);
        Assert.Equal(5000, card.BalanceMillimes);
        Assert.False(await db.FareTransactions.AnyAsync(f => f.TerminalId == code));
        Assert.False(await db.WalletLedgerEntries.AnyAsync(e => e.IdempotencyKey == LedgerIdempotencyKeys.FareDebit(code, eventId)));
        Assert.Equal(await OrganizationIdAsync(orgB), (await db.Validations.AsNoTracking().SingleAsync(v => v.TerminalId == code)).OrganizationId);
    }

    [Fact]
    public async Task ISO02_SimulatorEndpoints_OnlyShowCardsOfTheTerminalOrganization()
    {
        var foreignUid = await CreateCardAsync(null);
        var orgB = await CreateOrganizationAsync();
        var ownUid = await CreateCardAsync(orgB);
        var (_, token) = await EnrollAsync(orgB);

        using var list = await _client.SendAsync(Authed(HttpMethod.Get, "/api/simulator/cards?limit=50", token));
        var listJson = await list.Content.ReadAsStringAsync();
        Assert.Contains(ownUid, listJson);
        Assert.DoesNotContain(foreignUid, listJson);

        using var foreign = await _client.SendAsync(Authed(HttpMethod.Get, $"/api/simulator/cards/{foreignUid}/presence", token));
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
        using var own = await _client.SendAsync(Authed(HttpMethod.Get, $"/api/simulator/cards/{ownUid}/presence", token));
        Assert.Equal(HttpStatusCode.OK, own.StatusCode);

        // L'administrateur de plateforme voit les deux.
        using var admin = await _client.SendAsync(Authed(HttpMethod.Get, $"/api/simulator/cards/{foreignUid}/presence", JanusApiFactory.AdminToken));
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
    }

    [Fact]
    public async Task ISO03_EveryRow_HasTheOrganizationOfItsCardAndTerminal()
    {
        // Scénario mixte sur deux organisations : débits, rejet inter-organisation, crédit, correction, révocation.
        var orgB = await CreateOrganizationAsync();
        var (codeB, tokenB) = await EnrollAsync(orgB);
        var uidA = await CreateCardAsync(null, 4000);
        var uidB = await CreateCardAsync(orgB, 4000);
        using (await ValidateAsync(tokenB, uidB)) { }
        using (await ValidateAsync(tokenB, uidA)) { }
        using (await ValidateAsync(JanusApiFactory.TerminalToken, uidA)) { }
        using (await _client.SendAsync(Authed(HttpMethod.Post, $"/api/cards/{uidB}/credits", JanusApiFactory.AdminToken, new { operationId = Guid.NewGuid(), amountMillimes = 1000, reason = "Recharge" }))) { }
        using (await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uidB}", JanusApiFactory.AdminToken, new { balanceMillimes = 100, expectedBalanceMillimes = 3500, reason = "Correction" }))) { }
        using (await RevokeAsync(codeB)) { }

        var mig = _factory.MigrationConnectionString;
        // Contrôle sur toute la base (pas seulement le scénario).
        // Validations : même organisation que la borne ; même organisation que la carte, sauf rejet UNKNOWN_CARD
        // (une borne qui présente la carte d'une autre organisation : c'est précisément l'isolation).
        var checks = new Dictionary<string, string>
        {
            ["Validations/terminal"] = "SELECT count(*) FROM \"Validations\" v JOIN \"Terminals\" t ON t.\"Code\" = v.\"TerminalId\" WHERE v.\"OrganizationId\" <> t.\"OrganizationId\"",
            ["Validations/card"] = "SELECT count(*) FROM \"Validations\" v JOIN \"Cards\" c ON c.\"Uid\" = v.\"CardUid\" WHERE v.\"Reason\" <> 'UNKNOWN_CARD' AND v.\"OrganizationId\" <> c.\"OrganizationId\"",
            ["FareTransactions"] = "SELECT count(*) FROM \"FareTransactions\" f JOIN \"Cards\" c ON c.\"Id\" = f.\"CardId\" WHERE f.\"OrganizationId\" <> c.\"OrganizationId\"",
            ["WalletLedgerEntries"] = "SELECT count(*) FROM \"WalletLedgerEntries\" e JOIN \"Cards\" c ON c.\"Id\" = e.\"CardId\" WHERE e.\"OrganizationId\" <> c.\"OrganizationId\"",
            ["CardAuditLogs"] = "SELECT count(*) FROM \"CardAuditLogs\" a JOIN \"Cards\" c ON c.\"Uid\" = a.\"CardUid\" WHERE a.\"OrganizationId\" <> c.\"OrganizationId\"",
            ["TerminalAuditLogs"] = "SELECT count(*) FROM \"TerminalAuditLogs\" a JOIN \"Terminals\" t ON t.\"Id\" = a.\"TerminalId\" WHERE a.\"OrganizationId\" <> t.\"OrganizationId\"",
        };
        foreach (var (name, sql) in checks)
            Assert.True(Convert.ToInt64(await ScalarAsync(mig, sql)) == 0, $"Organization mismatch in {name}");

        // Et le scénario a bien produit des lignes dans l'organisation B pour chaque table contrôlée.
        var orgBId = await OrganizationIdAsync(orgB);
        foreach (var table in new[] { "Validations", "FareTransactions", "WalletLedgerEntries", "CardAuditLogs", "TerminalAuditLogs" })
            Assert.True(Convert.ToInt64(await ScalarAsync(mig, $"SELECT count(*) FROM \"{table}\" WHERE \"OrganizationId\" = {orgBId}")) > 0, table);
    }

    [Fact]
    public async Task ISO04_CompositeForeignKey_RejectsLedgerRowInAnotherOrganizationThanItsCard()
    {
        var uid = await CreateCardAsync(null);
        var otherOrgId = await OrganizationIdAsync(await CreateOrganizationAsync());

        var ex = await SqlFailsAsync(_factory.RuntimeConnectionString, $@"
            INSERT INTO ""WalletLedgerEntries"" (""OrganizationId"", ""CardId"", ""CardUid"", ""EntryType"", ""AmountMillimes"", ""BalanceAfterMillimes"", ""IdempotencyKey"", ""Actor"", ""CreatedAt"")
            SELECT {otherOrgId}, ""Id"", ""Uid"", 'ADMIN_CREDIT', 100, 100, 'iso04:{Guid.NewGuid():N}', 'TEST', now() FROM ""Cards"" WHERE ""Uid"" = '{uid}'");

        Assert.Equal("23503", ex.SqlState);
        Assert.Equal("FK_WalletLedgerEntries_Cards_CardId_OrganizationId", ex.ConstraintName);
    }

    // ---------------------------------------------------------------- cards and organization

    [Fact]
    public async Task CRD01_CRD03_CRD04_CardOrganization_DefaultInDevelopment_NeverMoves_UnknownRejected()
    {
        var uid = await CreateCardAsync(null);
        using (var db = _factory.CreateDbContext())
            Assert.Equal(OrganizationDefaults.DemoOrganizationId, (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid)).OrganizationId);

        var other = await CreateOrganizationAsync();
        using (var mismatch = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken, new { label = "x", organizationCode = other })))
        {
            Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
            Assert.Equal("organization_mismatch", await ErrorAsync(mismatch));
        }
        using (var same = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", JanusApiFactory.AdminToken, new { label = "y", organizationCode = "demo-transit" })))
            Assert.Equal(HttpStatusCode.OK, same.StatusCode);
        using (var unknown = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{NewUid()}", JanusApiFactory.AdminToken, new { balanceMillimes = 0, organizationCode = "NOPE-" + Suffix() })))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            Assert.Equal("organization_not_found", await ErrorAsync(unknown));
        }
        using (var invalid = await _client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{NewUid()}", JanusApiFactory.AdminToken, new { balanceMillimes = 0, organizationCode = "bad code" })))
            Assert.Equal("invalid_organization_code", await ErrorAsync(invalid));
    }

    [Fact]
    public async Task CRD02_TRM12_Production_RequiresOrganizationAtCardCreation_AndIgnoresStaticTerminalTokens()
    {
        var role = "janus_prodtest_" + Guid.NewGuid().ToString("N")[..8];
        var secret = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));
        // Rôle de connexion dédié (secret aléatoire fort, membre de janus_runtime) : la garde de production refuse les mots de passe
        // de dev. Le secret est transmis en paramètre SQL, jamais concaténé (même pratique que PostgresLeastPrivilegeProvisioner).
        await using (var conn = new NpgsqlConnection(_factory.MigrationConnectionString))
        {
            await conn.OpenAsync();
            await using var tx = await conn.BeginTransactionAsync();
            await using (var set = new NpgsqlCommand("SELECT set_config('janus.test_role_secret', @secret, true)", conn, tx))
            {
                set.Parameters.AddWithValue("secret", secret);
                await set.ExecuteScalarAsync();
            }
            await using (var create = new NpgsqlCommand(
                $"DO $$ BEGIN EXECUTE format('CREATE ROLE %I LOGIN PASSWORD %L INHERIT IN ROLE janus_runtime', '{role}', current_setting('janus.test_role_secret')); END $$", conn, tx))
            {
                await create.ExecuteNonQueryAsync();
            }
            await tx.CommitAsync();
        }
        var adminToken = "prod-admin-" + Guid.NewGuid().ToString("N");
        var staticTerminalToken = "prod-static-terminal-" + Guid.NewGuid().ToString("N");
        try
        {
            using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Production");
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Database:SkipMigration"] = "true",
                    ["ConnectionStrings:JanusDb"] = new NpgsqlConnectionStringBuilder(_factory.RuntimeConnectionString) { Username = role, Password = secret }.ConnectionString,
                    ["ConnectionStrings:MigrationDb"] = "",
                    ["AdminAuth:DemoToken"] = adminToken,
                    ["MetricsAuth:ScrapeToken"] = "prod-scrape-" + Guid.NewGuid().ToString("N"),
                    ["TerminalAuth:DemoToken"] = staticTerminalToken,
                    ["Fare:AmountMillimes"] = "1500"
                }));
            });
            using var client = factory.CreateClient();

            // CRD02 : organizationCode obligatoire à la création hors Development.
            var uid = NewUid();
            using (var missing = await client.SendAsync(Authed(HttpMethod.Put, $"/api/cards/{uid}", adminToken, new { balanceMillimes = 1000, reason = "prod" })))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                Assert.Equal("organization_required", await ErrorAsync(missing));
            }
            using (var db = _factory.CreateDbContext())
                Assert.False(await db.Cards.AnyAsync(c => c.Uid == uid));

            var org = await CreateOrganizationAsync(client, adminToken);
            var created = await CreateCardAsync(org, 1000, client, adminToken);
            using (var db = _factory.CreateDbContext())
                Assert.Equal(await OrganizationIdAsync(org), (await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == created)).OrganizationId);

            // TRM12 : un jeton TerminalAuth:* non enrôlé n'authentifie rien en Production, et n'est pas enrôlé au démarrage.
            using (var resp = await client.SendAsync(Authed(HttpMethod.Post, "/api/validations", staticTerminalToken,
                       new { eventId = Guid.NewGuid(), cardUid = created, eventType = "TRANSPORT_VALIDATION" })))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
            }
            using (var db = _factory.CreateDbContext())
                Assert.False(await db.Terminals.AnyAsync(t => t.TokenHash == TerminalToken.ComputeHash(staticTerminalToken)));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ScalarAsync(_factory.MigrationConnectionString, $"DROP ROLE IF EXISTS {role}");
        }
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("3651")]
    [InlineData("abc")]
    public void CFG01_InvalidTokenLifetime_FailsAtStartup(string value)
    {
        using var factory = _factory.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Terminals:TokenLifetimeDays"] = value })));

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Terminals:TokenLifetimeDays", ex.ToString());
    }

    // ---------------------------------------------------------------- metrics and concurrency

    [Fact]
    public async Task MET01_Metrics_NeverContainTerminalCodesOrCardUids()
    {
        var org = await CreateOrganizationAsync();
        var (code, token) = await EnrollAsync(org);
        var uid = await CreateCardAsync(org);
        using (await ValidateAsync(token, uid)) { }
        using (await ValidateAsync(JanusApiFactory.TerminalToken, uid)) { }
        using (await RevokeAsync(code)) { }
        using (await ValidateAsync(token, uid)) { }

        using var resp = await _client.SendAsync(Authed(HttpMethod.Get, "/metrics", JanusApiFactory.AdminToken));
        var metrics = await resp.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("janus_validations_total", metrics);
        Assert.DoesNotContain("terminal_id", metrics);
        Assert.DoesNotContain(code, metrics);
        Assert.DoesNotContain("TERMINAL-DEMO-01", metrics);
        Assert.DoesNotContain(uid, metrics);
        Assert.Contains("reason=\"revoked_token\"", metrics);
    }

    [Fact]
    public async Task CON01_EveryRequestAuthenticatedAfterRevocationCommit_IsRefused()
    {
        var org = await CreateOrganizationAsync();
        var (code, token) = await EnrollAsync(org);
        var uid = await CreateCardAsync(org, 1_000_000);

        long revocationCommittedAt = long.MaxValue;
        var outcomes = new System.Collections.Concurrent.ConcurrentBag<(long StartedAt, HttpStatusCode Status, string? Error)>();

        async Task Hammer()
        {
            for (var i = 0; i < 15; i++)
            {
                var startedAt = Stopwatch.GetTimestamp();
                using var resp = await ValidateAsync(token, uid);
                outcomes.Add((startedAt, resp.StatusCode, resp.StatusCode == HttpStatusCode.OK ? null : await ErrorAsync(resp)));
            }
        }

        var load = Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Hammer()));
        await Task.Delay(20);
        using (var revoke = await RevokeAsync(code))
        {
            Assert.Equal(HttpStatusCode.OK, revoke.StatusCode);
            // La réponse 200 est écrite après COMMIT : tout ce qui démarre ensuite doit voir la révocation.
            Interlocked.Exchange(ref revocationCommittedAt, Stopwatch.GetTimestamp());
        }
        await load;

        var after = outcomes.Where(o => o.StartedAt > revocationCommittedAt).ToList();
        Assert.All(after, o =>
        {
            Assert.Equal(HttpStatusCode.Unauthorized, o.Status);
            Assert.Equal("revoked_token", o.Error);
        });
        // Les requêtes authentifiées avant la révocation peuvent avoir abouti (authentification avant la transaction financière).
        Assert.All(outcomes.Where(o => o.StartedAt <= revocationCommittedAt), o => Assert.Contains(o.Status, new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized }));

        // Après la charge : contrôle déterministe en parallèle.
        var final = await Task.WhenAll(Enumerable.Range(0, 10).Select(_ => ValidateAsync(token, uid)));
        Assert.All(final, r => Assert.Equal(HttpStatusCode.Unauthorized, r.StatusCode));
        foreach (var r in final) r.Dispose();
    }
}
