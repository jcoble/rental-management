using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RentalCommandDbContext))]
    [Migration("20260727153000_AddLoanDebtServiceAutomationStartDate")]
    public partial class AddLoanDebtServiceAutomationStartDate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTime>(
                name: "DebtServiceAutomationStartDate",
                table: "Loans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "Loans"
                SET "DebtServiceAutomationStartDate" = "CreatedAt"
                WHERE "DebtServiceAutomationStartDate" IS NULL;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DebtServiceAutomationStartDate",
                table: "Loans");
        }
    }
}
