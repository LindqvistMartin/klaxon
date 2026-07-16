using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Klaxon.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddEscalationAlertIdIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Escalations_AlertId",
                table: "Escalations",
                column: "AlertId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Escalations_AlertId",
                table: "Escalations");
        }
    }
}
