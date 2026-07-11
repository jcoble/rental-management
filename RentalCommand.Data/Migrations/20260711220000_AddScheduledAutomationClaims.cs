using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711220000_AddScheduledAutomationClaims")]
public partial class AddScheduledAutomationClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        AddClaimColumns(migrationBuilder, "Loans");
        AddClaimColumns(migrationBuilder, "RecurringExpenses");
        AddClaimColumns(migrationBuilder, "RecurringMaintenanceTasks");

        migrationBuilder.AddColumn<bool>(
            name: "EnableRecurringMaintenance",
            table: "NotificationSettings",
            type: "boolean",
            nullable: false,
            defaultValue: true);

        migrationBuilder.CreateIndex(
            name: "IX_Loans_DebtServiceClaim",
            table: "Loans",
            columns: new[] { "Status", "WorkerClaimExpiresAtUtc", "StartDate", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_RecurringExpenses_GenerationClaim",
            table: "RecurringExpenses",
            columns: new[] { "Active", "NextRunDate", "WorkerClaimExpiresAtUtc", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_RecurringMaintenanceTasks_GenerationClaim",
            table: "RecurringMaintenanceTasks",
            columns: new[] { "IsActive", "NextDueDate", "WorkerClaimExpiresAtUtc", "Id" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_Loans_DebtServiceClaim", table: "Loans");
        migrationBuilder.DropIndex(name: "IX_RecurringExpenses_GenerationClaim", table: "RecurringExpenses");
        migrationBuilder.DropIndex(name: "IX_RecurringMaintenanceTasks_GenerationClaim", table: "RecurringMaintenanceTasks");

        migrationBuilder.DropColumn(name: "EnableRecurringMaintenance", table: "NotificationSettings");

        DropClaimColumns(migrationBuilder, "Loans");
        DropClaimColumns(migrationBuilder, "RecurringExpenses");
        DropClaimColumns(migrationBuilder, "RecurringMaintenanceTasks");
    }

    private static void AddClaimColumns(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.AddColumn<int>(
            name: "WorkerClaimAttemptCount", table: table, type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTime>(
            name: "WorkerClaimExpiresAtUtc", table: table, type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "WorkerClaimOwner", table: table, type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "WorkerClaimToken", table: table, type: "uuid", nullable: true);
    }

    private static void DropClaimColumns(MigrationBuilder migrationBuilder, string table)
    {
        migrationBuilder.DropColumn(name: "WorkerClaimAttemptCount", table: table);
        migrationBuilder.DropColumn(name: "WorkerClaimExpiresAtUtc", table: table);
        migrationBuilder.DropColumn(name: "WorkerClaimOwner", table: table);
        migrationBuilder.DropColumn(name: "WorkerClaimToken", table: table);
    }
}
