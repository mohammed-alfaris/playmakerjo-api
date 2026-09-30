using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "converted_owner_id",
                table: "venue_waitlist",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "lost_reason",
                table: "venue_waitlist",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "next_follow_up_on",
                table: "venue_waitlist",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "notes",
                table: "venue_waitlist",
                type: "text",
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "status",
                table: "venue_waitlist",
                type: "varchar(12)",
                maxLength: 12,
                nullable: false,
                // Hand-edited from EF's "": leads that already exist start at the first stage.
                defaultValue: "new")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "updated_at",
                table: "venue_waitlist",
                type: "datetime(6)",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "converted_owner_id",
                table: "venue_waitlist");

            migrationBuilder.DropColumn(
                name: "lost_reason",
                table: "venue_waitlist");

            migrationBuilder.DropColumn(
                name: "next_follow_up_on",
                table: "venue_waitlist");

            migrationBuilder.DropColumn(
                name: "notes",
                table: "venue_waitlist");

            migrationBuilder.DropColumn(
                name: "status",
                table: "venue_waitlist");

            migrationBuilder.DropColumn(
                name: "updated_at",
                table: "venue_waitlist");
        }
    }
}
