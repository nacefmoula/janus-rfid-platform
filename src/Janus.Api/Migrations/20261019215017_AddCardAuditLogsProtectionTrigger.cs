using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Janus.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddCardAuditLogsProtectionTrigger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // JANUS-SEC-006 / Lot 2B & 2B.1: Triggers de défense en profondeur empêchant toute modification,
            // suppression unitaire (UPDATE/DELETE) ou vidage massif (TRUNCATE) dans CardAuditLogs.
            // Aucun secret ni mot de passe n'est figé dans la migration versionnée.
            migrationBuilder.Sql(@"
SELECT pg_advisory_xact_lock(20261019, 2);

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

-- Attribution idempotente des droits DML stricts si le rôle runtime a déjà été provisionné
DO $$
DECLARE
    v_runtime_user text := COALESCE(NULLIF(current_setting('janus.runtime_user', true), ''), 'janus_runtime');
BEGIN
    EXECUTE format('REVOKE ALL ON DATABASE %I FROM PUBLIC', current_database());
    EXECUTE 'REVOKE ALL ON SCHEMA public FROM PUBLIC';

    IF to_regclass('public.""__EFMigrationsHistory""') IS NOT NULL THEN
        EXECUTE 'REVOKE ALL ON TABLE public.""__EFMigrationsHistory"" FROM PUBLIC';
    END IF;

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = v_runtime_user) THEN
        EXECUTE format('REVOKE CREATE ON SCHEMA public FROM %I', v_runtime_user);
        EXECUTE format('GRANT CONNECT ON DATABASE %I TO %I', current_database(), v_runtime_user);
        EXECUTE format('GRANT USAGE ON SCHEMA public TO %I', v_runtime_user);

        IF to_regclass('public.""__EFMigrationsHistory""') IS NOT NULL THEN
            EXECUTE format('REVOKE ALL ON TABLE public.""__EFMigrationsHistory"" FROM %I', v_runtime_user);
        END IF;

        EXECUTE 'REVOKE ALL ON TABLE public.""Cards"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""Cards"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT, UPDATE ON TABLE public.""Cards"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON TABLE public.""Validations"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""Validations"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""Validations"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON TABLE public.""FareTransactions"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""FareTransactions"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""FareTransactions"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON TABLE public.""CardAuditLogs"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON TABLE public.""CardAuditLogs"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""CardAuditLogs"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON SEQUENCE public.""Cards_Id_seq"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON SEQUENCE public.""Cards_Id_seq"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.""Cards_Id_seq"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON SEQUENCE public.""Validations_Id_seq"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON SEQUENCE public.""Validations_Id_seq"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.""Validations_Id_seq"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON SEQUENCE public.""FareTransactions_Id_seq"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON SEQUENCE public.""FareTransactions_Id_seq"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.""FareTransactions_Id_seq"" TO %I', v_runtime_user);

        EXECUTE 'REVOKE ALL ON SEQUENCE public.""CardAuditLogs_Id_seq"" FROM PUBLIC';
        EXECUTE format('REVOKE ALL ON SEQUENCE public.""CardAuditLogs_Id_seq"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.""CardAuditLogs_Id_seq"" TO %I', v_runtime_user);
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_protect_card_audit_logs_truncate ON ""CardAuditLogs"";
DROP TRIGGER IF EXISTS trg_protect_card_audit_logs ON ""CardAuditLogs"";
DROP FUNCTION IF EXISTS prevent_card_audit_logs_tampering();
");
        }
    }
}
