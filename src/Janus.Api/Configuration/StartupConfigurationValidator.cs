using Janus.Api.Security;

namespace Janus.Api.Configuration;

/// <summary>Mode de démarrage résolu à partir de la configuration et des arguments.</summary>
/// <param name="MigrateOnly">Le processus applique les migrations puis s'arrête (conteneur db-migrator).</param>
/// <param name="SkipMigration">L'API ne touche pas au schéma (rôle runtime uniquement).</param>
public sealed record StartupMode(bool MigrateOnly, bool SkipMigration)
{
    /// <summary>Les migrations doivent être appliquées par ce processus.</summary>
    public bool RunsMigrations => !SkipMigration || MigrateOnly;
}

/// <summary>
/// Validation centralisée et fail-fast de la configuration au démarrage (SEC-03 / REV-01 / REV-05 / REV-07 / FIN-05).
/// Lève <see cref="InvalidOperationException"/> sans jamais inclure de valeur secrète dans le message.
/// </summary>
public static class StartupConfigurationValidator
{
    private static readonly string[] KnownDevTokens =
    [
        "dev-terminal-token-change-me",
        "dev-admin-token-change-me",
        "dev-terminal-02-token-change-me",
        "dev-metrics-scrape-token-change-me"
    ];

    /// <summary>Valide la configuration et retourne le mode de démarrage.</summary>
    public static StartupMode Validate(IConfiguration configuration, IHostEnvironment environment, string[] args)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(args);

        var startupApbWindow = configuration["AntiPassback:WindowSeconds"];
        if (!string.IsNullOrEmpty(startupApbWindow) && (!int.TryParse(startupApbWindow, out var startupWindow) || startupWindow <= 0))
            throw new InvalidOperationException($"Invalid AntiPassback:WindowSeconds '{startupApbWindow}'. Must be a strictly positive integer.");

        var isDev = environment.IsDevelopment();
        var migrateOnly = args.Contains("--migrate-only", StringComparer.OrdinalIgnoreCase) ||
                          configuration.GetValue<bool>("Database:MigrateOnly", false);
        var skipMigration = configuration.GetValue<bool>("Database:SkipMigration", false);

        if (migrateOnly && skipMigration)
            throw new InvalidOperationException("Configuration error: 'Database:MigrateOnly' and 'Database:SkipMigration' cannot both be true.");

        if (migrateOnly && string.IsNullOrWhiteSpace(configuration.GetConnectionString("MigrationDb")))
            throw new InvalidOperationException("Configuration error: 'ConnectionStrings:MigrationDb' must be configured when 'Database:MigrateOnly' is true.");

        if (!migrateOnly)
        {
            ValidateTokens(configuration, isDev);
        }

        if (!isDev)
        {
            PostgresLeastPrivilegeProvisioner.ValidateProductionDatabaseConfiguration(
                configuration.GetConnectionString("JanusDb"),
                configuration.GetConnectionString("MigrationDb"),
                skipMigration && !migrateOnly);
        }

        // LOT-3A / FIN-05 : le tarif est une donnée financière obligatoire pour servir des validations.
        // Fail-fast au démarrage plutôt qu'une API "vivante" qui renverrait 500 sur chaque validation.
        // Aucune valeur par défaut implicite dans le code : le montant facturé doit toujours être un choix explicite d'exploitation.
        if (!migrateOnly)
        {
            var startupFareConfig = configuration["Fare:AmountMillimes"];
            if (!int.TryParse(startupFareConfig, out var startupFare) || startupFare <= 0)
                throw new InvalidOperationException("Configuration error: 'Fare:AmountMillimes' must be configured with a strictly positive integer amount in millimes.");
        }

        return new StartupMode(migrateOnly, skipMigration);
    }

    private static void ValidateTokens(IConfiguration configuration, bool isDev)
    {
        var terminalToken = configuration["TerminalAuth:DemoToken"];
        var terminal2Token = configuration["TerminalAuth:Terminal2Token"];
        var adminToken = configuration["AdminAuth:DemoToken"];
        var scrapeToken = configuration["MetricsAuth:ScrapeToken"];

        if (!isDev)
        {
            EnsureStrongToken(terminalToken, "TerminalAuth:DemoToken");
            EnsureStrongToken(terminal2Token, "TerminalAuth:Terminal2Token");
            EnsureStrongToken(adminToken, "AdminAuth:DemoToken");
            EnsureStrongToken(scrapeToken, "MetricsAuth:ScrapeToken");
        }

        // Vérification systématique d'unicité de tous les jetons configurés (évite toute collision de rôle Admin/Scraper/Terminal)
        var configuredTokens = new[] { terminalToken, terminal2Token, adminToken, scrapeToken }
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t!.Trim())
            .ToList();

        if (configuredTokens.Count != configuredTokens.Distinct(StringComparer.Ordinal).Count())
            throw new InvalidOperationException("Security configuration error: Distinct tokens are required across Terminal1, Terminal2, Admin, and MetricsScrape.");
    }

    private static void EnsureStrongToken(string? token, string key)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length < 16 || KnownDevTokens.Contains(token))
            throw new InvalidOperationException($"Production configuration error: '{key}' must be configured with a non-default secret (minimum 16 characters) in non-development environments.");
    }
}
