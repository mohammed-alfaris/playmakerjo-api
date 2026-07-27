using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddCustomers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "customer_id",
                table: "permanent_bookings",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "customer_id",
                table: "bookings",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "customers",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    owner_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    phone = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    name = table.Column<string>(type: "varchar(120)", maxLength: 120, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    note = table.Column<string>(type: "varchar(280)", maxLength: 280, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    archived_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    created_by_user_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime(6)", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_customers", x => x.id);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_permanent_bookings_customer_id",
                table: "permanent_bookings",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_bookings_customer_id_date",
                table: "bookings",
                columns: new[] { "customer_id", "date" });

            migrationBuilder.CreateIndex(
                name: "IX_customers_owner_id_phone",
                table: "customers",
                columns: new[] { "owner_id", "phone" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_customers_owner_id_status",
                table: "customers",
                columns: new[] { "owner_id", "status" });

            migrationBuilder.AddForeignKey(
                name: "FK_bookings_customers_customer_id",
                table: "bookings",
                column: "customer_id",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_bookings_customers_customer_id",
                table: "bookings");

            migrationBuilder.DropTable(
                name: "customers");

            migrationBuilder.DropIndex(
                name: "IX_permanent_bookings_customer_id",
                table: "permanent_bookings");

            migrationBuilder.DropIndex(
                name: "IX_bookings_customer_id_date",
                table: "bookings");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "permanent_bookings");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "bookings");
        }
    }
}
