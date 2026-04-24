using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPerSportConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // NOTE: Combines the fields from the (lost) AddSubdividablePitch
            // migration with the genuinely-new per-sport config columns. On a
            // freshly-seeded DB these are all new; on a DB that somehow has
            // the subdividable columns already, the IF NOT EXISTS guards keep
            // this safe to re-apply. The waitlist columns/tables were already
            // applied by the earlier AddWaitlistTables migration, so they are
            // not re-added here.

            migrationBuilder.AddColumn<string>(
                name: "parent_size",
                table: "venues",
                type: "varchar(8)",
                maxLength: 8,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "sub_sizes",
                table: "venues",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "size_prices",
                table: "venues",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "pitch_size",
                table: "bookings",
                type: "varchar(8)",
                maxLength: 8,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "sports_config",
                table: "venues",
                type: "longtext",
                nullable: false,
                defaultValue: "")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<bool>(
                name: "sports_isolated",
                table: "venues",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "parent_size", table: "venues");
            migrationBuilder.DropColumn(name: "sub_sizes",   table: "venues");
            migrationBuilder.DropColumn(name: "size_prices", table: "venues");
            migrationBuilder.DropColumn(name: "pitch_size",  table: "bookings");
            migrationBuilder.DropColumn(name: "sports_config",   table: "venues");
            migrationBuilder.DropColumn(name: "sports_isolated", table: "venues");
        }
    }
}
