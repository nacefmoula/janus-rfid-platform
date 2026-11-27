using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace Janus.Api.Tests;

/// <summary>
/// LOT-3A — Correctifs de robustesse :
/// - FIN-05 : tarif obligatoire au démarrage (fail-fast) ;
/// - INP-01 : bornes d'entrée alignées sur le schéma (400 au lieu de 500) ;
/// - TIME-01 : horodatage d'audit issu du TimeProvider injecté (testable, déterministe).
/// </summary>
public class InputAndConfigurationRobustnessTests : IAsyncLifetime
{
    private readonly JanusApiFactory _factory = new();
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

    private static string NewUid() => "04RB" + Guid.NewGuid().ToString("N")[..10].ToUpperInvariant();

    private static HttpRequestMessage Upsert(string uid, object body)
    {
        var req = new HttpRequestMessage(HttpMethod.Put, $"/api/cards/{uid}") { Content = JsonContent.Create(body) };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JanusApiFactory.AdminToken);
        return req;
    }

    private static async Task<string?> ReadErrorCodeAsync(HttpResponseMessage response)
    {
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
    }

    /// <summary>
    /// Configuration de production valide en tout point SAUF le tarif, afin d'isoler la règle FIN-05.
    /// </summary>
    private static WebApplicationFactory<Program> ProductionFactoryWithFare(string? fare)
    {
        var values = new Dictionary<string, string?>
        {
            ["Database:SkipMigration"] = "true",
            ["ConnectionStrings:JanusDb"] = $"Host=localhost;Port=5433;Database=janus_db;Username=janus_runtime;Password=prod-rt-{Guid.NewGuid():N}",
            ["ConnectionStrings:MigrationDb"] = "",
            ["TerminalAuth:DemoToken"] = "prod-t1-" + Guid.NewGuid().ToString("N"),
            ["TerminalAuth:Terminal2Token"] = "prod-t2-" + Guid.NewGuid().ToString("N"),
            ["AdminAuth:DemoToken"] = "prod-adm-" + Guid.NewGuid().ToString("N"),
            ["MetricsAuth:ScrapeToken"] = "prod-scr-" + Guid.NewGuid().ToString("N"),
            ["Fare:AmountMillimes"] = fare
        };

        return new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(values));
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1500")]
    [InlineData("abc")]
    [InlineData("1500.5")]
    public void FIN05_Startup_RejectsMissingOrInvalidFare(string? fare)
    {
        using var factory = ProductionFactoryWithFare(fare);

        var ex = Assert.ThrowsAny<Exception>(() => factory.CreateClient());

        Assert.Contains("Fare:AmountMillimes", ex.ToString());
    }

    [Fact]
    public void FIN05_Startup_AcceptsStrictlyPositiveFare()
    {
        using var factory = ProductionFactoryWithFare("1500");

        using var client = factory.CreateClient();

        Assert.NotNull(client);
    }

    [Fact]
    public async Task INP01_Upsert_LabelTooLong_Returns400_AndPersistsNothing()
    {
        var uid = NewUid();

        using var resp = await _client.SendAsync(Upsert(uid, new { balanceMillimes = 1000, label = new string('L', 121), reason = "robustness" }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_label", await ReadErrorCodeAsync(resp));

        using var db = _factory.CreateDbContext();
        Assert.False(await db.Cards.AnyAsync(c => c.Uid == uid));
        Assert.False(await db.CardAuditLogs.AnyAsync(a => a.CardUid == uid));
    }

    [Fact]
    public async Task INP01_Upsert_LabelAtMaxLength_IsAccepted()
    {
        var uid = NewUid();

        using var resp = await _client.SendAsync(Upsert(uid, new { balanceMillimes = 1000, label = new string('L', 120), reason = "robustness" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task INP01_Upsert_ReasonTooLong_Returns400_AndLeavesExistingCardUntouched()
    {
        var uid = NewUid();
        using (var created = await _client.SendAsync(Upsert(uid, new { balanceMillimes = 4000, reason = "initial" })))
        {
            Assert.Equal(HttpStatusCode.OK, created.StatusCode);
        }

        using var resp = await _client.SendAsync(Upsert(uid, new { balanceMillimes = 9000, reason = new string('R', 257) }));

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_reason", await ReadErrorCodeAsync(resp));

        using var db = _factory.CreateDbContext();
        var card = await db.Cards.AsNoTracking().SingleAsync(c => c.Uid == uid);
        Assert.Equal(4000, card.BalanceMillimes);
        Assert.Equal(1, await db.CardAuditLogs.CountAsync(a => a.CardUid == uid));
    }

    [Fact]
    public async Task INP01_GetCard_UidTooLong_Returns400()
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"/api/cards/{new string('A', 65)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", JanusApiFactory.AdminToken);

        using var resp = await _client.SendAsync(req);

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("invalid_uid", await ReadErrorCodeAsync(resp));
    }

    [Fact]
    public async Task TIME01_AuditTimestamp_ComesFromInjectedTimeProvider()
    {
        var fixedInstant = new DateTimeOffset(2030, 1, 15, 8, 30, 0, TimeSpan.Zero);
        _factory.SimulatedTime.SetUtcNow(fixedInstant);
        var uid = NewUid();

        using var resp = await _client.SendAsync(Upsert(uid, new { balanceMillimes = 2500, reason = "clock" }));

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var db = _factory.CreateDbContext();
        var audit = await db.CardAuditLogs.AsNoTracking().SingleAsync(a => a.CardUid == uid);
        Assert.Equal(fixedInstant, audit.CreatedAt.ToUniversalTime());
    }
}
