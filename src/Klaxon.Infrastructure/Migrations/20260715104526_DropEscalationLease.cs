using Microsoft.EntityFrameworkCore.Migrations;
using NodaTime;

#nullable disable

namespace Klaxon.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class DropEscalationLease : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LeaseUntil",
                table: "Escalations");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Instant>(
                name: "LeaseUntil",
                table: "Escalations",
                type: "timestamp with time zone",
                nullable: true);
        }
    }
}
