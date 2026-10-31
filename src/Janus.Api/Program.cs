using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using Janus.Api;
using Janus.Api.Security;

static int GetRateLimitConfig(IConfiguration config, string primaryKey, string envFallbackKey, int defaultValue)
{
    var raw = config[primaryKey] ?? config[envFallbackKey];
    return int.TryParse(raw, out var val) && val > 0 ? val : defaultValue;
}

static int GetClampedIntConfig(IConfiguration config, string primaryKey, string envFallbackKey, int defaultValue, int min, int max)
{
    var raw = config[primaryKey] ?? config[envFallbackKey];
    return int.TryParse(raw, out var val) ? Math.Clamp(val, min, max) : defaultValue;
}

static string? TryGetPostgresSqlState(Exception ex)
{
    for (Exception? curr = ex; curr is not null; curr = curr.InnerException)
    {
        if (curr is PostgresException pgEx)
        {
            return pgEx.SqlState;
        }
    }

    return null;
}

static bool IsTransientMigrationStartupException(Exception ex)
{
    if (ex is InvalidOperationException or ArgumentException or FormatException)
    {
        return false;
    }

    var sqlState = TryGetPostgresSqlState(ex);
    if (sqlState is not null)
    {
        // Erreurs d'authentification (28P01, 28000), de privilèges (42501) ou de syntaxe/schéma (42xxx) ne doivent pas être rejouées 30 fois
        if (sqlState.StartsWith("28", StringComparison.Ordinal) ||
            sqlState.StartsWith("42", StringComparison.Ordinal) ||
            string.Equals(sqlState, "3D000", StringComparison.Ordinal))
        {
            return false;
        }
    }

    return true;
}

static async Task ApplyTransactionTimeoutsAsync(JanusDbContext db, int lockTimeoutMs, int statementTimeoutMs)
{
    var safeLockMs = Math.Clamp(lockTimeoutMs, 50, 60000);
    var safeStmtMs = Math.Clamp(statementTimeoutMs, 100, 120000);
    await db.Database.ExecuteSqlRawAsync(
        $"SET LOCAL lock_timeout = '{safeLockMs}ms'; SET LOCAL statement_timeout = '{safeStmtMs}ms';");
}

static async Task SafeRollbackAsync(IDbContextTransaction? tx, ILogger logger, string operation)
{
    if (tx is null)
    {
        return;
    }

    try
    {
        await tx.RollbackAsync(CancellationToken.None);
    }
    catch (Exception rollbackEx)
    {
        if (logger.IsEnabled(LogLevel.Debug))
        {
            logger.LogDebug(rollbackEx, "Transaction rollback on aborted connection ignored for operation={Operation}", operation);
        }
    }
}

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddDbContext<JanusDbContext>((sp, o) =>
{
    var cfg = sp.GetRequiredService<IConfiguration>();
    var cmdTimeoutSeconds = GetClampedIntConfig(cfg, "Database:CommandTimeoutSeconds", "DATABASE_COMMAND_TIMEOUT_SECONDS", 10, 1, 120);
    o.UseNpgsql(cfg.GetConnectionString("JanusDb"), npgsql => npgsql.CommandTimeout(cmdTimeoutSeconds));
});
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<JanusTransactionCommitHook>();

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
var migrateOnlyConfig = args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase) ||
                        app.Configuration.GetValue<bool>("Database:MigrateOnly", false);
var skipMigrationConfig = app.Configuration.GetValue<bool>("Database:SkipMigration", false);

if (migrateOnlyConfig && skipMigrationConfig)
{
    throw new InvalidOperationException("Configuration error: 'Database:MigrateOnly' and 'Database:SkipMigration' cannot both be true.");
}

if (migrateOnlyConfig && string.IsNullOrWhiteSpace(app.Configuration.GetConnectionString("MigrationDb")))
{
    throw new InvalidOperationException("Configuration error: 'ConnectionStrings:MigrationDb' must be configured when 'Database:MigrateOnly' is true.");
}

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

if (!migrateOnlyConfig)
{
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
}

if (!isDev)
{
    PostgresLeastPrivilegeProvisioner.ValidateProductionDatabaseConfiguration(
        app.Configuration.GetConnectionString("JanusDb"),
        app.Configuration.GetConnectionString("MigrationDb"),
        skipMigrationConfig && !migrateOnlyConfig);
}

// JANUS-SEC-009: Gestionnaire d'exception global retournant une réponse JSON standardisée avec traceId et masquant les piles d'appels internes
app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exceptionHandlerFeature = context.Features.Get<IExceptionHandlerFeature>();
        var exception = exceptionHandlerFeature?.Error;
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();
        var traceId = Activity.Current?.Id ?? context.TraceIdentifier;

        logger.LogError(exception, "Unhandled exception occurred. TraceId: {TraceId}", traceId);

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json";

        await context.Response.WriteAsJsonAsync(new
        {
            error = "internal_error",
            message = "An unexpected error occurred.",
            traceId = traceId
        });
    });
});

// REV-03: Ordre strict du pipeline : ExceptionHandler -> Routing -> ForwardedHeaders -> Authentication -> RateLimiter -> Authorization
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

// JANUS-SEC-018 / Lot 2B, 2B.1 & 2C: Séparation stricte entre rôle de migration (DDL) et rôle runtime (DML moindre privilège)
if (!skipMigrationConfig || migrateOnlyConfig)
{
    var runtimeConn = app.Configuration.GetConnectionString("JanusDb");
    var migrationConn = app.Configuration.GetConnectionString("MigrationDb");
    var maxMigrationRetries = GetClampedIntConfig(app.Configuration, "Database:MigrationMaxRetries", "DATABASE_MIGRATION_MAX_RETRIES", 29, 0, 60);
    if (!string.IsNullOrWhiteSpace(migrationConn))
    {
        var migrationOptions = new DbContextOptionsBuilder<JanusDbContext>()
            .UseNpgsql(migrationConn, npgsql => npgsql.CommandTimeout(30))
            .Options;

        for (int attempt = 0; ; attempt++) {
            try {
                await using var migrationLockConn = new NpgsqlConnection(migrationConn);
                await migrationLockConn.OpenAsync();
                await using (var lockCmd = migrationLockConn.CreateCommand())
                {
                    lockCmd.CommandTimeout = 30;
                    lockCmd.CommandText = "SELECT pg_advisory_lock(20261019, 1);";
                    await lockCmd.ExecuteNonQueryAsync();
                }

                try
                {
                    await using var migrationDb = new JanusDbContext(migrationOptions);
                    await migrationDb.Database.MigrateAsync();
                    await PostgresLeastPrivilegeProvisioner.EnsureProvisionedAsync(migrationDb, runtimeConn, migrationConn);
                    // REV-07: Le provisionnement automatique des cartes de démonstration est strictement limité à Development
                    if (app.Environment.IsDevelopment() && !await migrationDb.Cards.AnyAsync()) {
                        migrationDb.Cards.AddRange(
                            new TransitCard { Uid = "04A1B2C3D4E5F6", Status = CardStatus.Active, Label = "Carte démo active", BalanceMillimes = 10000 },
                            new TransitCard { Uid = "04DEAD00000001", Status = CardStatus.Revoked, Label = "Carte démo révoquée", BalanceMillimes = 5000 }
                        );
                        await migrationDb.SaveChangesAsync();
                    }
                }
                finally
                {
                    try
                    {
                        await using var unlockCmd = migrationLockConn.CreateCommand();
                        unlockCmd.CommandText = "SELECT pg_advisory_unlock(20261019, 1);";
                        await unlockCmd.ExecuteNonQueryAsync();
                    }
                    catch
                    {
                        // La fermeture de migrationLockConn libère automatiquement le verrou de session PostgreSQL
                    }
                }
                break;
            } catch (Exception ex) when (attempt < maxMigrationRetries && IsTransientMigrationStartupException(ex)) {
                await Task.Delay(TimeSpan.FromSeconds(2));
            } catch (Exception ex) {
                var sqlState = TryGetPostgresSqlState(ex) ?? "none";
                app.Logger.LogCritical(
                    "Database migration or least-privilege provisioning failed (sqlState={SqlState}, exceptionType={ExceptionType}, attempt={Attempt}).",
                    sqlState,
                    ex.GetType().Name,
                    attempt + 1);
                if (migrateOnlyConfig) {
                    await app.DisposeAsync();
                    return 1;
                }
                throw;
            }
        }
    }
    else
    {
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
                } catch (Exception ex) when (attempt < maxMigrationRetries && IsTransientMigrationStartupException(ex)) {
                    await Task.Delay(TimeSpan.FromSeconds(2));
                } catch (Exception ex) {
                    var sqlState = TryGetPostgresSqlState(ex) ?? "none";
                    app.Logger.LogCritical(
                        "Database startup migration failed (sqlState={SqlState}, exceptionType={ExceptionType}, attempt={Attempt}).",
                        sqlState,
                        ex.GetType().Name,
                        attempt + 1);
                    if (migrateOnlyConfig) {
                        await app.DisposeAsync();
                        return 1;
                    }
                    throw;
                }
            }
        }
    }

    if (migrateOnlyConfig)
    {
        app.Logger.LogInformation("Database migrations and least-privilege provisioning completed successfully (MigrateOnly mode). Exiting.");
        await app.DisposeAsync();
        return 0;
    }
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = "janus-api" }))
    .AllowAnonymous();

// Liveness probe standardisée (vérifie que le runtime ASP.NET Core répond sans dépendance externe)
app.MapGet("/health/live", () => Results.Ok(new { status = "live", service = "janus-api" }))
    .AllowAnonymous();

// JANUS-SEC-011: Sonde de readiness vérifiant la connectivité effective à la base de données PostgreSQL
app.MapGet("/health/ready", async (JanusDbContext db, ILogger<Program> logger) =>
{
    try
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var canConnect = await db.Database.CanConnectAsync(cts.Token);
        if (canConnect)
        {
            return Results.Ok(new { status = "ready", service = "janus-api", database = "connected" });
        }

        logger.LogWarning("Readiness probe: Database.CanConnectAsync returned false.");
        return Results.Json(new { status = "unhealthy", service = "janus-api", database = "disconnected" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Readiness probe exception: unable to connect to database.");
        return Results.Json(new { status = "unhealthy", service = "janus-api", database = "disconnected", error = "database_unavailable" }, statusCode: StatusCodes.Status503ServiceUnavailable);
    }
})
    .AllowAnonymous();

if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Testing:EnableFaultEndpoint", false))
{
    app.MapGet("/test-fault/unhandled-exception", () =>
    {
        throw new InvalidOperationException("Simulated catastrophic crash in internal service. SecretValue=SUPER_SECRET_12345");
    }).AllowAnonymous();
}

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

// JANUS-SEC-002: Endpoint minimal de vérification de présence pour le simulateur et les valideurs (masque le solde et les transactions)
app.MapGet("/api/simulator/cards/{uid}/presence", async (string uid, JanusDbContext db) => {
    if (string.IsNullOrWhiteSpace(uid) || uid.Length > 64)
        return Results.BadRequest(new { error = "invalid_uid", message = "Valid card UID is required." });

    var normalized = uid.Trim().ToUpperInvariant();
    var card = await db.Cards.AsNoTracking().SingleOrDefaultAsync(c => c.Uid == normalized);
    if (card is null) return Results.NotFound(new { error = "card_not_found" });

    return Results.Ok(new {
        uid = card.Uid,
        status = card.Status.ToString()
    });
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
// JANUS-SEC-002: L'accès aux détails financiers et à l'historique complet est strictement restreint aux administrateurs
}).RequireAuthorization("AdminOnly").RequireRateLimiting("AdminPolicy");

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

app.MapPut("/api/cards/{uid}", async (string uid, CardUpsertRequest request, ClaimsPrincipal user, IConfiguration config, IServiceScopeFactory scopeFactory, ILogger<Program> logger) => {
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
    var lockTimeoutMs = GetClampedIntConfig(config, "Database:LockTimeoutMs", "DATABASE_LOCK_TIMEOUT_MS", 5000, 50, 60000);
    var statementTimeoutMs = GetClampedIntConfig(config, "Database:StatementTimeoutMs", "DATABASE_STATEMENT_TIMEOUT_MS", 10000, 100, 120000);
    const int maxTransientRetries = 2;

    for (int attempt = 0; attempt <= maxTransientRetries; attempt++)
    {
        await using var attemptScope = scopeFactory.CreateAsyncScope();
        var db = attemptScope.ServiceProvider.GetRequiredService<JanusDbContext>();
        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        bool wasCreatingNewCard = false;

        try {
            await ApplyTransactionTimeoutsAsync(db, lockTimeoutMs, statementTimeoutMs);

            var card = await db.Cards.FromSqlInterpolated($"SELECT * FROM \"Cards\" WHERE \"Uid\" = {normalized} FOR UPDATE").SingleOrDefaultAsync();
            wasCreatingNewCard = card is null;
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
            await tx.CommitAsync(CancellationToken.None);

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
            await SafeRollbackAsync(tx, logger, "card_upsert");
            var sqlState = TryGetPostgresSqlState(ex);

            // P1-3 / Lot 2C: Si deux requêtes PUT concurrentes créent la même nouvelle carte simultanément (23505 sur IX_Cards_Uid),
            // rejouer une unique fois la transaction complète dans un nouveau scope DI afin de verrouiller (SELECT ... FOR UPDATE)
            // la carte nouvellement créée et d'enregistrer la seconde opération comme ADMIN_CARD_UPDATED avec PreviousBalance exact.
            if (string.Equals(sqlState, "23505", StringComparison.Ordinal) && wasCreatingNewCard && attempt == 0)
            {
                JanusMetrics.RecordDbTransactionEvent("card_upsert", "conflict_recovered");
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Concurrent card creation collision resolved by retrying under row lock: operation=card_upsert, sqlState=23505, outcome=conflict_recovered, attempt={Attempt}, cardUid={CardUid}",
                        attempt, normalized);
                }
                continue;
            }

            if (string.Equals(sqlState, "55P03", StringComparison.Ordinal) || string.Equals(sqlState, "57014", StringComparison.Ordinal))
            {
                JanusMetrics.RecordDbTransactionEvent("card_upsert", "lock_timeout");
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Database lock or statement timeout on card_upsert (no retry): operation=card_upsert, sqlState={SqlState}, outcome=lock_timeout, attempt={Attempt}, cardUid={CardUid}",
                        sqlState, attempt, normalized);
                }
                return Results.Json(new { error = "database_busy", message = "The card record is temporarily locked by a concurrent operation. Please retry." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.Equals(sqlState, "40001", StringComparison.Ordinal) || string.Equals(sqlState, "40P01", StringComparison.Ordinal))
            {
                if (attempt < maxTransientRetries)
                {
                    JanusMetrics.RecordDbTransactionEvent("card_upsert", "transient_retry");
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.LogWarning("Transient database conflict on card_upsert; retrying in a new scope: operation=card_upsert, sqlState={SqlState}, outcome=transient_retry, attempt={Attempt}, cardUid={CardUid}",
                            sqlState, attempt, normalized);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)));
                    continue;
                }

                return Results.Json(new { error = "database_busy", message = "Temporary transactional conflict persisted after retries. Please retry." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            logger.LogError(ex, "Failed to persist admin card upsert for {CardUid} (sqlState={SqlState}, attempt={Attempt})", normalized, sqlState, attempt);
            throw;
        }
    }

    return Results.Json(new { error = "database_busy", message = "Unable to complete card update after bounded retries." }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).RequireAuthorization("AdminOnly").RequireRateLimiting("AdminPolicy");

app.MapPost("/api/validations", async (ValidationRequest request, ClaimsPrincipal user, IConfiguration config, IServiceScopeFactory scopeFactory, JanusTransactionCommitHook commitHook, TimeProvider timeProvider, ILogger<Program> logger) => {
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
    var lockTimeoutMs = GetClampedIntConfig(config, "Database:LockTimeoutMs", "DATABASE_LOCK_TIMEOUT_MS", 5000, 50, 60000);
    var statementTimeoutMs = GetClampedIntConfig(config, "Database:StatementTimeoutMs", "DATABASE_STATEMENT_TIMEOUT_MS", 10000, 100, 120000);
    const int maxTransientRetries = 2;

    IResult BuildIdempotentOrConflictResponse(ValidationEvent existingRecord, int? reconciledBalanceMillimes)
    {
        var elapsedSeconds = Stopwatch.GetElapsedTime(validationStartTime).TotalSeconds;
        if (existingRecord.PayloadHash != hash) {
            JanusMetrics.RecordConflictValidation();
            JanusMetrics.ValidationDurationSeconds.Record(elapsedSeconds,
                new KeyValuePair<string, object?>("decision", "CONFLICT"));
            JanusMetrics.ValidationsTotal.Add(1,
                new KeyValuePair<string, object?>("decision", "CONFLICT"),
                new KeyValuePair<string, object?>("reason", "IDEMPOTENCY_CONFLICT"),
                new KeyValuePair<string, object?>("terminal_id", terminalId),
                new KeyValuePair<string, object?>("duplicate", "false"));
            return Results.Conflict(new { error = "idempotency_conflict", message = "eventId already used with different payload." });
        }

        JanusMetrics.ValidationDurationSeconds.Record(elapsedSeconds,
            new KeyValuePair<string, object?>("decision", existingRecord.Decision.ToString().ToUpperInvariant()));
        JanusMetrics.ValidationsTotal.Add(1,
            new KeyValuePair<string, object?>("decision", existingRecord.Decision.ToString().ToUpperInvariant()),
            new KeyValuePair<string, object?>("reason", existingRecord.Reason),
            new KeyValuePair<string, object?>("terminal_id", terminalId),
            new KeyValuePair<string, object?>("duplicate", "true"));

        if (reconciledBalanceMillimes.HasValue)
        {
            return Results.Ok(new {
                eventId = existingRecord.EventId,
                terminalId = existingRecord.TerminalId,
                cardUid = existingRecord.CardUid,
                decision = existingRecord.Decision.ToString().ToUpperInvariant(),
                reason = existingRecord.Reason,
                balanceMillimes = reconciledBalanceMillimes.Value,
                amountDebited = (existingRecord.Decision == ValidationDecision.Accepted ? fareAmount : 0),
                duplicate = true,
                receivedAt = existingRecord.ReceivedAt
            });
        }

        return Results.Ok(new {
            eventId = existingRecord.EventId,
            terminalId = existingRecord.TerminalId,
            cardUid = existingRecord.CardUid,
            decision = existingRecord.Decision.ToString().ToUpperInvariant(),
            reason = existingRecord.Reason,
            duplicate = true,
            receivedAt = existingRecord.ReceivedAt
        });
    }

    for (int attempt = 0; attempt <= maxTransientRetries; attempt++)
    {
        await using var attemptScope = scopeFactory.CreateAsyncScope();
        var db = attemptScope.ServiceProvider.GetRequiredService<JanusDbContext>();

        // Situation 3 : Pré-vérification d'idempotence sur une connexion saine
        var existing = await db.Validations.AsNoTracking().SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);
        if (existing is not null) {
            return BuildIdempotentOrConflictResponse(existing, null);
        }

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.ReadCommitted);
        bool commitAttempted = false;
        try {
            await ApplyTransactionTimeoutsAsync(db, lockTimeoutMs, statementTimeoutMs);

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
            commitAttempted = true;
            await tx.CommitAsync(CancellationToken.None);

            if (commitHook.OnAfterValidationDatabaseCommitAsync is not null)
            {
                await commitHook.OnAfterValidationDatabaseCommitAsync(terminalId, request.EventId, CancellationToken.None);
            }

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
        } catch (Exception ex) {
            if (commitAttempted)
            {
                // Situation 2 (P1-1 / Lot 2C) : Échec pendant ou immédiatement après CommitAsync() -> résultat d'engagement AMBIGU.
                // Ne jamais réutiliser le DbContext potentiellement corrompu ni déclencher de retry financier aveugle.
                // Ouvrir un nouveau scope DI indépendant pour vérifier si PostgreSQL a engagé (TerminalId, EventId).
                try
                {
                    await using var reconcileScope = scopeFactory.CreateAsyncScope();
                    var reconcileDb = reconcileScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                    var committed = await reconcileDb.Validations.AsNoTracking()
                        .SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);

                    if (committed is not null)
                    {
                        if (committed.PayloadHash != hash)
                        {
                            JanusMetrics.RecordDbTransactionEvent("validation", "conflict_recovered");
                            logger.LogWarning(ex,
                                "Ambiguous commit reconciliation found conflicting payload: operation=validation, outcome=conflict_recovered, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                                terminalId, request.EventId, attempt);
                            return BuildIdempotentOrConflictResponse(committed, null);
                        }

                        var reconciledBalance = await reconcileDb.Cards.AsNoTracking()
                            .Where(c => c.Uid == uid)
                            .Select(c => (int?)c.BalanceMillimes)
                            .SingleOrDefaultAsync();

                        JanusMetrics.RecordDbTransactionEvent("validation", "ambiguous_commit_recovered");
                        if (committed.Decision == ValidationDecision.Accepted)
                        {
                            JanusMetrics.FareAmountDebitedMillimesTotal.Add(fareAmount,
                                new KeyValuePair<string, object?>("terminal_id", terminalId));
                        }

                        logger.LogWarning(ex,
                            "Ambiguous commit reconciled via fresh scope (transaction was committed in PostgreSQL): operation=validation, outcome=ambiguous_commit_recovered, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}, exceptionType={ExceptionType}",
                            terminalId, request.EventId, attempt, ex.GetType().Name);

                        return BuildIdempotentOrConflictResponse(committed, reconciledBalance);
                    }
                }
                catch (Exception reconcileEx)
                {
                    logger.LogError(reconcileEx,
                        "Reconciliation query failed after ambiguous commit: operation=validation, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                        terminalId, request.EventId, attempt);
                    return Results.Json(new { error = "database_unavailable", message = "Validation commit state is ambiguous and reconciliation failed. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
                }

                // L'événement n'a pas été trouvé dans un scope sain : le commit n'a pas abouti côté PostgreSQL.
                await SafeRollbackAsync(tx, logger, "validation");
                if (attempt == 0)
                {
                    JanusMetrics.RecordDbTransactionEvent("validation", "transient_retry");
                    logger.LogWarning(ex,
                        "Commit did not persist in PostgreSQL; retrying transaction once in a new scope: operation=validation, outcome=transient_retry, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                        terminalId, request.EventId, attempt);
                    await Task.Delay(TimeSpan.FromMilliseconds(20));
                    continue;
                }

                return Results.Json(new { error = "database_unavailable", message = "Validation transaction could not be committed. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            // Situation 1 (P1-1 / P1-2) : Échec AVANT CommitAsync() -> aucun débit engagé par cette tentative.
            await SafeRollbackAsync(tx, logger, "validation");
            var sqlState = TryGetPostgresSqlState(ex);

            if (string.Equals(sqlState, "23505", StringComparison.Ordinal) || ex is DbUpdateException)
            {
                await using var reconcileScope = scopeFactory.CreateAsyncScope();
                var reconcileDb = reconcileScope.ServiceProvider.GetRequiredService<JanusDbContext>();
                var winner = await reconcileDb.Validations.AsNoTracking()
                    .SingleOrDefaultAsync(v => v.TerminalId == terminalId && v.EventId == request.EventId);

                if (winner is not null)
                {
                    JanusMetrics.RecordDbTransactionEvent("validation", "conflict_recovered");
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.LogWarning("Concurrent validation race resolved via fresh scope: operation=validation, sqlState={SqlState}, outcome=conflict_recovered, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                            sqlState ?? "DbUpdateException", terminalId, request.EventId, attempt);
                    }
                    return BuildIdempotentOrConflictResponse(winner, null);
                }
            }

            if (string.Equals(sqlState, "55P03", StringComparison.Ordinal) || string.Equals(sqlState, "57014", StringComparison.Ordinal))
            {
                JanusMetrics.RecordDbTransactionEvent("validation", "lock_timeout");
                if (logger.IsEnabled(LogLevel.Warning))
                {
                    logger.LogWarning("Database lock or statement timeout during validation (no automatic retry): operation=validation, sqlState={SqlState}, outcome=lock_timeout, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                        sqlState, terminalId, request.EventId, attempt);
                }
                return Results.Json(new { error = "database_busy", message = "Validation resource is temporarily locked by a concurrent operation. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            if (string.Equals(sqlState, "40001", StringComparison.Ordinal) || string.Equals(sqlState, "40P01", StringComparison.Ordinal))
            {
                if (attempt < maxTransientRetries)
                {
                    JanusMetrics.RecordDbTransactionEvent("validation", "transient_retry");
                    if (logger.IsEnabled(LogLevel.Warning))
                    {
                        logger.LogWarning("Transient serialization or deadlock conflict during validation; retrying in a new scope: operation=validation, sqlState={SqlState}, outcome=transient_retry, terminalId={TerminalId}, eventId={EventId}, attempt={Attempt}",
                            sqlState, terminalId, request.EventId, attempt);
                    }
                    await Task.Delay(TimeSpan.FromMilliseconds(15 * (attempt + 1)));
                    continue;
                }

                return Results.Json(new { error = "database_busy", message = "Temporary transactional conflict persisted after retries. Please retry with the same eventId." }, statusCode: StatusCodes.Status503ServiceUnavailable);
            }

            logger.LogError(ex, "Unrecoverable error during validation: terminalId={TerminalId}, eventId={EventId}, sqlState={SqlState}, attempt={Attempt}",
                terminalId, request.EventId, sqlState, attempt);
            throw;
        }
    }

    return Results.Json(new { error = "database_busy", message = "Unable to complete validation after bounded retries." }, statusCode: StatusCodes.Status503ServiceUnavailable);
}).RequireAuthorization("TerminalOnly").RequireRateLimiting("ValidationsPolicy");

await app.RunAsync();
return 0;
