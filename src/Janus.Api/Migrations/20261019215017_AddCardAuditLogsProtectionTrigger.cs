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
            // JANUS-SEC-006 / Lot 2B: Trigger de défense en profondeur empêchant toute modification ou suppression dans CardAuditLogs
            migrationBuilder.Sql(@"
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

-- Configuration idempotente du rôle moindre privilège et attribution des droits DML stricts
DO $$
BEGIN
    BEGIN
        IF NOT EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'janus_runtime') THEN
            CREATE ROLE janus_runtime WITH LOGIN PASSWORD 'local-dev-runtime-password-change-me' NOINHERIT NOCREATEDB NOCREATEROLE NOSUPERUSER;
        END IF;
    EXCEPTION WHEN insufficient_privilege THEN
        NULL;
    END;

    IF EXISTS (SELECT FROM pg_catalog.pg_roles WHERE rolname = 'janus_runtime') THEN
        EXECUTE 'REVOKE CREATE ON SCHEMA public FROM PUBLIC';
        EXECUTE 'GRANT CONNECT ON DATABASE ' || quote_ident(current_database()) || ' TO janus_runtime';
        EXECUTE 'GRANT USAGE ON SCHEMA public TO janus_runtime';

        EXECUTE 'GRANT SELECT, INSERT, UPDATE ON TABLE ""Cards"" TO janus_runtime';
        EXECUTE 'GRANT SELECT, INSERT ON TABLE ""Validations"" TO janus_runtime';
        EXECUTE 'GRANT SELECT, INSERT ON TABLE ""FareTransactions"" TO janus_runtime';
        EXECUTE 'GRANT SELECT, INSERT ON TABLE ""CardAuditLogs"" TO janus_runtime';

        EXECUTE 'GRANT USAGE, SELECT ON SEQUENCE ""Cards_Id_seq"" TO janus_runtime';
        EXECUTE 'GRANT USAGE, SELECT ON SEQUENCE ""Validations_Id_seq"" TO janus_runtime';
        EXECUTE 'GRANT USAGE, SELECT ON SEQUENCE ""FareTransactions_Id_seq"" TO janus_runtime';
        EXECUTE 'GRANT USAGE, SELECT ON SEQUENCE ""CardAuditLogs_Id_seq"" TO janus_runtime';
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_protect_card_audit_logs ON ""CardAuditLogs"";
DROP FUNCTION IF EXISTS prevent_card_audit_logs_tampering();
");
        }
    }
}
