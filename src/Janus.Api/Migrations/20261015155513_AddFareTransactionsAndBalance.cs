using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Janus.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddFareTransactionsAndBalance : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BalanceMillimes",
                table: "Cards",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "FareTransactions",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CardId = table.Column<int>(type: "integer", nullable: false),
                    CardUid = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    TerminalId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EventId = table.Column<Guid>(type: "uuid", nullable: false),
                    AmountMillimes = table.Column<int>(type: "integer", nullable: false),
                    BalanceAfterMillimes = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FareTransactions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FareTransactions_Cards_CardId",
                        column: x => x.CardId,
                        principalTable: "Cards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_FareTransactions_CardId",
                table: "FareTransactions",
                column: "CardId");

            migrationBuilder.CreateIndex(
                name: "IX_FareTransactions_TerminalId_EventId",
                table: "FareTransactions",
                columns: new[] { "TerminalId", "EventId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "FareTransactions");

            migrationBuilder.DropColumn(
                name: "BalanceMillimes",
                table: "Cards");
        }
    }
}
