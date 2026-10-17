using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Janus.Api.Tests;

public class SecurityHardeningTests : IAsyncLifetime
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

    private string TerminalToken => _factory.TerminalToken;
    private string Terminal02Token => _factory.Terminal02Token;
    private string AdminToken => _factory.AdminToken;
    private string ScrapeToken => _factory.ScrapeToken;

    private async Task<string> PrepareCardAsync(int balanceMillimes, CardStatus status = CardStatus.Active)
    {
        var uid = "04SEC" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        using var db = _factory.CreateDbContext();
        var card = new TransitCard
        {
            Uid = uid,
            Status = status,
            Label = $"Test Sec Card {uid}",
            BalanceMillimes = balanceMillimes
        };
        db.Cards.Add(card);
        await db.SaveChangesAsync();
        return uid;
    }

    [Fact]
    public async Task SEC01_DemoCards_AnonymousCall_Returns401Unauthorized()
    {
        // Act
        var response = await _client.GetAsync("/api/demo/cards");

        // Assert
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        var content = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("unauthorized", content.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SEC01_DemoCards_TerminalCaller_Returns403Forbidden()
    {
        // Act: Terminal 1
        using var req1 = new HttpRequestMessage(HttpMethod.Get, "/api/demo/cards");
        req1.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp1 = await _client.SendAsync(req1);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, resp1.StatusCode);
        var content1 = await resp1.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", content1.GetProperty("error").GetString());

        // Act: Terminal 2
        using var req2 = new HttpRequestMessage(HttpMethod.Get, "/api/demo/cards");
        req2.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Terminal02Token);
        var resp2 = await _client.SendAsync(req2);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, resp2.StatusCode);
    }

    [Fact]
    public async Task SEC01_DemoCards_AdminCaller_Returns200WithCardRegistry()
    {
        // Arrange
        await PrepareCardAsync(5000);

        // Act
        using var req = new HttpRequestMessage(HttpMethod.Get, "/api/demo/cards");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var response = await _client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var cards = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, cards.ValueKind);
        Assert.True(cards.GetArrayLength() > 0);
    }

    [Fact]
    public async Task SEC02_Terminal02_CanReadCard_AndValidateWithServerDeterminedIdentity()
    {
        // Arrange: Prepare test card
        var cardUid = await PrepareCardAsync(10000);

        // Act 1: Terminal 2 queries card details
        using var getReq = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}");
        getReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Terminal02Token);
        var getResp = await _client.SendAsync(getReq);

        // Assert 1: SEC-02 resolved — Terminal 2 is recognized and authenticated
        Assert.Equal(HttpStatusCode.OK, getResp.StatusCode);
        var cardBody = await getResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(cardUid, cardBody.GetProperty("uid").GetString());

        // Act 2: Terminal 2 submits a validation
        var eventId = Guid.NewGuid();
        using var valReq = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(new
            {
                eventId,
                cardUid,
                occurredAt = DateTimeOffset.UtcNow,
                eventType = "TRANSPORT_VALIDATION",
                terminalId = "CLIENT_ATTACKER_OVERRIDE" // Client tries to spoof terminal
            })
        };
        valReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Terminal02Token);
        var valResp = await _client.SendAsync(valReq);

        // Assert 2: TerminalId is determined strictly by the server token
        Assert.Equal(HttpStatusCode.OK, valResp.StatusCode);
        var valBody = await valResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("ACCEPTED", valBody.GetProperty("decision").GetString());
        Assert.Equal("TERMINAL-DEMO-02", valBody.GetProperty("terminalId").GetString());
    }

    [Fact]
    public async Task SEC02_Terminal_CannotPerformAdministrativeCardUpsert_Returns403()
    {
        // Arrange
        var cardUid = await PrepareCardAsync(2000);

        // Act
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{cardUid}")
        {
            Content = JsonContent.Create(new
            {
                balanceMillimes = 50000,
                status = "Active",
                label = "Hacked Card",
                reason = "Attacker attempted balance inflation"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var resp = await _client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.Forbidden, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("forbidden", body.GetProperty("error").GetString());
    }

    [Fact]
    public async Task SEC06_Admin_CardUpsert_RecordsAuthenticatedActorInAuditTrail()
    {
        // Arrange
        var cardUid = "04ACTOR" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

        // Act: Admin performs card creation/upsert
        using var req = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{cardUid}")
        {
            Content = JsonContent.Create(new
            {
                balanceMillimes = 7500,
                status = "Active",
                label = "Card with Verified Actor",
                reason = "Initial issuance"
            })
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var resp = await _client.SendAsync(req);

        // Assert
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Check DB audit record
        using var db = _factory.CreateDbContext();
        var audit = await db.CardAuditLogs.SingleOrDefaultAsync(a => a.CardUid == cardUid);
        Assert.NotNull(audit);
        Assert.Equal("ADMIN", audit.Actor);
        Assert.Equal(7500, audit.NewBalanceMillimes);
    }

    [Fact]
    public async Task SEC05_Metrics_Anonymous_Returns401_Terminal_Returns403_Admin_Returns200()
    {
        // 1. Anonymous request to /metrics -> 401 Unauthorized
        var anonResp = await _client.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.Unauthorized, anonResp.StatusCode);

        // 2. Terminal token to /metrics -> 403 Forbidden
        using var termReq = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        termReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var termResp = await _client.SendAsync(termReq);
        Assert.Equal(HttpStatusCode.Forbidden, termResp.StatusCode);

        // 3. Admin token to /metrics -> 200 OK
        using var adminReq = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        adminReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
        var adminResp = await _client.SendAsync(adminReq);
        Assert.Equal(HttpStatusCode.OK, adminResp.StatusCode);
        var rawMetrics = await adminResp.Content.ReadAsStringAsync();
        Assert.Contains("janus_validations_total", rawMetrics);
    }

    [Fact]
    public async Task REV01_ScraperRole_CanAccessMetrics_ButCannotAccessCardsAuditAdminValidationOrDashboard()
    {
        var cardUid = await PrepareCardAsync(4000);

        // 1. Scraper token to /metrics -> 200 OK
        using var metricsReq = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        metricsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        var metricsResp = await _client.SendAsync(metricsReq);
        Assert.Equal(HttpStatusCode.OK, metricsResp.StatusCode);
        var rawMetrics = await metricsResp.Content.ReadAsStringAsync();
        Assert.Contains("janus_validations_total", rawMetrics);

        // 2. Scraper cannot read cards -> 403 Forbidden
        using var cardReadReq = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}");
        cardReadReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(cardReadReq)).StatusCode);

        // 3. Scraper cannot upsert/modify cards -> 403 Forbidden
        using var cardUpsertReq = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{cardUid}")
        {
            Content = JsonContent.Create(new { balanceMillimes = 99999, status = "Active", label = "Scraper Attempt" })
        };
        cardUpsertReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(cardUpsertReq)).StatusCode);

        // 4. Scraper cannot consult card audit -> 403 Forbidden
        using var auditReq = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit");
        auditReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(auditReq)).StatusCode);

        // 5. Scraper cannot access admin demo cards -> 403 Forbidden
        using var demoCardsReq = new HttpRequestMessage(HttpMethod.Get, "/api/demo/cards");
        demoCardsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(demoCardsReq)).StatusCode);

        // 6. Scraper cannot access simulator cards or dashboard summary -> 403 Forbidden
        using var simCardsReq = new HttpRequestMessage(HttpMethod.Get, "/api/simulator/cards");
        simCardsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(simCardsReq)).StatusCode);

        using var dashReq = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/summary");
        dashReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(dashReq)).StatusCode);

        // 7. Scraper cannot submit validations -> 403 Forbidden
        using var valReq = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(new
            {
                eventId = Guid.NewGuid(),
                cardUid,
                occurredAt = DateTimeOffset.UtcNow,
                eventType = "TRANSPORT_VALIDATION"
            })
        };
        valReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(valReq)).StatusCode);
    }

    [Fact]
    public async Task REV02_DashboardSummary_And_SimulatorCards_EnforceLeastPrivilegeAndReturnSafePayloads()
    {
        var cardUid = await PrepareCardAsync(6000);

        // 1. Anonymous calls -> 401 Unauthorized
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/dashboard/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await _client.GetAsync("/api/simulator/cards")).StatusCode);

        // 2. Terminal 1 can read /api/dashboard/summary without admin or scrape token
        using var dashReq = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/summary");
        dashReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var dashResp = await _client.SendAsync(dashReq);
        Assert.Equal(HttpStatusCode.OK, dashResp.StatusCode);
        var dashJson = await dashResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.True(dashJson.TryGetProperty("activeCardsCount", out var activeCards) && activeCards.GetInt32() >= 1);
        Assert.True(dashJson.TryGetProperty("totalValidations", out _));
        Assert.True(dashJson.TryGetProperty("acceptedValidations", out _));
        Assert.True(dashJson.TryGetProperty("rejectedValidations", out _));
        Assert.True(dashJson.TryGetProperty("conflictValidations", out _));
        Assert.True(dashJson.TryGetProperty("totalDebitedMillimes", out _));

        // 3. Terminal 1 and Terminal 2 can read /api/simulator/cards with minimal fields only (no balanceMillimes)
        using var simReq = new HttpRequestMessage(HttpMethod.Get, "/api/simulator/cards?limit=1&offset=0");
        simReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Terminal02Token);
        var simResp = await _client.SendAsync(simReq);
        Assert.Equal(HttpStatusCode.OK, simResp.StatusCode);
        var simCards = await simResp.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(JsonValueKind.Array, simCards.ValueKind);
        Assert.Equal(1, simCards.GetArrayLength());

        var firstCard = simCards[0];
        Assert.True(firstCard.TryGetProperty("uid", out _));
        Assert.True(firstCard.TryGetProperty("status", out _));
        Assert.True(firstCard.TryGetProperty("label", out _));
        // Financial balance and internal DB entity fields must not be exposed on simulator endpoint (FIN-02)
        Assert.False(firstCard.TryGetProperty("balanceMillimes", out _));
        Assert.False(firstCard.TryGetProperty("id", out _));
        Assert.False(firstCard.TryGetProperty("rowVersion", out _));
        Assert.False(firstCard.TryGetProperty("updatedAt", out _));
        Assert.False(firstCard.TryGetProperty("transactions", out _));
        Assert.False(firstCard.TryGetProperty("auditLogs", out _));
    }

    [Fact]
    public async Task SEC04_SecurityMetrics_RecordedOnInvalidAuthenticationAndForbiddenAccess()
    {
        // Act 1: Send bad token to trigger authentication failure
        using var badAuthReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04TEST");
        badAuthReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "completely-invalid-token");
        var badAuthResp = await _client.SendAsync(badAuthReq);
        Assert.Equal(HttpStatusCode.Unauthorized, badAuthResp.StatusCode);

        // Act 2: Send terminal token to admin audit endpoint to trigger authorization denial
        using var forbiddenReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04TEST/audit");
        forbiddenReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
        var forbiddenResp = await _client.SendAsync(forbiddenReq);
        Assert.Equal(HttpStatusCode.Forbidden, forbiddenResp.StatusCode);

        // Act 3: Read metrics via dedicated scraper credential
        using var metricsReq = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        metricsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        var metricsResp = await _client.SendAsync(metricsReq);
        var metricsText = await metricsResp.Content.ReadAsStringAsync();

        // Assert: Both security counters exist and have recorded values
        Assert.Contains("janus_security_auth_failures_total", metricsText);
        Assert.Contains("janus_security_authz_denials_total", metricsText);
    }

    [Fact]
    public async Task REV07_SecurityTelemetry_DistinguishesMissing_NonBearer_Malformed_And_InvalidToken()
    {
        // 1. Missing token (no Authorization header)
        var missingResp = await _client.GetAsync("/api/cards/04TEST");
        Assert.Equal(HttpStatusCode.Unauthorized, missingResp.StatusCode);

        // 2. Non-Bearer scheme (Basic)
        using var basicReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04TEST");
        basicReq.Headers.Authorization = new AuthenticationHeaderValue("Basic", "dGVzdDp0ZXN0");
        var basicResp = await _client.SendAsync(basicReq);
        Assert.Equal(HttpStatusCode.Unauthorized, basicResp.StatusCode);

        // 3. Malformed Bearer header (empty token and token with whitespace)
        using var emptyBearerReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04TEST");
        emptyBearerReq.Headers.TryAddWithoutValidation("Authorization", "Bearer   ");
        var emptyBearerResp = await _client.SendAsync(emptyBearerReq);
        Assert.Equal(HttpStatusCode.Unauthorized, emptyBearerResp.StatusCode);

        using var spaceBearerReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04TEST");
        spaceBearerReq.Headers.TryAddWithoutValidation("Authorization", "Bearer token with spaces");
        var spaceBearerResp = await _client.SendAsync(spaceBearerReq);
        Assert.Equal(HttpStatusCode.Unauthorized, spaceBearerResp.StatusCode);

        // 4. Invalid Bearer token
        using var invalidBearerReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04TEST");
        invalidBearerReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "unknown-secret-token");
        var invalidBearerResp = await _client.SendAsync(invalidBearerReq);
        Assert.Equal(HttpStatusCode.Unauthorized, invalidBearerResp.StatusCode);

        // Read Prometheus metrics via ScrapeToken
        using var metricsReq = new HttpRequestMessage(HttpMethod.Get, "/metrics");
        metricsReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ScrapeToken);
        var metricsResp = await _client.SendAsync(metricsReq);
        var metricsText = await metricsResp.Content.ReadAsStringAsync();

        Assert.Contains("reason=\"missing_token\"", metricsText);
        Assert.Contains("reason=\"non_bearer\"", metricsText);
        Assert.Contains("reason=\"malformed_header\"", metricsText);
        Assert.Contains("reason=\"invalid_token\"", metricsText);
        Assert.DoesNotContain("unknown-secret-token", metricsText);
    }

    [Fact]
    public async Task SEC07_RateLimiting_TooManyRequests_Returns429()
    {
        // Factory with strict admin rate limit of 2 requests
        using var tightLimiterFactory = new JanusApiFactory();
        using var tightClient = tightLimiterFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:AdminPermitLimit"] = "2"
                });
            });
        }).CreateClient();

        var cardUid = await PrepareCardAsync(1000);
        var statuses = new List<HttpStatusCode>();

        for (int i = 0; i < 10; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{cardUid}/audit");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", AdminToken);
            var resp = await tightClient.SendAsync(req);
            statuses.Add(resp.StatusCode);
        }

        // Assert: At least one request was throttled with 429 Too Many Requests
        Assert.Contains(HttpStatusCode.TooManyRequests, statuses);
    }

    [Fact]
    public async Task REV03_RateLimiting_IsolatesTerminals_And_PreventsAnonymousExhaustion()
    {
        using var isolatedFactory = new JanusApiFactory();
        isolatedFactory.AntiPassbackEnabledOverride = false;
        using var client = isolatedFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["RateLimiting:AnonymousPermitLimit"] = "2",
                    ["RateLimiting:ValidationsPermitLimit"] = "2",
                    ["RateLimiting:ValidationsBurstLimit"] = "2"
                });
            });
        }).CreateClient();

        var cardUid = await PrepareCardAsync(20000);

        // Step 1: Flood with anonymous requests trying to spoof X-Forwarded-For
        var anonStatuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            using var anonReq = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
            {
                Content = JsonContent.Create(new
                {
                    eventId = Guid.NewGuid(),
                    cardUid,
                    occurredAt = DateTimeOffset.UtcNow,
                    eventType = "TRANSPORT_VALIDATION"
                })
            };
            anonReq.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{i + 10}");
            var anonResp = await client.SendAsync(anonReq);
            anonStatuses.Add(anonResp.StatusCode);
        }
        // Anonymous flood is throttled by the pre-auth GlobalLimiter (429) or rejected by auth (401)
        Assert.Contains(HttpStatusCode.TooManyRequests, anonStatuses);

        // Step 2: Terminal 1 is unaffected by the anonymous flood and can validate up to its own burst limit (2)
        var term1Statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            using var term1Req = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
            {
                Content = JsonContent.Create(new
                {
                    eventId = Guid.NewGuid(),
                    cardUid,
                    occurredAt = DateTimeOffset.UtcNow,
                    eventType = "TRANSPORT_VALIDATION"
                })
            };
            term1Req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", TerminalToken);
            var term1Resp = await client.SendAsync(term1Req);
            term1Statuses.Add(term1Resp.StatusCode);
        }
        Assert.Contains(HttpStatusCode.OK, term1Statuses);
        Assert.Contains(HttpStatusCode.TooManyRequests, term1Statuses);

        // Step 3: Even while Terminal 1 is rate-limited (429), Terminal 2 has an independent partition and succeeds (200 OK)
        using var term2Req = new HttpRequestMessage(HttpMethod.Post, "/api/validations")
        {
            Content = JsonContent.Create(new
            {
                eventId = Guid.NewGuid(),
                cardUid,
                occurredAt = DateTimeOffset.UtcNow,
                eventType = "TRANSPORT_VALIDATION"
            })
        };
        term2Req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Terminal02Token);
        var term2Resp = await client.SendAsync(term2Req);
        Assert.Equal(HttpStatusCode.OK, term2Resp.StatusCode);
    }

    [Fact]
    public void SEC03_StartupValidation_RejectsDefaultTokens_InProduction()
    {
        using var prodFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:JanusDb"] = "Host=localhost;Database=dummy",
                    ["TerminalAuth:DemoToken"] = "dev-terminal-token-change-me", // Known dev secret
                    ["AdminAuth:DemoToken"] = "dev-admin-token-change-me",
                    ["MetricsAuth:ScrapeToken"] = "dev-metrics-scrape-token-change-me"
                });
            });
        });

        // Assert: Starting production with default tokens throws InvalidOperationException without leaking secrets
        var ex = Assert.ThrowsAny<Exception>(() => prodFactory.CreateClient());
        Assert.Contains("Production configuration error", ex.ToString());
        Assert.DoesNotContain("dev-terminal-token-change-me", ex.Message);
    }

    [Fact]
    public void REV01_StartupValidation_RejectsInvalidScrapeToken_OrDuplicateTokens_InStagingAndProduction()
    {
        var strongTerm1 = "prod-term1-" + Guid.NewGuid().ToString("N");
        var strongTerm2 = "prod-term2-" + Guid.NewGuid().ToString("N");
        var strongAdmin = "prod-admin-" + Guid.NewGuid().ToString("N");

        // Case 1: Missing/default ScrapeToken in Staging throws configuration error
        using var stagingBadScrape = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:JanusDb"] = "Host=localhost;Database=dummy",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = "dev-metrics-scrape-token-change-me"
                });
            });
        });
        var exStaging = Assert.ThrowsAny<Exception>(() => stagingBadScrape.CreateClient());
        Assert.Contains("MetricsAuth:ScrapeToken", exStaging.ToString());
        Assert.DoesNotContain("dev-metrics-scrape-token-change-me", exStaging.Message);

        // Case 2: Duplicate token between AdminAuth:DemoToken and MetricsAuth:ScrapeToken throws error
        using var duplicateTokenFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:JanusDb"] = "Host=localhost;Database=dummy",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = strongTerm2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongAdmin // Reused admin token!
                });
            });
        });
        var exDup = Assert.ThrowsAny<Exception>(() => duplicateTokenFactory.CreateClient());
        Assert.Contains("Distinct tokens are required", exDup.ToString());
        Assert.DoesNotContain(strongAdmin, exDup.Message);
    }

    [Fact]
    public void SEC03_StartupValidation_AcceptsStrongTokens_InProduction()
    {
        // Generate strong 32+ character distinct secrets without mutating process environment variables
        var strongTerminal1 = "prod-t1-" + Guid.NewGuid().ToString("N");
        var strongTerminal2 = "prod-t2-" + Guid.NewGuid().ToString("N");
        var strongAdmin = "prod-adm-" + Guid.NewGuid().ToString("N");
        var strongScrape = "prod-scr-" + Guid.NewGuid().ToString("N");
        var dbConn = "Host=localhost;Port=5433;Database=janus_db;Username=janus_app;Password=local-dev-db-password-change-me";

        using var prodFactory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:JanusDb"] = dbConn,
                    ["TerminalAuth:DemoToken"] = strongTerminal1,
                    ["TerminalAuth:Terminal2Token"] = strongTerminal2,
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape,
                    ["Fare:AmountMillimes"] = "1500"
                });
            });
        });

        // Act & Assert: Starts successfully without configuration error
        using var client = prodFactory.CreateClient();
        Assert.NotNull(client);
    }

    [Fact]
    public void FIN04_StartupValidation_RejectsMissingOrDefaultTerminal2Token_InProductionAndStaging()
    {
        var strongTerm1 = "prod-term1-" + Guid.NewGuid().ToString("N");
        var strongAdmin = "prod-admin-" + Guid.NewGuid().ToString("N");
        var strongScrape = "prod-scrape-" + Guid.NewGuid().ToString("N");

        // Case 1: Missing/empty Terminal2Token in Production throws configuration error
        using var missingTerm2Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:JanusDb"] = "Host=localhost;Database=dummy",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = "",
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });
        var exMissing = Assert.ThrowsAny<Exception>(() => missingTerm2Factory.CreateClient());
        Assert.Contains("TerminalAuth:Terminal2Token", exMissing.ToString());

        // Case 2: Default dev Terminal2Token in Staging throws configuration error without leaking value
        using var defaultTerm2Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Staging");
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:JanusDb"] = "Host=localhost;Database=dummy",
                    ["TerminalAuth:DemoToken"] = strongTerm1,
                    ["TerminalAuth:Terminal2Token"] = "dev-terminal-02-token-change-me",
                    ["AdminAuth:DemoToken"] = strongAdmin,
                    ["MetricsAuth:ScrapeToken"] = strongScrape
                });
            });
        });
        var exDefault = Assert.ThrowsAny<Exception>(() => defaultTerm2Factory.CreateClient());
        Assert.Contains("TerminalAuth:Terminal2Token", exDefault.ToString());
        Assert.DoesNotContain("dev-terminal-02-token-change-me", exDefault.Message);
    }

    [Fact]
    public async Task FIN01_TrustedProxy_ForwardedHeaders_IsolatesLegitimateClientsBehindNginx_And_BlocksSpoofing()
    {
        using var proxyFactory = new JanusApiFactory();
        using var client = proxyFactory.WithWebHostBuilder(builder =>
        {
            builder.ConfigureAppConfiguration((_, config) =>
            {
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReverseProxy:TrustedProxies"] = "172.28.0.10",
                    ["RateLimiting:AnonymousPermitLimit"] = "2"
                });
            });
            builder.ConfigureServices(services =>
            {
                services.AddTransient<IStartupFilter, SimulatedRemoteIpStartupFilter>();
            });
        }).CreateClient();

        // 1. Anonymous attacker (198.51.100.10) behind trusted Nginx proxy (172.28.0.10) tries to spoof an upstream X-Forwarded-For hop
        var attackerStatuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04A1B2C3D4E5F6");
            req.Headers.TryAddWithoutValidation("X-Test-Remote-Ip", "172.28.0.10");
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", $"203.0.113.{i + 1}, 198.51.100.10");
            var resp = await client.SendAsync(req);
            attackerStatuses.Add(resp.StatusCode);
        }
        // ForwardLimit=1 ignores the forged 203.0.113.x hop and throttles 198.51.100.10 with 429
        Assert.Contains(HttpStatusCode.TooManyRequests, attackerStatuses);

        // 2. Another legitimate client (198.51.100.20) behind the same Nginx proxy (172.28.0.10) is NOT throttled by 198.51.100.10's flood
        using var otherClientReq = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04A1B2C3D4E5F6");
        otherClientReq.Headers.TryAddWithoutValidation("X-Test-Remote-Ip", "172.28.0.10");
        otherClientReq.Headers.TryAddWithoutValidation("X-Forwarded-For", "198.51.100.20");
        var otherClientResp = await client.SendAsync(otherClientReq);
        Assert.Equal(HttpStatusCode.Unauthorized, otherClientResp.StatusCode);

        // 3. Direct caller not coming from 172.28.0.10 (e.g. Docker gateway 172.28.0.1) cannot spoof X-Forwarded-For
        var directSpoofStatuses = new List<HttpStatusCode>();
        for (int i = 0; i < 6; i++)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/api/cards/04A1B2C3D4E5F6");
            req.Headers.TryAddWithoutValidation("X-Test-Remote-Ip", "172.28.0.1");
            req.Headers.TryAddWithoutValidation("X-Forwarded-For", $"198.51.100.{i + 50}");
            var resp = await client.SendAsync(req);
            directSpoofStatuses.Add(resp.StatusCode);
        }
        Assert.Contains(HttpStatusCode.TooManyRequests, directSpoofStatuses);
    }

    private sealed class SimulatedRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            return app =>
            {
                app.Use(async (ctx, nextMiddleware) =>
                {
                    if (ctx.Request.Headers.TryGetValue("X-Test-Remote-Ip", out var ipHeader) &&
                        IPAddress.TryParse(ipHeader.ToString(), out var parsedIp))
                    {
                        ctx.Connection.RemoteIpAddress = parsedIp;
                    }
                    await nextMiddleware();
                });
                next(app);
            };
        }
    }
}
