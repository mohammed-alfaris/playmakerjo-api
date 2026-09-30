using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddWaitlistTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // This migration shipped without its .Designer.cs, so EF never discovered it: a
            // database built from migrations had no waitlist tables and the website's sign-up
            // forms failed there. Production has the tables anyway (created before the file lost
            // its attributes), so now that it is discoverable again it must not trip over them —
            // hence raw CREATE TABLE IF NOT EXISTS instead of CreateTable. The shape is exactly
            // the one the original CreateTable calls produced.
            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS `player_waitlist` (
    `id` int NOT NULL AUTO_INCREMENT,
    `email` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `created_at` datetime(6) NOT NULL,
    CONSTRAINT `PK_player_waitlist` PRIMARY KEY (`id`),
    UNIQUE INDEX `IX_player_waitlist_email` (`email`)
) CHARACTER SET=utf8mb4;");

            migrationBuilder.Sql(@"
CREATE TABLE IF NOT EXISTS `venue_waitlist` (
    `id` int NOT NULL AUTO_INCREMENT,
    `contact_name` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `venue_name` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `city` varchar(100) CHARACTER SET utf8mb4 NOT NULL,
    `phone` varchar(30) CHARACTER SET utf8mb4 NOT NULL,
    `email` varchar(255) CHARACTER SET utf8mb4 NOT NULL,
    `sports` longtext CHARACTER SET utf8mb4 NOT NULL,
    `created_at` datetime(6) NOT NULL,
    CONSTRAINT `PK_venue_waitlist` PRIMARY KEY (`id`),
    UNIQUE INDEX `IX_venue_waitlist_email` (`email`)
) CHARACTER SET=utf8mb4;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP TABLE IF EXISTS `player_waitlist`;");
            migrationBuilder.Sql("DROP TABLE IF EXISTS `venue_waitlist`;");
        }
    }
}
