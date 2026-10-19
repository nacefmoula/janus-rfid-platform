using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Janus.Api;
using Janus.Api.Security;

static int GetRateLimitConfig(IConfiguration config, string primaryKey, string envFallbackKey, int defaultValue)
{
    var raw = config[primaryKey] ?? config[envFallbackKey];
    return int.TryParse(raw, out var val) && val > 0 ? val : defaultValue;
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<JanusDbContext>((sp, o) =>
    o.UseNpgsql(sp.GetRequiredService<IConfiguration>().GetConnectionString("JanusDb")));
builder.Services.AddSingleton(TimeProvider.System);

// REV-03: Configuration stricte de ForwardedHeaders (uniquement proxies et réseaux explicitement approuvés)
builder.Services.AddOptions<ForwardedHeadersOptions>().Configure<IConfiguration>((options, config) =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownProxies.Clear();
    options.KnownIPNetworks.Clear();

    var trustedProxies = config["ReverseProxy:TrustedProxies"];
    if (!string.IsNullOrWhiteSpace(trustedProxies))
    {
        foreach (var ipStr in trustedProxies.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(ipStr, out var ip))
                options.KnownProxies.Add(ip);
        }
    }

    var trustedNetworks = config["ReverseProxy:TrustedNetworks"];
    if (!string.IsNullOrWhiteSpace(trustedNetworks))
    {
        foreach (var cidr in trustedNetworks.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = cidr.Split('/');
            if (parts.Length == 2 && IPAddress.TryParse(parts[0], out var prefix) && int.TryParse(parts[1], out var prefixLen))
                options.KnownIPNetworks.Add(new System.Net.IPNetwork(prefix, prefixLen));
        }
    }
});

// SEC-07 / REV-03 / REV-04: Rate Limiting à deux niveaux (pré-auth par source réseau fiable pour anonymes + post-auth par identité authentifiée)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.ContentType = "application/json";
        await context.HttpContext.Response.WriteAsync(
            JsonSerializer.Serialize(new { error = "rate_limit_exceeded", message = "Too many requests. Please retry later." }),
            token);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        // Les requêtes authentifiées sont gouvernées par leurs politiques dédiées par identité (ne partagent jamais le quota anonyme)
        if (httpContext.User.Identity?.IsAuthenticated == true)
        {
            return RateLimitPartition.GetNoLimiter("authenticated");
        }

        var remoteIp = httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown_ip";
        return RateLimitPartition.GetSlidingWindowLimiter($"anon:{remoteIp}", _ =>
        {
            var cfg = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var anonLimit = GetRateLimitConfig(cfg, "RateLimiting:AnonymousPermitLimit", "ANONYMOUS_PERMIT_LIMIT", 30);
            return new SlidingWindowRateLimiterOptions
            {
                PermitLimit = anonLimit,
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 2,
                QueueLimit = 0,
                AutoReplenishment = true
            };
        });
    });

    // Paramètres initiaux calibrés vis-à-vis du pool Npgsql par défaut (MaxPoolSize=100) :
    // - Validations : 30 req/s soutenu par borne (burst de 60 jetons pour reprise de buffer hors-ligne)
    // - Admin : 15 req/s par identité administrative
    // - Lectures / observation : 60 req/s par identité authentifiée
    options.AddPolicy("ValidationsPolicy", httpContext =>
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return RateLimitPartition.GetNoLimiter("unauth_pass_to_authz");
        }

        var partitionKey = $"terminal:{httpContext.User.Identity.Name ?? "unknown_terminal"}";
        return RateLimitPartition.GetTokenBucketLimiter(partitionKey, _ =>
        {
            var cfg = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var valLimit = GetRateLimitConfig(cfg, "RateLimiting:ValidationsPermitLimit", "VALIDATIONS_PERMIT_LIMIT", 30);
            var valBurst = GetRateLimitConfig(cfg, "RateLimiting:ValidationsBurstLimit", "VALIDATIONS_BURST_LIMIT", valLimit * 2);
            return new TokenBucketRateLimiterOptions
            {
                TokenLimit = Math.Max(valLimit, valBurst),
                TokensPerPeriod = valLimit,
                ReplenishmentPeriod = TimeSpan.FromSeconds(1),
                QueueLimit = 0,
                AutoReplenishment = true
            };
        });
    });

    options.AddPolicy("AdminPolicy", httpContext =>
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return RateLimitPartition.GetNoLimiter("unauth_pass_to_authz");
        }

        var partitionKey = $"admin:{httpContext.User.Identity.Name ?? "unknown_admin"}";
        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ =>
        {
            var cfg = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var adminLimit = GetRateLimitConfig(cfg, "RateLimiting:AdminPermitLimit", "ADMIN_PERMIT_LIMIT", 15);
            return new SlidingWindowRateLimiterOptions
            {
                PermitLimit = adminLimit,
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 2,
                QueueLimit = 0,
                AutoReplenishment = true
            };
        });
    });

    options.AddPolicy("CardsReadPolicy", httpContext =>
    {
        if (httpContext.User.Identity?.IsAuthenticated != true)
        {
            return RateLimitPartition.GetNoLimiter("unauth_pass_to_authz");
        }

        var partitionKey = $"read:{httpContext.User.Identity.Name ?? "unknown_reader"}";
        return RateLimitPartition.GetSlidingWindowLimiter(partitionKey, _ =>
        {
            var cfg = httpContext.RequestServices.GetRequiredService<IConfiguration>();
            var cardsReadLimit = GetRateLimitConfig(cfg, "RateLimiting:CardsReadPermitLimit", "CARDS_READ_PERMIT_LIMIT", 60);
            return new SlidingWindowRateLimiterOptions
            {
                PermitLimit = cardsReadLimit,
                Window = TimeSpan.FromSeconds(1),
                SegmentsPerWindow = 2,
                QueueLimit = 0,
                AutoReplenishment = true
            };
        });
    });
});

// SEC-02: Authentification et Autorisation centralisées ASP.NET Core
builder.Services.AddAuthentication(JanusTokenAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, JanusTokenAuthHandler>(JanusTokenAuthHandler.SchemeName, null);

builder.Services.AddAuthorizationBuilder()
    .AddPolicy("TerminalOnly", p => p.RequireRole("Terminal").RequireClaim("permission", "validations:create"))
    .AddPolicy("AdminOnly", p => p.RequireRole("Admin").RequireClaim("permission", "cards:admin"))
    .AddPolicy("CardsRead", p => p.RequireAssertion(ctx => ctx.User.HasClaim("permission", "cards:read")))
    .AddPolicy("DashboardRead", p => p.RequireAssertion(ctx => ctx.User.HasClaim("permission", "dashboard:read") || ctx.User.HasClaim("permission", "cards:read")))
    .AddPolicy("AuditRead", p => p.RequireRole("Admin").RequireClaim("permission", "audit:read"))
    .AddPolicy("MetricsScrape", p => p.RequireAssertion(ctx => ctx.User.HasClaim("permission", "metrics:scrape") || ctx.User.IsInRole("Admin")));

builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, JanusAuthorizationMiddlewareResultHandler>();

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

// Validation centralisée de la configuration au démarrage (SEC-03 / REV-01 / REV-05 / REV-07)
var startupFareConfig = app.Configuration["Fare:AmountMillimes"];
if (!string.IsNullOrEmpty(startupFareConfig) && (!int.TryParse(startupFareConfig, out var startupFare) || startupFare <= 0))
    throw new InvalidOperationException($"Invalid Fare:AmountMillimes '{startupFareConfig}'. Must be a strictly positive integer.");

var startupApbWindow = app.Configuration["AntiPassback:WindowSeconds"];
if (!string.IsNullOrEmpty(startupApbWindow)) {
    if (!int.TryParse(startupApbWindow, out var startupWindow) || startupWindow <= 0)
        throw new InvalidOperationException($"Invalid AntiPassback:WindowSeconds '{startupApbWindow}'. Must be a strictly positive integer.");
}

var isDev = app.Environment.IsDevelopment();
var terminalToken = app.Configuration["TerminalAuth:DemoToken"];
var terminal2Token = app.Configuration["TerminalAuth:Terminal2Token"];
var adminToken = app.Configuration["AdminAuth:DemoToken"];
var scrapeToken = app.Configuration["MetricsAuth:ScrapeToken"];

string[] knownDevTokens = [
    "dev-terminal-token-change-me",
    "dev-admin-token-change-me",
    "dev-terminal-02-token-change-me",
    "dev-metrics-scrape-token-change-me"
];

if (!isDev)
{
    if (string.IsNullOrWhiteSpace(terminalToken) || terminalToken.Length < 16 || knownDevTokens.Contains(terminalToken))
        throw new InvalidOperationException("Production configuration error: 'TerminalAuth:DemoToken' must be configured with a non-default secret (minimum 16 characters) in non-development environments.");

    if (string.IsNullOrWhiteSpace(terminal2Token) || terminal2Token.Length < 16 || knownDevTokens.Contains(terminal2Token))
        throw new InvalidOperationException("Production configuration error: 'TerminalAuth:Terminal2Token' must be configured with a non-default secret (minimum 16 characters) in non-development environments.");

    if (string.IsNullOrWhiteSpace(adminToken) || adminToken.Length < 16 || knownDevTokens.Contains(adminToken))
        throw new InvalidOperationException("Production configuration error: 'AdminAuth:DemoToken' must be configured with a non-default secret (minimum 16 characters) in non-development environments.");

    if (string.IsNullOrWhiteSpace(scrapeToken) || scrapeToken.Length < 16 || knownDevTokens.Contains(scrapeToken))
        throw new InvalidOperationException("Production configuration error: 'MetricsAuth:ScrapeToken' must be configured with a non-default secret (minimum 16 characters) in non-development environments.");
}

// Vérification systématique d'unicité de tous les jetons configurés (évite toute collision de rôle Admin/Scraper/Terminal)
var configuredTokens = new[] { terminalToken, terminal2Token, adminToken, scrapeToken }
    .Where(t => !string.IsNullOrWhiteSpace(t))
    .Select(t => t!.Trim())
    .ToList();

if (configuredTokens.Count != configuredTokens.Distinct(StringComparer.Ordinal).Count())
{
    throw new InvalidOperationException("Security configuration error: Distinct tokens are required across Terminal1, Terminal2, Admin, and MetricsScrape.");
}

// REV-03: Ordre strict du pipeline : Routing -> ForwardedHeaders (si proxies/réseaux de confiance configurés) -> Authentication -> RateLimiter -> Authorization
app.UseRouting();
if (!string.IsNullOrWhiteSpace(app.Configuration["ReverseProxy:TrustedProxies"]) ||
    !string.IsNullOrWhiteSpace(app.Configuration["ReverseProxy:TrustedNetworks"]))
{
    app.UseWhen(ctx => ctx.Connection.RemoteIpAddress is not null, branch => branch.UseForwardedHeaders());
}
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

app.MapPrometheusScrapingEndpoint()
    .RequireAuthorization("MetricsScrape")
    .RequireRateLimiting("CardsReadPolicy");

await using (var scope = app.Services.CreateAsyncScope()) {
    var db = scope.ServiceProvider.GetRequiredService<JanusDbContext>();
    for (int attempt = 0; ; attempt++) {
        try {
            await db.Database.MigrateAsync();
            // REV-07: Le provisionnement automatique des cartes de démonstration est strictement limité à Development
            if (app.Environment.IsDevelopment() && !await db.Cards.AnyAsync()) {
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

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "janus-api" }))
    .AllowAnonymous();

// PAG-01: Endpoint administratif paginé pour l'inventaire des cartes (avec filtres recherche et statut, tri déterministe)
app.MapGet("/api/cards", async (HttpRequest http, JanusDbContext db) => {
    int page = 1;
    if (http.Query.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
        page = Math.Max(1, parsedPage);

    int pageSize = 25;
    if (http.Query.TryGetValue("pageSize", out var sizeStr) && int.TryParse(sizeStr, out var parsedSize))
        pageSize = Math.Clamp(parsedSize, 1, 100);

    var query = db.Cards.AsNoTracking();

    if (http.Query.TryGetValue("status", out var statusStr) && !string.IsNullOrWhiteSpace(statusStr)) {
        var statusVal = statusStr.ToString().Trim();
        if (!statusVal.Equals("ALL", StringComparison.OrdinalIgnoreCase)) {
            if (Enum.TryParse<CardStatus>(statusVal, true, out var parsedStatus)) {
                query = query.Where(c => c.Status == parsedStatus);
            } else {
                return Results.BadRequest(new { error = "invalid_status", message = "Invalid status filter. Allowed values: Active, Revoked, ALL." });
            }
        }
    }

    if (http.Query.TryGetValue("q", out var qStr) && !string.IsNullOrWhiteSpace(qStr)) {
        var q = qStr.ToString().Trim().ToUpperInvariant();
        query = query.Where(c => c.Uid.ToUpper().Contains(q) || c.Label.ToUpper().Contains(q));
    }

    var totalCount = await query.CountAsync();
    var totalPages = totalCount == 0 ? 1 : (int)Math.Ceiling((double)totalCount / pageSize);

    var offset = checked((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue));

    var items = await query
        .OrderBy(c => c.Uid)
        .Skip(offset)
        .Take(pageSize)
        .Select(c => new AdminTransitCardDto(
            c.Uid,
            c.Status.ToString(),
            c.Label,
            c.BalanceMillimes
        ))
        .ToListAsync();

    return Results.Ok(new PagedResult<AdminTransitCardDto>(
        items,
        page,
        pageSize,
        totalCount,
        totalPages
    ));
})
    .RequireAuthorization("AdminOnly")
    .RequireRateLimiting("AdminPolicy");

app.MapGet("/api/demo/cards", async (HttpRequest http, JanusDbContext db) => {
    int limit = 50;
    if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
        limit = Math.Clamp(parsedLimit, 1, 100);

    int offset = 0;
    if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
        offset = Math.Max(0, parsedOffset);

    return Results.Ok(await db.Cards.AsNoTracking()
        .OrderBy(c => c.Uid)
        .Skip(offset)
        .Take(limit)
        .Select(c => new {
            c.Uid,
            status = c.Status.ToString(),
            c.Label,
            balanceMillimes = c.BalanceMillimes
        }).ToListAsync());
})
    .RequireAuthorization("AdminOnly")
    .RequireRateLimiting("AdminPolicy");

// REV-02 / FIN-02: Endpoint dédié au simulateur ne retournant que les informations minimales (sans solde financier ni métadonnées internes), avec limite paginée
app.MapGet("/api/simulator/cards", async (HttpRequest http, JanusDbContext db) => {
    int limit = 20;
    if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
        limit = Math.Clamp(parsedLimit, 1, 50);

    int offset = 0;
    if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
        offset = Math.Max(0, parsedOffset);

    return Results.Ok(await db.Cards.AsNoTracking()
        .OrderBy(c => c.Uid)
        .Skip(offset)
        .Take(limit)
        .Select(c => new {
            c.Uid,
            status = c.Status.ToString(),
            c.Label
        }).ToListAsync());
})
    .RequireAuthorization("CardsRead")
    .RequireRateLimiting("CardsReadPolicy");

// REV-02 / FIN-03: Endpoint métier synthétique pour le dashboard (agrégats SQL regroupés + compteur mémoire explicite pour les rejets 409 non persistés en base)
app.MapGet("/api/dashboard/summary", async (JanusDbContext db) => {
    var validationCounts = await db.Validations.AsNoTracking()
        .GroupBy(v => v.Decision)
        .Select(g => new { Decision = g.Key, Count = g.Count() })
        .ToListAsync();
    var acceptedValidations = validationCounts.FirstOrDefault(x => x.Decision == ValidationDecision.Accepted)?.Count ?? 0;
    var rejectedValidations = validationCounts.FirstOrDefault(x => x.Decision == ValidationDecision.Rejected)?.Count ?? 0;

    var totalDebitedMillimes = await db.FareTransactions.AsNoTracking().SumAsync(t => (long?)t.AmountMillimes) ?? 0L;

    var cardStats = await db.Cards.AsNoTracking()
        .GroupBy(c => c.Status)
        .Select(g => new { Status = g.Key, Count = g.Count(), TotalBalance = g.Sum(c => (long)c.BalanceMillimes) })
        .ToListAsync();
    var activeCardsCount = cardStats.FirstOrDefault(x => x.Status == CardStatus.Active)?.Count ?? 0;
    var revokedCardsCount = cardStats.FirstOrDefault(x => x.Status == CardStatus.Revoked)?.Count ?? 0;
    var totalCirculatingBalanceMillimes = cardStats.Sum(x => x.TotalBalance);

    return Results.Ok(new {
        totalValidations = acceptedValidations + rejectedValidations,
        acceptedValidations,
        rejectedValidations,
        conflictValidations = JanusMetrics.GetConflictValidationsCount(),
        totalDebitedMillimes,
        activeCardsCount,
        revokedCardsCount,
        totalCardsCount = activeCardsCount + revokedCardsCount,
        totalCirculatingBalanceMillimes,
        scrapedAt = DateTimeOffset.UtcNow
    });
})
    .RequireAuthorization("DashboardRead")
    .RequireRateLimiting("CardsReadPolicy");

app.MapGet("/api/cards/{uid}", async (string uid, HttpRequest http, JanusDbContext db) => {
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
}).RequireAuthorization("CardsRead").RequireRateLimiting("CardsReadPolicy");

app.MapGet("/api/cards/{uid}/audit", async (string uid, HttpRequest http, JanusDbContext db) => {
    if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
        return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

    var normalized = uid.Trim().ToUpperInvariant();
    var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(c => c.Uid == normalized);
    if (card is null) return Results.NotFound(new { error = "card_not_found" });

    // PAG-02: Gestion duale des paramètres modernes (page/pageSize) et historiques (limit/offset)
    // Règle de priorité : les paramètres modernes prévalent s'ils sont explicitement fournis.
    int page = 1;
    int pageSize = 25;
    int limit = 50;
    int offset = 0;

    bool hasModernParams = http.Query.ContainsKey("page") || http.Query.ContainsKey("pageSize");
    bool hasLegacyParams = http.Query.ContainsKey("limit") || http.Query.ContainsKey("offset");

    if (hasModernParams)
    {
        if (http.Query.TryGetValue("page", out var pageStr) && int.TryParse(pageStr, out var parsedPage))
            page = Math.Max(1, parsedPage);

        if (http.Query.TryGetValue("pageSize", out var sizeStr) && int.TryParse(sizeStr, out var parsedSize))
            pageSize = Math.Clamp(parsedSize, 1, 100);

        offset = checked((int)Math.Min((long)(page - 1) * pageSize, int.MaxValue));
        limit = pageSize;
    }
    else if (hasLegacyParams)
    {
        if (http.Query.TryGetValue("limit", out var limitStr) && int.TryParse(limitStr, out var parsedLimit))
            limit = Math.Clamp(parsedLimit, 1, 100);

        if (http.Query.TryGetValue("offset", out var offsetStr) && int.TryParse(offsetStr, out var parsedOffset))
            offset = Math.Max(0, parsedOffset);

        pageSize = limit;
        page = (offset / limit) + 1;
    }
    else
    {
        // Défaut historique : limit = 50, offset = 0
        limit = 50;
        offset = 0;
        pageSize = limit;
        page = 1;
    }

    var totalAudits = await db.CardAuditLogs.AsNoTracking().CountAsync(a => a.CardUid == normalized);
    var totalPages = totalAudits == 0 ? 1 : (int)Math.Ceiling((double)totalAudits / pageSize);

    var audits = await db.CardAuditLogs.AsNoTracking()
        .Where(a => a.CardUid == normalized)
        .OrderByDescending(a => a.CreatedAt)
        .ThenByDescending(a => a.Id)
        .Skip(offset)
        .Take(limit)
        .Select(a => new CardAuditLogDto(
            a.Id,
            a.Action,
            a.PreviousBalanceMillimes,
            a.NewBalanceMillimes,
            a.PreviousStatus,
            a.NewStatus,
            a.Reason,
            a.Actor,
            a.CreatedAt
        ))
        .ToListAsync();

    return Results.Ok(new {
        items = audits,
        page,
        pageSize,
        totalCount = totalAudits,
        totalPages,
        cardUid = normalized,
        limit,
        offset,
        auditLogs = audits
    });
}).RequireAuthorization("AuditRead").RequireRateLimiting("AdminPolicy");

app.MapPut("/api/cards/{uid}", async (string uid, CardUpsertRequest request, ClaimsPrincipal user, JanusDbContext db, ILogger<Program> logger) => {
    if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
        return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

    if (request.BalanceMillimes < 0)
        return Results.BadRequest(new { error = "invalid_balance", message = "Balance cannot be negative." });

    // Validation stricte du statut si fourni (uniquement Active ou Revoked, insensible à la casse)
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
    var actor = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "ADMIN";

    await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
    try {
        var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {normalized} FOR UPDATE").SingleOrDefaultAsync();
        var prevBalance = card?.BalanceMillimes;
        var prevStatus = card?.Status.ToString();
        var action = card is null ? "ADMIN_CARD_CREATED" : "ADMIN_CARD_UPDATED";

        // Détermination du statut effectif :
        // - Pour une nouvelle carte : Active par défaut si aucun statut n'est fourni.
        // - Pour une carte existante : conserve son statut actuel si aucun statut n'est fourni.
        CardStatus targetStatus = card is null
            ? (requestedStatus ?? CardStatus.Active)
            : (requestedStatus ?? card.Status);

        // Motif d'audit distinct du libellé de la carte
        var auditReason = !string.IsNullOrWhiteSpace(request.Reason)
            ? request.Reason.Trim()
            : (card is null ? "Initial card provisioning" : "Admin balance/status update");

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

        // Traçabilité d'audit avec identité réelle persistée (SEC-06)
        var audit = new CardAuditLog {
            CardUid = normalized,
            Action = action,
            PreviousBalanceMillimes = prevBalance,
            NewBalanceMillimes = card.BalanceMillimes,
            PreviousStatus = prevStatus,
            NewStatus = card.Status.ToString(),
            Reason = auditReason,
            Actor = actor,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CardAuditLogs.Add(audit);

        await db.SaveChangesAsync();
        await tx.CommitAsync();

        // Journalisation sans secrets
        if (logger.IsEnabled(LogLevel.Information))
        {
            logger.LogInformation("Admin card update: action={Action}, uid={CardUid}, balance={OldBal}->{NewBal}, status={OldStat}->{NewStat}, reason={Reason}, actor={Actor}",
                action, normalized, prevBalance, request.BalanceMillimes, prevStatus, card.Status, auditReason, actor);
        }

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
}).RequireAuthorization("AdminOnly").RequireRateLimiting("AdminPolicy");

app.MapPost("/api/validations", async (ValidationRequest request, ClaimsPrincipal user, IConfiguration config, JanusDbContext db, TimeProvider timeProvider, ILogger<Program> logger) => {
    // SEC-02: Identité du terminal déterminée côté serveur par le jeton authentifié
    var terminalId = user.Identity?.Name ?? user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "TERMINAL-DEMO-01";

    var validationStartTime = Stopwatch.GetTimestamp();
    var serverNow = timeProvider.GetUtcNow();

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

    var apbEnabledConfig = config["AntiPassback:Enabled"];
    bool isApbEnabled = !string.Equals(apbEnabledConfig, "false", StringComparison.OrdinalIgnoreCase);
    int apbWindowSeconds = 30;
    if (int.TryParse(config["AntiPassback:WindowSeconds"], out var parsedWindow) && parsedWindow > 0) {
        apbWindowSeconds = parsedWindow;
    }

    var uid = request.CardUid.Trim().ToUpperInvariant();
    var canonical = JsonSerializer.Serialize(new { eventId = request.EventId, cardUid = uid, occurredAt = request.OccurredAt?.ToUniversalTime(), eventType = request.EventType });
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));

    var existing = await db.Validations.AsNoTracking().SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);
    if (existing is not null) {
        var elapsedDuplicateSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
        if (existing.PayloadHash != hash) {
            JanusMetrics.RecordConflictValidation();
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
        } else {
            bool isPassback = false;
            if (isApbEnabled) {
                var cutoff = serverNow - TimeSpan.FromSeconds(apbWindowSeconds);
                isPassback = await db.Validations.AnyAsync(v =>
                    v.TerminalId == terminalId &&
                    v.CardUid == uid &&
                    v.Decision == ValidationDecision.Accepted &&
                    v.ReceivedAt >= cutoff);
            }

            if (isPassback) {
                decision = ValidationDecision.Rejected;
                reason = "PASSBACK_VIOLATION";
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
                    CreatedAt = serverNow
                });
            }
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
            ReceivedAt = serverNow
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
            JanusMetrics.RecordConflictValidation();
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
}).RequireAuthorization("TerminalOnly").RequireRateLimiting("ValidationsPolicy");

await app.RunAsync();
