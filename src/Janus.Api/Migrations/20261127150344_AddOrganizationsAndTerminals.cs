using System;
using Janus.Domain.Organizations;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Janus.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddOrganizationsAndTerminals : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // LOT-5A : verrou consultatif partagé avec les migrations précédentes (sérialise les migrateurs concurrents).
            migrationBuilder.Sql("SELECT pg_advisory_xact_lock(20261019, 2);");

            migrationBuilder.CreateTable(
                name: "Organizations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Organizations", x => x.Id);
                    table.CheckConstraint("CK_Organizations_Code", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{1,31}$'");
                    table.CheckConstraint("CK_Organizations_Status", "\"Status\" IN (0, 1)");
                });

            // Organisation de reprise à Id fixe, puis séquence d'identité positionnée après elle.
            migrationBuilder.Sql($@"
INSERT INTO ""Organizations"" (""Id"", ""Code"", ""Name"", ""Status"", ""CreatedAt"")
VALUES ({OrganizationDefaults.DemoOrganizationId}, '{OrganizationDefaults.DemoOrganizationCode}', '{OrganizationDefaults.DemoOrganizationName}', 0, now());
SELECT setval(pg_get_serial_sequence('""Organizations""', 'Id'), (SELECT MAX(""Id"") FROM ""Organizations""), true);
");

            // Rattachement des données existantes SANS UPDATE : sur PostgreSQL >= 11, ADD COLUMN ... NOT NULL DEFAULT <constante>
            // enregistre la valeur dans le catalogue (pas de réécriture de table, aucun trigger de ligne déclenché), ce qui
            // respecte les triggers append-only de CardAuditLogs et WalletLedgerEntries. Le DEFAULT est ensuite supprimé :
            // toute nouvelle ligne doit fournir explicitement son organisation.
            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "WalletLedgerEntries",
                type: "integer",
                nullable: false,
                defaultValue: OrganizationDefaults.DemoOrganizationId);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "Validations",
                type: "integer",
                nullable: false,
                defaultValue: OrganizationDefaults.DemoOrganizationId);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "FareTransactions",
                type: "integer",
                nullable: false,
                defaultValue: OrganizationDefaults.DemoOrganizationId);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "Cards",
                type: "integer",
                nullable: false,
                defaultValue: OrganizationDefaults.DemoOrganizationId);

            migrationBuilder.AddColumn<int>(
                name: "OrganizationId",
                table: "CardAuditLogs",
                type: "integer",
                nullable: false,
                defaultValue: OrganizationDefaults.DemoOrganizationId);

            migrationBuilder.Sql(@"
ALTER TABLE ""Cards"" ALTER COLUMN ""OrganizationId"" DROP DEFAULT;
ALTER TABLE ""Validations"" ALTER COLUMN ""OrganizationId"" DROP DEFAULT;
ALTER TABLE ""FareTransactions"" ALTER COLUMN ""OrganizationId"" DROP DEFAULT;
ALTER TABLE ""WalletLedgerEntries"" ALTER COLUMN ""OrganizationId"" DROP DEFAULT;
ALTER TABLE ""CardAuditLogs"" ALTER COLUMN ""OrganizationId"" DROP DEFAULT;
");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_Cards_Id_OrganizationId",
                table: "Cards",
                columns: new[] { "Id", "OrganizationId" });


            migrationBuilder.CreateTable(
                name: "Terminals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Label = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedBy = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Terminals", x => x.Id);
                    table.CheckConstraint("CK_Terminals_Code", "\"Code\" ~ '^[A-Z0-9][A-Z0-9-]{2,79}$'");
                    table.CheckConstraint("CK_Terminals_ExpiresAfterCreation", "\"ExpiresAt\" > \"CreatedAt\"");
                    table.CheckConstraint("CK_Terminals_RevocationConsistency", "(\"Status\" = 0 AND \"RevokedAt\" IS NULL AND \"RevokedBy\" IS NULL AND \"RevocationReason\" IS NULL) OR (\"Status\" = 1 AND \"RevokedAt\" IS NOT NULL AND \"RevokedBy\" IS NOT NULL AND \"RevocationReason\" IS NOT NULL)");
                    table.CheckConstraint("CK_Terminals_Status", "\"Status\" IN (0, 1)");
                    table.CheckConstraint("CK_Terminals_TokenHash", "\"TokenHash\" ~ '^[0-9A-F]{64}$'");
                    table.ForeignKey(
                        name: "FK_Terminals_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TerminalAuditLogs",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    OrganizationId = table.Column<int>(type: "integer", nullable: false),
                    TerminalId = table.Column<int>(type: "integer", nullable: false),
                    TerminalCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Action = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    TokenExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Actor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TerminalAuditLogs", x => x.Id);
                    table.CheckConstraint("CK_TerminalAuditLogs_Action", "\"Action\" IN ('TERMINAL_ENROLLED', 'TERMINAL_REVOKED')");
                    table.CheckConstraint("CK_TerminalAuditLogs_RevocationReason", "\"Action\" <> 'TERMINAL_REVOKED' OR \"Reason\" IS NOT NULL");
                    table.ForeignKey(
                        name: "FK_TerminalAuditLogs_Organizations_OrganizationId",
                        column: x => x.OrganizationId,
                        principalTable: "Organizations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TerminalAuditLogs_Terminals_TerminalId",
                        column: x => x.TerminalId,
                        principalTable: "Terminals",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_CardId_OrganizationId",
                table: "WalletLedgerEntries",
                columns: new[] { "CardId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_OrganizationId",
                table: "WalletLedgerEntries",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Validations_OrganizationId",
                table: "Validations",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_FareTransactions_CardId_OrganizationId",
                table: "FareTransactions",
                columns: new[] { "CardId", "OrganizationId" });

            migrationBuilder.CreateIndex(
                name: "IX_FareTransactions_OrganizationId",
                table: "FareTransactions",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Cards_OrganizationId",
                table: "Cards",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_CardAuditLogs_OrganizationId",
                table: "CardAuditLogs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Organizations_Code",
                table: "Organizations",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TerminalAuditLogs_OrganizationId",
                table: "TerminalAuditLogs",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_TerminalAuditLogs_TerminalId_Id",
                table: "TerminalAuditLogs",
                columns: new[] { "TerminalId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_Code",
                table: "Terminals",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_OrganizationId",
                table: "Terminals",
                column: "OrganizationId");

            migrationBuilder.CreateIndex(
                name: "IX_Terminals_TokenHash",
                table: "Terminals",
                column: "TokenHash",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_CardAuditLogs_Organizations_OrganizationId",
                table: "CardAuditLogs",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Cards_Organizations_OrganizationId",
                table: "Cards",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FareTransactions_Cards_CardId_OrganizationId",
                table: "FareTransactions",
                columns: new[] { "CardId", "OrganizationId" },
                principalTable: "Cards",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_FareTransactions_Organizations_OrganizationId",
                table: "FareTransactions",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Validations_Organizations_OrganizationId",
                table: "Validations",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WalletLedgerEntries_Cards_CardId_OrganizationId",
                table: "WalletLedgerEntries",
                columns: new[] { "CardId", "OrganizationId" },
                principalTable: "Cards",
                principalColumns: new[] { "Id", "OrganizationId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_WalletLedgerEntries_Organizations_OrganizationId",
                table: "WalletLedgerEntries",
                column: "OrganizationId",
                principalTable: "Organizations",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.Sql(ProtectionAndGrantsSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_protect_card_identity ON ""Cards"";
DROP FUNCTION IF EXISTS protect_card_identity();
");

            migrationBuilder.DropForeignKey(
                name: "FK_CardAuditLogs_Organizations_OrganizationId",
                table: "CardAuditLogs");

            migrationBuilder.DropForeignKey(
                name: "FK_Cards_Organizations_OrganizationId",
                table: "Cards");

            migrationBuilder.DropForeignKey(
                name: "FK_FareTransactions_Cards_CardId_OrganizationId",
                table: "FareTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_FareTransactions_Organizations_OrganizationId",
                table: "FareTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_Validations_Organizations_OrganizationId",
                table: "Validations");

            migrationBuilder.DropForeignKey(
                name: "FK_WalletLedgerEntries_Cards_CardId_OrganizationId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_WalletLedgerEntries_Organizations_OrganizationId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropTable(
                name: "TerminalAuditLogs");

            migrationBuilder.DropTable(
                name: "Terminals");

            migrationBuilder.Sql(@"
DROP FUNCTION IF EXISTS protect_terminal_identity();
DROP FUNCTION IF EXISTS prevent_terminal_deletion();
DROP FUNCTION IF EXISTS prevent_terminal_audit_logs_tampering();
");

            migrationBuilder.DropTable(
                name: "Organizations");

            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_CardId_OrganizationId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_WalletLedgerEntries_OrganizationId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropIndex(
                name: "IX_Validations_OrganizationId",
                table: "Validations");

            migrationBuilder.DropIndex(
                name: "IX_FareTransactions_CardId_OrganizationId",
                table: "FareTransactions");

            migrationBuilder.DropIndex(
                name: "IX_FareTransactions_OrganizationId",
                table: "FareTransactions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_Cards_Id_OrganizationId",
                table: "Cards");

            migrationBuilder.DropIndex(
                name: "IX_Cards_OrganizationId",
                table: "Cards");

            migrationBuilder.DropIndex(
                name: "IX_CardAuditLogs_OrganizationId",
                table: "CardAuditLogs");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "WalletLedgerEntries");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Validations");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "FareTransactions");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "Cards");

            migrationBuilder.DropColumn(
                name: "OrganizationId",
                table: "CardAuditLogs");
        }
        // Triggers d'intégrité et droits minimaux du rôle runtime. Les triggers protègent contre l'erreur et l'accident
        // (y compris via janus_app) ; ils ne protègent pas contre un superutilisateur ou le propriétaire qui les désactiverait.
        private const string ProtectionAndGrantsSql = @"
CREATE OR REPLACE FUNCTION protect_card_identity()
RETURNS TRIGGER AS $$
BEGIN
    IF NEW.""Id"" IS DISTINCT FROM OLD.""Id"" OR NEW.""Uid"" IS DISTINCT FROM OLD.""Uid""
       OR NEW.""OrganizationId"" IS DISTINCT FROM OLD.""OrganizationId"" THEN
        RAISE EXCEPTION 'Card identity columns (Id, Uid, OrganizationId) are immutable.'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_card_identity ON ""Cards"";
CREATE TRIGGER trg_protect_card_identity
BEFORE UPDATE ON ""Cards""
FOR EACH ROW
EXECUTE FUNCTION protect_card_identity();

CREATE OR REPLACE FUNCTION protect_terminal_identity()
RETURNS TRIGGER AS $$
BEGIN
    IF OLD.""Status"" = 1 THEN
        RAISE EXCEPTION 'Terminal revocation is irreversible.'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    IF NEW.""Id"" IS DISTINCT FROM OLD.""Id"" OR NEW.""OrganizationId"" IS DISTINCT FROM OLD.""OrganizationId""
       OR NEW.""Code"" IS DISTINCT FROM OLD.""Code"" OR NEW.""TokenHash"" IS DISTINCT FROM OLD.""TokenHash""
       OR NEW.""ExpiresAt"" IS DISTINCT FROM OLD.""ExpiresAt"" OR NEW.""CreatedAt"" IS DISTINCT FROM OLD.""CreatedAt""
       OR NEW.""CreatedBy"" IS DISTINCT FROM OLD.""CreatedBy"" THEN
        RAISE EXCEPTION 'Terminal identity columns are immutable.'
            USING ERRCODE = 'integrity_constraint_violation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_terminal_identity ON ""Terminals"";
CREATE TRIGGER trg_protect_terminal_identity
BEFORE UPDATE ON ""Terminals""
FOR EACH ROW
EXECUTE FUNCTION protect_terminal_identity();

CREATE OR REPLACE FUNCTION prevent_terminal_deletion()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'Terminals cannot be deleted; revoke them instead.'
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_prevent_terminal_delete ON ""Terminals"";
CREATE TRIGGER trg_prevent_terminal_delete
BEFORE DELETE ON ""Terminals""
FOR EACH ROW
EXECUTE FUNCTION prevent_terminal_deletion();

DROP TRIGGER IF EXISTS trg_prevent_terminal_truncate ON ""Terminals"";
CREATE TRIGGER trg_prevent_terminal_truncate
BEFORE TRUNCATE ON ""Terminals""
FOR EACH STATEMENT
EXECUTE FUNCTION prevent_terminal_deletion();

CREATE OR REPLACE FUNCTION prevent_terminal_audit_logs_tampering()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'TerminalAuditLogs is append-only. Modifications and deletions are strictly prohibited.'
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_terminal_audit_logs ON ""TerminalAuditLogs"";
CREATE TRIGGER trg_protect_terminal_audit_logs
BEFORE UPDATE OR DELETE ON ""TerminalAuditLogs""
FOR EACH ROW
EXECUTE FUNCTION prevent_terminal_audit_logs_tampering();

DROP TRIGGER IF EXISTS trg_protect_terminal_audit_logs_truncate ON ""TerminalAuditLogs"";
CREATE TRIGGER trg_protect_terminal_audit_logs_truncate
BEFORE TRUNCATE ON ""TerminalAuditLogs""
FOR EACH STATEMENT
EXECUTE FUNCTION prevent_terminal_audit_logs_tampering();

DO $$
DECLARE
    v_runtime_user text := COALESCE(NULLIF(current_setting('janus.runtime_user', true), ''), 'janus_runtime');
BEGIN
    EXECUTE 'REVOKE ALL ON TABLE public.""Organizations"", public.""Terminals"", public.""TerminalAuditLogs"" FROM PUBLIC';
    EXECUTE 'REVOKE ALL ON SEQUENCE public.""Organizations_Id_seq"", public.""Terminals_Id_seq"", public.""TerminalAuditLogs_Id_seq"" FROM PUBLIC';

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = v_runtime_user) THEN
        EXECUTE format('REVOKE ALL ON TABLE public.""Organizations"", public.""Terminals"", public.""TerminalAuditLogs"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""Organizations"", public.""Terminals"", public.""TerminalAuditLogs"" TO %I', v_runtime_user);
        EXECUTE format('GRANT UPDATE (""Status"", ""RevokedAt"", ""RevokedBy"", ""RevocationReason"") ON TABLE public.""Terminals"" TO %I', v_runtime_user);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.""Organizations_Id_seq"", public.""Terminals_Id_seq"", public.""TerminalAuditLogs_Id_seq"" TO %I', v_runtime_user);
        -- Cards : plus aucun UPDATE de table entière, uniquement les colonnes modifiables (Uid et OrganizationId immuables).
        EXECUTE format('REVOKE UPDATE ON TABLE public.""Cards"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT UPDATE (""Status"", ""Label"", ""BalanceMillimes"") ON TABLE public.""Cards"" TO %I', v_runtime_user);
    END IF;
END $$;
";
    }
}
