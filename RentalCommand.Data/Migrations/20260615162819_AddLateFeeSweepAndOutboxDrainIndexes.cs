using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLateFeeSweepAndOutboxDrainIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_Payments_LateFeeSweep",
                table: "Payments",
                columns: new[] { "PaymentType", "Status", "DueDate" },
                filter: "\"PeriodKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_OutboxMessages_Unsent_CreatedAt",
                table: "OutboxMessages",
                columns: new[] { "CreatedAt", "RetryCount" },
                filter: "\"SentAt\" IS NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_LateFeeSweep",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_OutboxMessages_Unsent_CreatedAt",
                table: "OutboxMessages");
        }
    }
}
