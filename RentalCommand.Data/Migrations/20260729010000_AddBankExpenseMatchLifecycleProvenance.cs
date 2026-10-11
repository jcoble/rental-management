using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729010000_AddBankExpenseMatchLifecycleProvenance")]
public partial class AddBankExpenseMatchLifecycleProvenance : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>(
            name: "ExpenseMatchAppliedAt",
            table: "BankTransactions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ExpenseMatchPreviousPaidAt",
            table: "BankTransactions",
            type: "timestamp with time zone",
            nullable: true);

        migrationBuilder.AddColumn<int>(
            name: "ExpenseMatchPreviousStatus",
            table: "BankTransactions",
            type: "integer",
            nullable: true);

        migrationBuilder.AddColumn<DateTime>(
            name: "ExpenseMatchPreviousUpdatedAt",
            table: "BankTransactions",
            type: "timestamp with time zone",
            nullable: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropColumn(
            name: "ExpenseMatchAppliedAt",
            table: "BankTransactions");

        migrationBuilder.DropColumn(
            name: "ExpenseMatchPreviousPaidAt",
            table: "BankTransactions");

        migrationBuilder.DropColumn(
            name: "ExpenseMatchPreviousStatus",
            table: "BankTransactions");

        migrationBuilder.DropColumn(
            name: "ExpenseMatchPreviousUpdatedAt",
            table: "BankTransactions");
    }
}
