using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Janus.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddWalletLedger : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "WalletLedgerEntries",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardId = table.Column<int>(type: "integer", nullable: false),
                    CardUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AmountMillimes = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfterMillimes = table.Column<int>(type: "integer", nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    Actor = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Reason = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WalletLedgerEntries", x => x.Id);
                    table.CheckConstraint("CK_WalletLedgerEntries_AmountNonZero", "\"AmountMillimes\" <> 0");
                    table.CheckConstraint("CK_WalletLedgerEntries_BalanceAfterNonNegative", "\"BalanceAfterMillimes\" >= 0");
                    table.CheckConstraint("CK_WalletLedgerEntries_EntryType", "\"EntryType\" IN ('OPENING_BALANCE', 'FARE_DEBIT', 'ADMIN_CREDIT', 'ADMIN_ADJUSTMENT')");
                    table.ForeignKey(
                        name: "FK_WalletLedgerEntries_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_CardId_Id",
                table: "WalletLedgerEntries",
                columns: new[] { "CardId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_WalletLedgerEntries_IdempotencyKey",
                table: "WalletLedgerEntries",
                column: "IdempotencyKey",
                unique: true);

            // LOT-3B / FIN-06 : reprise des soldes existants sous forme d'écritures d'ouverture, afin que
            // l'invariant SUM(AmountMillimes) == Cards.BalanceMillimes soit vrai pour toutes les cartes dès la migration.
            // Puis : journal append-only (triggers) et droits minimaux du rôle runtime (SELECT, INSERT).
            migrationBuilder.Sql(@"
SELECT pg_advisory_xact_lock(20261019, 2);

INSERT INTO ""WalletLedgerEntries"" (""CardId"", ""CardUid"", ""EntryType"", ""AmountMillimes"", ""BalanceAfterMillimes"", ""IdempotencyKey"", ""Actor"", ""Reason"", ""CreatedAt"")
SELECT c.""Id"", c.""Uid"", 'OPENING_BALANCE', c.""BalanceMillimes"", c.""BalanceMillimes"", 'opening:migration:' || c.""Id"", 'MIGRATION', 'Reprise du solde existant (migration AddWalletLedger)', now()
FROM ""Cards"" c
WHERE c.""BalanceMillimes"" <> 0;

CREATE OR REPLACE FUNCTION prevent_wallet_ledger_tampering()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'WalletLedgerEntries is an append-only financial ledger. Modifications and deletions are strictly prohibited.'
        USING ERRCODE = 'integrity_constraint_violation';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_protect_wallet_ledger ON ""WalletLedgerEntries"";
CREATE TRIGGER trg_protect_wallet_ledger
BEFORE UPDATE OR DELETE ON ""WalletLedgerEntries""
FOR EACH ROW
EXECUTE FUNCTION prevent_wallet_ledger_tampering();

DROP TRIGGER IF EXISTS trg_protect_wallet_ledger_truncate ON ""WalletLedgerEntries"";
CREATE TRIGGER trg_protect_wallet_ledger_truncate
BEFORE TRUNCATE ON ""WalletLedgerEntries""
FOR EACH STATEMENT
EXECUTE FUNCTION prevent_wallet_ledger_tampering();

DO $$
DECLARE
    v_runtime_user text := COALESCE(NULLIF(current_setting('janus.runtime_user', true), ''), 'janus_runtime');
BEGIN
    EXECUTE 'REVOKE ALL ON TABLE public.""WalletLedgerEntries"" FROM PUBLIC';
    EXECUTE 'REVOKE ALL ON SEQUENCE public.""WalletLedgerEntries_Id_seq"" FROM PUBLIC';

    IF EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = v_runtime_user) THEN
        EXECUTE format('REVOKE ALL ON TABLE public.""WalletLedgerEntries"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT SELECT, INSERT ON TABLE public.""WalletLedgerEntries"" TO %I', v_runtime_user);
        EXECUTE format('REVOKE ALL ON SEQUENCE public.""WalletLedgerEntries_Id_seq"" FROM %I', v_runtime_user);
        EXECUTE format('GRANT USAGE, SELECT ON SEQUENCE public.""WalletLedgerEntries_Id_seq"" TO %I', v_runtime_user);
    END IF;
END $$;
");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
DROP TRIGGER IF EXISTS trg_protect_wallet_ledger_truncate ON ""WalletLedgerEntries"";
DROP TRIGGER IF EXISTS trg_protect_wallet_ledger ON ""WalletLedgerEntries"";
DROP FUNCTION IF EXISTS prevent_wallet_ledger_tampering();
");

            migrationBuilder.DropTable(
                name: "WalletLedgerEntries");
        }
    }
}
