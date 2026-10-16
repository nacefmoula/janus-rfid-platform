using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Janus.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddAntiPassbackIndexOnValidations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Validations_TerminalId_CardUid_Decision_ReceivedAt",
                table: "Validations",
                columns: new[] { "TerminalId", "CardUid", "Decision", "ReceivedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Validations_TerminalId_CardUid_Decision_ReceivedAt",
                table: "Validations");
        }
    }
}
