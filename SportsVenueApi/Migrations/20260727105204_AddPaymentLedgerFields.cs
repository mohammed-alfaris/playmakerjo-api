using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SportsVenueApi.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentLedgerFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "customer_id",
                table: "payments",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            // "full" rather than the generated "" because the only rows this backfills are
            // seed rows, each of which records a whole booking amount. No production row
            // exists to mislabel: nothing in the application had ever written to payments
            // before this migration.
            migrationBuilder.AddColumn<string>(
                name: "kind",
                table: "payments",
                type: "varchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "full")
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "note",
                table: "payments",
                type: "varchar(255)",
                maxLength: 255,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "recorded_by_user_id",
                table: "payments",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.AddColumn<string>(
                name: "venue_id",
                table: "payments",
                type: "varchar(32)",
                maxLength: 32,
                nullable: true)
                .Annotation("MySql:CharSet", "utf8mb4");

            migrationBuilder.CreateIndex(
                name: "IX_payments_customer_id",
                table: "payments",
                column: "customer_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_recorded_by_user_id",
                table: "payments",
                column: "recorded_by_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_payments_venue_id_date",
                table: "payments",
                columns: new[] { "venue_id", "date" });

            migrationBuilder.AddForeignKey(
                name: "FK_payments_customers_customer_id",
                table: "payments",
                column: "customer_id",
                principalTable: "customers",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_payments_users_recorded_by_user_id",
                table: "payments",
                column: "recorded_by_user_id",
                principalTable: "users",
                principalColumn: "id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_payments_customers_customer_id",
                table: "payments");

            migrationBuilder.DropForeignKey(
                name: "FK_payments_users_recorded_by_user_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_customer_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_recorded_by_user_id",
                table: "payments");

            migrationBuilder.DropIndex(
                name: "IX_payments_venue_id_date",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "customer_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "kind",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "note",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "recorded_by_user_id",
                table: "payments");

            migrationBuilder.DropColumn(
                name: "venue_id",
                table: "payments");
        }
    }
}
