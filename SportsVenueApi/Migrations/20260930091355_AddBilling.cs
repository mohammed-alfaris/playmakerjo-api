using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddBilling : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "payment_terms_days",
                table: "platform_settings",
                type: "int",
                nullable: false,
                defaultValue: 14);

            migrationBuilder.AddColumn<double>(
                name: "price_extra_venue",
                table: "platform_settings",
                type: "double",
                nullable: false,
                defaultValue: 15.0);

            migrationBuilder.AddColumn<double>(
                name: "price_first_venue",
                table: "platform_settings",
                type: "double",
                nullable: false,
                defaultValue: 30.0);

            migrationBuilder.AddColumn<double>(
                name: "setup_fee",
                table: "platform_settings",
                type: "double",
                nullable: false,
                defaultValue: 100.0);

            migrationBuilder.AddColumn<int>(
                name: "trial_days",
                table: "platform_settings",
                type: "int",
                nullable: false,
                defaultValue: 30);

            migrationBuilder.AddColumn<string>(
                name: "billing_cycle",
                table: "companies",
                type: "varchar(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "monthly")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<double>(
                name: "price_extra_venue",
                table: "companies",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<double>(
                name: "price_first_venue",
                table: "companies",
                type: "double",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "setup_fee_waived",
                table: "companies",
                type: "tinyint(1)",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTime>(
                name: "suspended_at",
                table: "companies",
                type: "datetime(6)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "suspended_reason",
                table: "companies",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<DateTime>(
                name: "trial_ends_on",
                table: "companies",
                type: "datetime(6)",
                nullable: true);

            // Hand-written: the defaults above were edited from EF's zeros (a 0 JOD price, an empty
            // cycle) to the real ones, and companies that already exist start a 30-day trial
            // from the day this runs — so the first Generate bills nobody by surprise. The admin
            // can end or extend it per company.
            migrationBuilder.Sql("UPDATE companies SET trial_ends_on = DATE_ADD(DATE(CONVERT_TZ(UTC_TIMESTAMP(), '+00:00', '+03:00')), INTERVAL 29 DAY) WHERE trial_ends_on IS NULL;");

            migrationBuilder.CreateTable(
                name: "invoices",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    number = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    owner_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    period = table.Column<string>(type: "varchar(7)", maxLength: 7, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    status = table.Column<string>(type: "varchar(10)", maxLength: 10, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    total = table.Column<double>(type: "double", nullable: false),
                    issued_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    due_on = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    paid_at = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    paid_method = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    paid_reference = table.Column<string>(type: "varchar(100)", maxLength: 100, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    void_reason = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_by_user_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    created_at = table.Column<DateTime>(type: "datetime(6)", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoices", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoices_users_owner_id",
                        column: x => x.owner_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateTable(
                name: "invoice_lines",
                columns: table => new
                {
                    id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    invoice_id = table.Column<string>(type: "varchar(32)", maxLength: 32, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    kind = table.Column<string>(type: "varchar(20)", maxLength: 20, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: false)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    description_ar = table.Column<string>(type: "varchar(255)", maxLength: 255, nullable: true)
                        .Annotation("MySql:CharSet", "utf8mb4"),
                    quantity = table.Column<double>(type: "double", nullable: false),
                    unit_price = table.Column<double>(type: "double", nullable: false),
                    amount = table.Column<double>(type: "double", nullable: false),
                    covers_from = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    covers_to = table.Column<DateTime>(type: "datetime(6)", nullable: true),
                    sort = table.Column<int>(type: "int", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invoice_lines", x => x.id);
                    table.ForeignKey(
                        name: "FK_invoice_lines_invoices_invoice_id",
                        column: x => x.invoice_id,
                        principalTable: "invoices",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                })
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_invoice_lines_invoice_id",
                table: "invoice_lines",
                column: "invoice_id");

            migrationBuilder.CreateIndex(
                name: "IX_invoices_number",
                table: "invoices",
                column: "number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_invoices_owner_id_period",
                table: "invoices",
                columns: new[] { "owner_id", "period" });

            migrationBuilder.CreateIndex(
                name: "IX_invoices_status_due_on",
                table: "invoices",
                columns: new[] { "status", "due_on" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invoice_lines");

            migrationBuilder.DropTable(
                name: "invoices");

            migrationBuilder.DropColumn(
                name: "payment_terms_days",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "price_extra_venue",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "price_first_venue",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "setup_fee",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "trial_days",
                table: "platform_settings");

            migrationBuilder.DropColumn(
                name: "billing_cycle",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "price_extra_venue",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "price_first_venue",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "setup_fee_waived",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "suspended_at",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "suspended_reason",
                table: "companies");

            migrationBuilder.DropColumn(
                name: "trial_ends_on",
                table: "companies");
        }
    }
}
