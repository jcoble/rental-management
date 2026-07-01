using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddDataAccessIndexes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_StoredFiles_FilePath",
                table: "StoredFiles",
                column: "FilePath");

            migrationBuilder.CreateIndex(
                name: "IX_RecurringMaintenanceTasks_Active_NextDueDate",
                table: "RecurringMaintenanceTasks",
                columns: new[] { "IsActive", "NextDueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Portfolio_Lease_LedgerDates",
                table: "Payments",
                columns: new[] { "PortfolioId", "LeaseId", "PaidDate", "DueDate", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Portfolio_PaidDate",
                table: "Payments",
                columns: new[] { "PortfolioId", "PaidDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_Portfolio_Status_DueDate",
                table: "Payments",
                columns: new[] { "PortfolioId", "Status", "DueDate" });

            migrationBuilder.CreateIndex(
                name: "IX_Leases_ExpirySweep",
                table: "Leases",
                columns: new[] { "Status", "EndDate" },
                filter: "\"ExpiryReminderSentAt\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Portfolio_IncurredAt",
                table: "Expenses",
                columns: new[] { "PortfolioId", "IncurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Portfolio_PaidAt",
                table: "Expenses",
                columns: new[] { "PortfolioId", "PaidAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_Portfolio_Property_Category_IncurredAt",
                table: "Expenses",
                columns: new[] { "PortfolioId", "PropertyId", "Category", "IncurredAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AccountingConnections_Status_TokenExpiresAt",
                table: "AccountingConnections",
                columns: new[] { "Status", "TokenExpiresAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_StoredFiles_FilePath",
                table: "StoredFiles");

            migrationBuilder.DropIndex(
                name: "IX_RecurringMaintenanceTasks_Active_NextDueDate",
                table: "RecurringMaintenanceTasks");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Portfolio_Lease_LedgerDates",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Portfolio_PaidDate",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Payments_Portfolio_Status_DueDate",
                table: "Payments");

            migrationBuilder.DropIndex(
                name: "IX_Leases_ExpirySweep",
                table: "Leases");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_Portfolio_IncurredAt",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_Portfolio_PaidAt",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_Expenses_Portfolio_Property_Category_IncurredAt",
                table: "Expenses");

            migrationBuilder.DropIndex(
                name: "IX_AccountingConnections_Status_TokenExpiresAt",
                table: "AccountingConnections");
        }
    }
}
