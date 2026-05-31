using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class Phase4Automation : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "PeriodKey",
                table: "Payments",
                type: "character varying(7)",
                maxLength: 7,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "ExpiryReminderSentAt",
                table: "Leases",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Payments_LeaseId_PaymentType_PeriodKey",
                table: "Payments",
                columns: new[] { "LeaseId", "PaymentType", "PeriodKey" },
                unique: true,
                filter: "\"PeriodKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Payments_LeaseId_PaymentType_PeriodKey",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "PeriodKey",
                table: "Payments");

            migrationBuilder.DropColumn(
                name: "ExpiryReminderSentAt",
                table: "Leases");
        }
    }
}
