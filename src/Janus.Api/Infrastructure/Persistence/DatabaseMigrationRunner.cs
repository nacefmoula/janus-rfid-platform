using Janus.Api.Configuration;
using Janus.Api.Security;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Janus.Api.Infrastructure.Persistence;

/// <summary>
/// Application des migrations EF Core et du provisionnement du moindre privilège (JANUS-SEC-018 / Lots 2B, 2B.1 et 2C) :
/// séparation stricte entre rôle de migration (DDL) et rôle runtime (DML), démarrages concurrents sérialisés par
/// verrou consultatif PostgreSQL, nouvelles tentatives bornées sur erreur transitoire uniquement.
/// </summary>
public static class DatabaseMigrationRunner
{
    // Clé de verrou consultatif (20261019, 1) : littéraux SQL constants, aucune construction dynamique (SonarCloud S2077).
    private const string AcquireMigrationLockSql = "SELECT pg_advisory_lock(20261019, 1);";
    private const string ReleaseMigrationLockSql = "SELECT pg_advisory_unlock(20261019, 1);";

    /// <summary>
    /// Applique les migrations si le mode de démarrage l'exige.
    /// Retourne un code de sortie du processus en mode MigrateOnly (0 succès, 1 échec), sinon null pour poursuivre le démarrage.
    /// </summary>
    public static async Task<int?> RunAsync(WebApplication app, StartupMode mode)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(mode);

        if (!mode.RunsMigrations)
        {
            return null;
        }

        var runtimeConn = app.Configuration.GetConnectionString("JanusDb");
        var migrationConn = app.Configuration.GetConnectionString("MigrationDb");
        var maxMigrationRetries = ConfigurationReader.GetClampedInt(app.Configuration, "Database:MigrationMaxRetries", "DATABASE_MIGRATION_MAX_RETRIES", 29, 0, 60);

        var succeeded = !string.IsNullOrWhiteSpace(migrationConn)
            ? await MigrateWithDedicatedRoleAsync(app, mode, runtimeConn, migrationConn, maxMigrationRetries)
            : await MigrateWithRuntimeContextAsync(app, mode, maxMigrationRetries);

        if (!succeeded)
        {
            await app.DisposeAsync();
            return 1;
        }

        if (mode.MigrateOnly)
        {
            app.Logger.LogInformation("Database migrations and least-privilege provisioning completed successfully (MigrateOnly mode). Exiting.");
            await app.DisposeAsync();
            return 0;
        }

        return null;
    }

    private static async Task<bool> MigrateWithDedicatedRoleAsync(WebApplication app, StartupMode mode, string? runtimeConn, string migrationConn, int maxMigrationRetries)
    {
        var migrationOptions = new DbContextOptionsBuilder<JanusDbContext>()
            .UseNpgsql(migrationConn, npgsql => npgsql.CommandTimeout(30))
            .Options;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await using var migrationLockConn = new NpgsqlConnection(migrationConn);
                await migrationLockConn.OpenAsync();
                await using (var lockCmd = migrationLockConn.CreateCommand())
                {
                    lockCmd.CommandTimeout = 30;
                    lockCmd.CommandText = AcquireMigrationLockSql;
                    await lockCmd.ExecuteNonQueryAsync();
                }

                try
                {
                    await using var migrationDb = new JanusDbContext(migrationOptions);
                    await migrationDb.Database.MigrateAsync();
                    await PostgresLeastPrivilegeProvisioner.EnsureProvisionedAsync(migrationDb, runtimeConn, migrationConn);
                    // REV-07: Le provisionnement automatique des cartes de démonstration est strictement limité à Development
                    if (app.Environment.IsDevelopment())
                    {
                        await DemoDataSeeder.SeedDemoCardsIfEmptyAsync(migrationDb);
                    }
                }
                finally
                {
                    try
                    {
                        await using var unlockCmd = migrationLockConn.CreateCommand();
                        unlockCmd.CommandText = ReleaseMigrationLockSql;
                        await unlockCmd.ExecuteNonQueryAsync();
                    }
                    catch
                    {
                        // La fermeture de migrationLockConn libère automatiquement le verrou de session PostgreSQL
                    }
                }

                return true;
            }
            catch (Exception ex) when (attempt < maxMigrationRetries && IsTransientMigrationStartupException(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                var sqlState = PostgresErrors.TryGetSqlState(ex) ?? "none";
                app.Logger.LogCritical(
                    "Database migration or least-privilege provisioning failed (sqlState={SqlState}, exceptionType={ExceptionType}, attempt={Attempt}).",
                    sqlState,
                    ex.GetType().Name,
                    attempt + 1);
                if (mode.MigrateOnly)
                {
                    return false;
                }

                throw;
            }
        }
    }

    private static async Task<bool> MigrateWithRuntimeContextAsync(WebApplication app, StartupMode mode, int maxMigrationRetries)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<JanusDbContext>();
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await db.Database.MigrateAsync();
                // REV-07: Le provisionnement automatique des cartes de démonstration est strictement limité à Development
                if (app.Environment.IsDevelopment())
                {
                    await DemoDataSeeder.SeedDemoCardsIfEmptyAsync(db);
                }

                return true;
            }
            catch (Exception ex) when (attempt < maxMigrationRetries && IsTransientMigrationStartupException(ex))
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
            }
            catch (Exception ex)
            {
                var sqlState = PostgresErrors.TryGetSqlState(ex) ?? "none";
                app.Logger.LogCritical(
                    "Database startup migration failed (sqlState={SqlState}, exceptionType={ExceptionType}, attempt={Attempt}).",
                    sqlState,
                    ex.GetType().Name,
                    attempt + 1);
                if (mode.MigrateOnly)
                {
                    return false;
                }

                throw;
            }
        }
    }

    private static bool IsTransientMigrationStartupException(Exception ex)
    {
        if (ex is InvalidOperationException or ArgumentException or FormatException)
        {
            return false;
        }

        var sqlState = PostgresErrors.TryGetSqlState(ex);
        // Erreurs d'authentification (28P01, 28000), de privilèges (42501) ou de syntaxe/schéma (42xxx) ne doivent pas être rejouées 30 fois
        return sqlState is null ||
               !(sqlState.StartsWith("28", StringComparison.Ordinal) ||
                 sqlState.StartsWith("42", StringComparison.Ordinal) ||
                 string.Equals(sqlState, "3D000", StringComparison.Ordinal));
    }
}
