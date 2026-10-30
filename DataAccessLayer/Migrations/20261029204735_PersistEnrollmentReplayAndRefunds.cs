using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace DataAccessLayer.Migrations
{
    /// <inheritdoc />
    public partial class PersistEnrollmentReplayAndRefunds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_enrollments_user_id",
                table: "enrollments");

            migrationBuilder.AddColumn<string>(
                name: "idempotency_body_hash",
                table: "enrollments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key_hash",
                table: "enrollments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_response_json",
                table: "enrollments",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "refunded_visits",
                table: "enrollments",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_user_id_idempotency_key_hash",
                table: "enrollments",
                columns: new[] { "user_id", "idempotency_key_hash" },
                unique: true,
                filter: "idempotency_key_hash IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_enrollments_user_id_idempotency_key_hash",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "idempotency_body_hash",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "idempotency_key_hash",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "idempotency_response_json",
                table: "enrollments");

            migrationBuilder.DropColumn(
                name: "refunded_visits",
                table: "enrollments");

            migrationBuilder.CreateIndex(
                name: "IX_enrollments_user_id",
                table: "enrollments",
                column: "user_id");
        }
    }
}
