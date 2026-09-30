using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddVenueFeatures : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Both venue columns default to "[]", not EF's generated no-default: MySQL would
            // otherwise backfill every existing venue with "" — the empty-JSON rows that
            // AddPerSportConfig left behind and that forced tolerant getters. Same hand edit as
            // AddVenuePitches.
            migrationBuilder.AddColumn<string>(
                name: "custom_features",
                table: "venues",
                type: "longtext",
                nullable: false,
                defaultValue: "[]")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "feature_ids",
                table: "venues",
                type: "longtext",
                nullable: false,
                defaultValue: "[]")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "venue_features",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name_en = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name_ar = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    icon = table.Column<string>(type: "varchar(40)", maxLength: 40, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    sort_order = table.Column<int>(type: "int", nullable: false),
                    is_active = table.Column<bool>(type: "tinyint(1)", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_venue_features", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_venue_features_name_ar",
                table: "venue_features",
                column: "name_ar",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_venue_features_name_en",
                table: "venue_features",
                column: "name_en",
                unique: true);

            // A starter catalog, so the owner picker and the app filter are not empty the day
            // this ships. Inserted here rather than by a startup "insert if missing" check like
            // PlatformSettings: that would resurrect any of these an admin deliberately deleted,
            // on every restart. A migration runs once.
            var seeded = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc);
            migrationBuilder.InsertData(
                table: "venue_features",
                columns: new[] { "id", "name_en", "name_ar", "icon", "sort_order", "is_active", "created_at", "updated_at" },
                values: new object[,]
                {
                    { "vf-parking",        "Parking",           "موقف سيارات",      "parking",       10,  true, seeded, seeded },
                    { "vf-showers",        "Showers",           "دشّات",             "shower",        20,  true, seeded, seeded },
                    { "vf-changing-rooms", "Changing rooms",    "غرف تبديل الملابس", "changing_room", 30,  true, seeded, seeded },
                    { "vf-lockers",        "Lockers",           "خزائن",             "locker",        40,  true, seeded, seeded },
                    { "vf-floodlights",    "Floodlights",       "إضاءة الملعب",      "floodlights",   50,  true, seeded, seeded },
                    { "vf-wifi",           "Wi-Fi",             "واي فاي",           "wifi",          60,  true, seeded, seeded },
                    { "vf-cafe",           "Café",              "كافيه",             "cafe",          70,  true, seeded, seeded },
                    { "vf-drinking-water", "Drinking water",    "مياه شرب",          "water",         80,  true, seeded, seeded },
                    { "vf-seating",        "Spectator seating", "مقاعد للجمهور",     "seating",       90,  true, seeded, seeded },
                    { "vf-prayer-room",    "Prayer room",       "مصلى",              "prayer_room",   100, true, seeded, seeded },
                    { "vf-first-aid",      "First aid",         "إسعافات أولية",     "first_aid",     110, true, seeded, seeded },
                    { "vf-restrooms",      "Restrooms",         "دورات مياه",        "restroom",      120, true, seeded, seeded },
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "venue_features");

            migrationBuilder.DropColumn(
                name: "custom_features",
                table: "venues");

            migrationBuilder.DropColumn(
                name: "feature_ids",
                table: "venues");
        }
    }
}
