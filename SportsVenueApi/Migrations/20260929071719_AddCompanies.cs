using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCompanies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Hand edits, and why they matter on a live database:
            //   staff_all_venues defaults TRUE. EF generated false, which would have restricted
            //   every existing clerk to an empty venue list — locking all staff out on deploy.
            //   staff_venue_ids defaults "[]", not the "" MySQL would otherwise backfill.
            // The backfill at the end gives every existing company and clerk exactly the
            // access they had before, so deploying this changes nobody's rights.
            migrationBuilder.AddColumn<bool>(
                name: "staff_all_venues",
                table: "users",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: true);

            migrationBuilder.AddColumn<string>(
                name: "staff_role_id",
                table: "users",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "staff_venue_ids",
                table: "users",
                type: "longtext",
                nullable: false,
                defaultValue: "[]")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<int>(
                name: "default_max_staff",
                table: "platform_settings",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "default_max_venues",
                table: "platform_settings",
                type: "int",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "companies",
                columns: table => new
                {
                    owner_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name_ar = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    max_venues = table.Column<int>(type: "int", nullable: true),
                    max_staff = table.Column<int>(type: "int", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_companies", x => x.owner_id);
                    table.ForeignKey(
                        name: "FK_companies_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "staff_roles",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    owner_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(60)", maxLength: 60, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    permissions = table.Column<string>(type: "longtext", nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_roles", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_users_managed_by_owner_id",
                table: "users",
                column: "managed_by_owner_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_staff_role_id",
                table: "users",
                column: "staff_role_id");

            migrationBuilder.CreateIndex(
                name: "IX_staff_roles_owner_id_name",
                table: "staff_roles",
                columns: new[] { "owner_id", "name" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_users_staff_roles_staff_role_id",
                table: "users",
                column: "staff_role_id",
                principalTable: "staff_roles",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            // ---- Backfill: nobody's access changes ----------------------------------------

            // 1. Every owner becomes a company, named after them, with no limits.
            migrationBuilder.Sql(@"
                INSERT INTO companies (owner_id, name, name_ar, max_venues, max_staff, created_at, updated_at)
                SELECT id, LEFT(name, 120), NULL, NULL, NULL, UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                FROM users WHERE role = 'venue_owner';");

            // 2. Every company gets the two roles that reproduce the old levels exactly:
            //    'write' clerks could do all of Front desk, 'read' clerks all of View only.
            migrationBuilder.Sql(@"
                INSERT INTO staff_roles (id, owner_id, name, permissions, created_at, updated_at)
                SELECT CONCAT('srw_', LEFT(SHA2(id, 256), 24)), id, 'Front desk',
                       '[""bookings.view"", ""bookings.manage"", ""payments.view"", ""payments.record"", ""customers.view"", ""customers.export"", ""customers.manage"", ""standing.view"", ""standing.manage""]', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                FROM users WHERE role = 'venue_owner';");
            migrationBuilder.Sql(@"
                INSERT INTO staff_roles (id, owner_id, name, permissions, created_at, updated_at)
                SELECT CONCAT('srr_', LEFT(SHA2(id, 256), 24)), id, 'View only',
                       '[""bookings.view"", ""payments.view"", ""customers.view"", ""customers.export"", ""standing.view""]', UTC_TIMESTAMP(6), UTC_TIMESTAMP(6)
                FROM users WHERE role = 'venue_owner';");

            // 3. Every linked clerk moves onto the role matching their old level, all venues.
            //    Unlinked clerks stay role-less: they had no access before and still have none.
            migrationBuilder.Sql(@"
                UPDATE users s
                JOIN users o ON o.id = s.managed_by_owner_id AND o.role = 'venue_owner'
                SET s.staff_role_id = CASE WHEN s.permissions = 'write'
                                           THEN CONCAT('srw_', LEFT(SHA2(o.id, 256), 24))
                                           ELSE CONCAT('srr_', LEFT(SHA2(o.id, 256), 24)) END,
                    s.staff_all_venues = 1
                WHERE s.role = 'venue_staff';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_users_staff_roles_staff_role_id",
                table: "users");

            migrationBuilder.DropTable(
                name: "companies");

            migrationBuilder.DropTable(
                name: "staff_roles");

            migrationBuilder.DropIndex(
                name: "IX_users_managed_by_owner_id",
                table: "users");

            migrationBuilder.DropIndex(
                name: "IX_users_staff_role_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "staff_all_venues",
                table: "users");

            migrationBuilder.DropColumn(
                name: "staff_role_id",
                table: "users");

            migrationBuilder.DropColumn(
                name: "staff_venue_ids",
                table: "users");

            migrationBuilder.DropColumn(
                name: "default_max_staff",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "default_max_venues",
                table: "platform_settings");
        }
    }
}
