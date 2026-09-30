using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCancellationPolicy : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "free_cancel_hours",
                table: "venues",
                type: "int",
                nullable: false,
                // Existing venues get the standard rule (free until 24h before), not 0 — which
                // would mean "always free" and refund every cancellation.
                defaultValue: 24);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "free_cancel_hours",
                table: "venues");
        }
    }
}
