using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingIsManual : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "is_manual",
                table: "bookings",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "is_manual",
                table: "bookings");
        }
    }
}
