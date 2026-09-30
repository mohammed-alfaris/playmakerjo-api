using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPermanentLink : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "permanent_booking_id",
                table: "bookings",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_permanent_booking_id_date",
                table: "bookings",
                columns: new[] { "permanent_booking_id", "date" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bookings_permanent_booking_id_date",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "permanent_booking_id",
                table: "bookings");
        }
    }
}
