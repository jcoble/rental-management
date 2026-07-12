using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711233000_AddScheduledFinanceOccurrenceFence")]
public partial class AddScheduledFinanceOccurrenceFence : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "RecurringExpenseId",
            table: "Expenses",
            type: "integer",
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "RecurringExpenseOccurrenceDate",
            table: "Expenses",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.CreateIndex(
            name: "UX_Expenses_RecurringExpense_Occurrence",
            table: "Expenses",
            columns: new[] { "RecurringExpenseId", "RecurringExpenseOccurrenceDate" },
            unique: true,
            filter: "\"RecurringExpenseId\" IS NOT NULL AND \"RecurringExpenseOccurrenceDate\" IS NOT NULL");
        migrationBuilder.AddForeignKey(
            name: "FK_Expenses_RecurringExpenses_RecurringExpenseId",
            table: "Expenses",
            column: "RecurringExpenseId",
            principalTable: "RecurringExpenses",
            principalColumn: "Id",
            onDelete: ReferentialAction.Restrict);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropForeignKey(
            name: "FK_Expenses_RecurringExpenses_RecurringExpenseId",
            table: "Expenses");
        migrationBuilder.DropIndex(
            name: "UX_Expenses_RecurringExpense_Occurrence",
            table: "Expenses");
        migrationBuilder.DropColumn(name: "RecurringExpenseId", table: "Expenses");
        migrationBuilder.DropColumn(name: "RecurringExpenseOccurrenceDate", table: "Expenses");
    }
}
