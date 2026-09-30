using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBookingPaymentDeadline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "auto_cancelled_at",
                table: "bookings",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "payment_deadline_at",
                table: "bookings",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_payment_deadline_at_status",
                table: "bookings",
                columns: new[] { "payment_deadline_at", "status" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_bookings_payment_deadline_at_status",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "auto_cancelled_at",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "payment_deadline_at",
                table: "bookings");
        }
    }
}
