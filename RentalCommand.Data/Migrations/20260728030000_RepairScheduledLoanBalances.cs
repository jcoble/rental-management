using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    [DbContext(typeof(RentalCommandDbContext))]
    [Migration("20260728030000_RepairScheduledLoanBalances")]
    public partial class RepairScheduledLoanBalances : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                WITH payment_bounds AS (
                    SELECT
                        payment."LoanId",
                        (array_agg(
                            payment."BalanceAfter" + payment."PrincipalAmount"
                            ORDER BY payment."DueDate", payment."Id"))[1] AS opening_balance,
                        (array_agg(
                            payment."BalanceAfter"
                            ORDER BY payment."DueDate" DESC, payment."Id" DESC))[1] AS latest_scheduled_balance
                    FROM "LoanPayments" AS payment
                    GROUP BY payment."LoanId"
                    HAVING bool_and(payment."Status" = 0)
                )
                UPDATE "Loans" AS loan
                SET "CurrentBalance" = bounds.opening_balance,
                    "Status" = CASE
                        WHEN loan."Status" = 1 AND bounds.opening_balance > 0 THEN 0
                        ELSE loan."Status"
                    END,
                    "UpdatedAt" = clock_timestamp()
                FROM payment_bounds AS bounds
                WHERE loan."Id" = bounds."LoanId"
                  AND loan."CurrentBalance" = bounds.latest_scheduled_balance;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // This repairs live financial state. Reapplying the historical defect is intentionally
            // not a supported rollback.
        }
    }
}
