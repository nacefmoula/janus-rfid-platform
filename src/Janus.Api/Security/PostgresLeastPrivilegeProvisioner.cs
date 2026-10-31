using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Janus.Api.Security;

public static class PostgresLeastPrivilegeProvisioner
{
    private static readonly SemaphoreSlim ProvisionSemaphore = new(1, 1);

    private static readonly string[] KnownDevDatabasePasswords =
    [
        "local-dev-db-password-change-me",
        "local-dev-runtime-password-change-me",
        "change_me_runtime_only",
        "postgres",
        "password",
        "admin",
        "change-me",
        "change_me"
    ];

    public static bool IsKnownDevelopmentPassword(string? password)
    {
        if (string.IsNullOrWhiteSpace(password))
        {
            return true;
        }

        var trimmed = password.Trim();
        if (trimmed.Contains("change-me", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("change_me", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        foreach (var candidate in KnownDevDatabasePasswords)
        {
            if (string.Equals(trimmed, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    public static void ValidateProductionDatabaseConfiguration(
        string? runtimeConnectionString,
        string? migrationConnectionString,
        bool skipMigration)
    {
        if (string.IsNullOrWhiteSpace(runtimeConnectionString))
        {
            throw new InvalidOperationException(
                "Production configuration error: 'ConnectionStrings:JanusDb' must be configured in non-development environments.");
        }

        NpgsqlConnectionStringBuilder runtimeBuilder;
        try
        {
            runtimeBuilder = new NpgsqlConnectionStringBuilder(runtimeConnectionString);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            throw new InvalidOperationException(
                "Production configuration error: 'ConnectionStrings:JanusDb' format is invalid.", ex);
        }

        if (string.IsNullOrWhiteSpace(runtimeBuilder.Host) ||
            string.IsNullOrWhiteSpace(runtimeBuilder.Database) ||
            string.IsNullOrWhiteSpace(runtimeBuilder.Username) ||
            string.IsNullOrWhiteSpace(runtimeBuilder.Password))
        {
            throw new InvalidOperationException(
                "Production configuration error: 'ConnectionStrings:JanusDb' must specify Host, Database, Username, and Password in non-development environments.");
        }

        if (string.Equals(runtimeBuilder.Username.Trim(), "postgres", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Production configuration error: 'ConnectionStrings:JanusDb' cannot use the 'postgres' superuser role.");
        }

        if (runtimeBuilder.Password.Length < 12 || IsKnownDevelopmentPassword(runtimeBuilder.Password))
        {
            throw new InvalidOperationException(
                "Production configuration error: 'ConnectionStrings:JanusDb' must use a non-default database password (minimum 12 characters) in non-development environments.");
        }

        if (!skipMigration && string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            throw new InvalidOperationException(
                "Production configuration error: 'ConnectionStrings:MigrationDb' must be configured when 'Database:SkipMigration' is false in non-development environments.");
        }

        if (!string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            NpgsqlConnectionStringBuilder migrationBuilder;
            try
            {
                migrationBuilder = new NpgsqlConnectionStringBuilder(migrationConnectionString);
            }
            catch (Exception ex) when (ex is ArgumentException or FormatException)
            {
                throw new InvalidOperationException(
                    "Production configuration error: 'ConnectionStrings:MigrationDb' format is invalid.", ex);
            }

            if (string.IsNullOrWhiteSpace(migrationBuilder.Host) ||
                string.IsNullOrWhiteSpace(migrationBuilder.Database) ||
                string.IsNullOrWhiteSpace(migrationBuilder.Username) ||
                string.IsNullOrWhiteSpace(migrationBuilder.Password))
            {
                throw new InvalidOperationException(
                    "Production configuration error: 'ConnectionStrings:MigrationDb' must specify Host, Database, Username, and Password in non-development environments.");
            }

            if (migrationBuilder.Password.Length < 12 || IsKnownDevelopmentPassword(migrationBuilder.Password))
            {
                throw new InvalidOperationException(
                    "Production configuration error: 'ConnectionStrings:MigrationDb' must use a non-default database password (minimum 12 characters) in non-development environments.");
            }

            if (string.Equals(runtimeBuilder.Username.Trim(), migrationBuilder.Username.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Production configuration error: 'ConnectionStrings:JanusDb' (runtime role) and 'ConnectionStrings:MigrationDb' (migration role) must use distinct PostgreSQL roles.");
            }

            if (!string.Equals(runtimeBuilder.Database.Trim(), migrationBuilder.Database.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Production configuration error: 'ConnectionStrings:JanusDb' and 'ConnectionStrings:MigrationDb' must target the same database.");
            }
        }
    }

    public static async Task EnsureProvisionedAsync(
        JanusDbContext migrationDb,
        string? runtimeConnectionString,
        string? migrationConnectionString,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(migrationDb);

        await ProvisionSemaphore.WaitAsync(cancellationToken);
        try
        {
            await using var tx = await migrationDb.Database.BeginTransactionAsync(cancellationToken);

            // Verrou consultatif transactionnel PostgreSQL pour sérialiser les démarrages concurrents
            // (évite l'erreur catalogue XX000 'tuple concurrently updated' lorsque plusieurs hôtes démarrent en parallèle)
            await migrationDb.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(20261019, 2);", cancellationToken);

        // 1. Garantie idempotente des triggers d'immuabilité d'audit (UPDATE, DELETE et TRUNCATE)
        // même sur une base où la migration initiale 20261019215017 avait déjà été appliquée avant le Lot 2B.1
        const string ensureTriggersSql = @"
CREATE OR REPLACE FUNCTION prevent_card_audit_logs_tampering()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'CardAuditLogs is an append-only audit trail. Modifications and deletions are strictly prohibited.'
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_card_audit_logs ON ""CardAuditLogs"";
CREATE TRIGGER trg_protect_card_audit_logs
BEFORE UPDATE OR DELETE ON ""CardAuditLogs""
FOR EACH ROW
EXECUTE FUNCTION prevent_card_audit_logs_tampering();

DROP TRIGGER IF EXISTS trg_protect_card_audit_logs_truncate ON ""CardAuditLogs"";
CREATE TRIGGER trg_protect_card_audit_logs_truncate
BEFORE TRUNCATE ON ""CardAuditLogs""
FOR EACH STATEMENT
EXECUTE FUNCTION prevent_card_audit_logs_tampering();
";
        await migrationDb.Database.ExecuteSqlRawAsync(ensureTriggersSql, cancellationToken);

        if (string.IsNullOrWhiteSpace(runtimeConnectionString) || string.IsNullOrWhiteSpace(migrationConnectionString))
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        var runtimeBuilder = new NpgsqlConnectionStringBuilder(runtimeConnectionString);
        var migrationBuilder = new NpgsqlConnectionStringBuilder(migrationConnectionString);

        var runtimeUser = runtimeBuilder.Username?.Trim();
        var runtimePassword = runtimeBuilder.Password;
        var migrationUser = migrationBuilder.Username?.Trim();

        if (string.IsNullOrWhiteSpace(runtimeUser) ||
            string.IsNullOrWhiteSpace(runtimePassword) ||
            string.IsNullOrWhiteSpace(migrationUser) ||
            string.Equals(runtimeUser, migrationUser, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(runtimeUser, "postgres", StringComparison.OrdinalIgnoreCase))
        {
            await tx.CommitAsync(cancellationToken);
            return;
        }

        // REV-2B1-02 / Lot 2C: Réduction de surface dans les journaux PostgreSQL pendant le provisionnement du rôle.
        // Note de sécurité : log_statement et log_min_duration_statement sont des paramètres de contexte SUSET dans PostgreSQL.
        // Cette instruction prend effet lorsque le rôle de migration dispose de l'attribut superuser (ex: conteneur postgres:18)
        // et est ignorée sans erreur (insufficient_privilege) sous un rôle propriétaire non-superutilisateur.
        // Elle constitue une défense en profondeur locale à la transaction et non une garantie universelle contre un audit externe.
        const string suppressStatementLoggingSql = @"
DO $$
BEGIN
    EXECUTE 'SET LOCAL log_statement = ''none''';
    EXECUTE 'SET LOCAL log_min_duration_statement = -1';
EXCEPTION
    WHEN insufficient_privilege THEN
        NULL;
END $$;";
        await migrationDb.Database.ExecuteSqlRawAsync(suppressStatementLoggingSql, cancellationToken);

        // Injection paramétrée des identifiants en variables de transaction locales (is_local = true)
        // sans aucune interpolation de chaîne SQL non fiable
        var pRuntimeUser = new NpgsqlParameter("runtimeUser", runtimeUser);
        var pRuntimePass = new NpgsqlParameter("runtimePass", runtimePassword);
        var pMigrationUser = new NpgsqlParameter("migrationUser", migrationUser);

        await migrationDb.Database.ExecuteSqlRawAsync(
            "SELECT set_config('janus.runtime_user', @runtimeUser, true), set_config('janus.runtime_password', @runtimePass, true), set_config('janus.admin_user', @migrationUser, true);",
            [pRuntimeUser, pRuntimePass, pMigrationUser],
            cancellationToken);

        const string provisionRoleAndGrantsSql = @"
DO $$
DECLARE
    v_runtime_user text := current_setting('janus.runtime_user', true);
    v_runtime_password text := current_setting('janus.runtime_password', true);
    v_admin_user text := current_setting('janus.admin_user', true);
    v_seq text;
    v_sequences text[] := ARRAY[
        'Cards_Id_seq',
        'Validations_Id_seq',
        'FareTransactions_Id_seq',
        'CardAuditLogs_Id_seq'
    ];
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = v_runtime_user) THEN
        EXECUTE format(
            'CREATE ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOCREATEDB NOCREATEROLE NOSUPERUSER NOBYPASSRLS NOREPLICATION',
            v_runtime_user,
            v_runtime_password
        );
    ELSE
        EXECUTE format(
            'ALTER ROLE %I WITH LOGIN PASSWORD %L NOINHERIT NOCREATEDB NOCREATEROLE NOSUPERUSER NOBYPASSRLS NOREPLICATION',
            v_runtime_user,
            v_runtime_password
        );
    END IF;

    -- Effacement immédiat du secret de la configuration de transaction
    PERFORM set_config('janus.runtime_password', '', true);

    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE 'REVOKE ALL ON SCHEMA public FROM PUBLIC';
    EXECUTE format('REVOKE CREATE ON SCHEMA public FROM %I', v_runtime_user);

    EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), v_runtime_user);
    EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', v_runtime_user);

    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON TABLES FROM PUBLIC', v_admin_user);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON SEQUENCES FROM PUBLIC', v_admin_user);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public REVOKE ALL ON FUNCTIONS FROM PUBLIC', v_admin_user);
    EXECUTE format('ALTER DEFAULT PRIVILEGES FOR ROLE %I IN SCHEMA public GRANT USAGE, SELECT ON SEQUENCES TO %I', v_admin_user, v_runtime_user);

    IF to_regclass('public.""__EFMigrationsHistory""') IS NOT NULL THEN
        EXECUTE 'REVOKE ALL ON TABLE public.""__EFMigrationsHistory"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""__EFMigrationsHistory"" FROM %I', v_runtime_user);
    END IF;

    IF to_regclass('public.""Cards""') IS NOT NULL THEN
        EXECUTE 'REVOKE ALL ON TABLE public.""Cards"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""Cards"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON TABLE public.""Cards"" TO %I', v_runtime_user);
    END IF;

    IF to_regclass('public.""Validations""') IS NOT NULL THEN
        EXECUTE 'REVOKE ALL ON TABLE public.""Validations"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""Validations"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""Validations"" TO %I', v_runtime_user);
    END IF;

    IF to_regclass('public.""FareTransactions""') IS NOT NULL THEN
        EXECUTE 'REVOKE ALL ON TABLE public.""FareTransactions"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""FareTransactions"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""FareTransactions"" TO %I', v_runtime_user);
    END IF;

    IF to_regclass('public.""CardAuditLogs""') IS NOT NULL THEN
        EXECUTE 'REVOKE ALL ON TABLE public.""CardAuditLogs"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""CardAuditLogs"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""CardAuditLogs"" TO %I', v_runtime_user);
    END IF;

    FOREACH v_seq IN ARRAY v_sequences LOOP
        IF to_regclass(format('public.%I', v_seq)) IS NOT NULL THEN
            EXECUTE format('REVOKE ALL ON SEQUENCE public.%I FROM PUBLIC', v_seq);
            EXECUTE format('REVOKE ALL ON SEQUENCE public.%I FROM %I', v_seq, v_runtime_user);
            EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.%I TO %I', v_seq, v_runtime_user);
        END IF;
    END LOOP;
END $$;
";
        await migrationDb.Database.ExecuteSqlRawAsync(provisionRoleAndGrantsSql, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        }
        finally
        {
            ProvisionSemaphore.Release();
        }
    }
}
