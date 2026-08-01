using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class EnforceUniqueBankReconciliationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM "BankTransactions"
                        WHERE "MatchedTenantLedgerEntryId" IS NOT NULL
                        GROUP BY "PortfolioId", "MatchedTenantLedgerEntryId"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Duplicate bank reconciliation tenant ledger targets exist; resolve before applying EnforceUniqueBankReconciliationTargets.';
                    END IF;

                    IF EXISTS (
                        SELECT 1
                        FROM "BankTransactions"
                        WHERE "MatchedExpenseId" IS NOT NULL
                        GROUP BY "PortfolioId", "MatchedExpenseId"
                        HAVING COUNT(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Duplicate bank reconciliation expense targets exist; resolve before applying EnforceUniqueBankReconciliationTargets.';
                    END IF;
                END $$;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedExpenseId",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "MatchedExpenseId" },
                unique: true,
                filter: "\"MatchedExpenseId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedTenantLedgerEntryId",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "MatchedTenantLedgerEntryId" },
                unique: true,
                filter: "\"MatchedTenantLedgerEntryId\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedExpenseId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedTenantLedgerEntryId",
                table: "BankTransactions");
        }
    }
}
