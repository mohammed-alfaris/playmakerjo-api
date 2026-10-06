using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddWebBooking : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "slug",
                table: "venues",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "large_venue_min_pitches",
                table: "platform_settings",
                type: "int",
                nullable: false,
                defaultValue: 3);

            migrationBuilder.AddColumn<double>(
                name: "price_large_venue",
                table: "platform_settings",
                type: "double",
                nullable: false,
                defaultValue: 75.0);

            migrationBuilder.AddColumn<double>(
                name: "price_small_venue",
                table: "platform_settings",
                type: "double",
                nullable: false,
                defaultValue: 50.0);

            migrationBuilder.AddColumn<double>(
                name: "price_large_venue",
                table: "companies",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "price_small_venue",
                table: "companies",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "public_token",
                table: "bookings",
                type: "varchar(64)",
                maxLength: 64,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "source",
                table: "bookings",
                type: "varchar(16)",
                maxLength: 16,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // Every existing venue starts with its id as its public link; owners can change it.
            migrationBuilder.Sql("UPDATE venues SET slug = id WHERE slug IS NULL;");

            // No PlayMaker commission until the app's public launch (decided 2026-10-06). New app
            // bookings carry a 0% fee; bookings already made keep the fee they were made with.
            // Re-enable from Settings at launch.
            migrationBuilder.Sql("UPDATE platform_settings SET platform_fee_percentage = 0;");

            migrationBuilder.CreateIndex(
                name: "IX_venues_slug",
                table: "venues",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_bookings_public_token",
                table: "bookings",
                column: "public_token",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_venues_slug",
                table: "venues");

            migrationBuilder.DropIndex(
                name: "IX_bookings_public_token",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "slug",
                table: "venues");

            migrationBuilder.DropColumn(
                name: "large_venue_min_pitches",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "price_large_venue",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "price_small_venue",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "price_large_venue",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "price_small_venue",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "public_token",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "source",
                table: "bookings");
        }
    }
}
