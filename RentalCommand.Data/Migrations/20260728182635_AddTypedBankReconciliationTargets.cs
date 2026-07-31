using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTypedBankReconciliationTargets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MatchedBankTransactionId",
                table: "BankTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchedLoanPaymentId",
                table: "BankTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MatchedOwnerDistributionId",
                table: "BankTransactions",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedBankTransactionId",
                table: "BankTransactions",
                column: "MatchedBankTransactionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedLoanPaymentId",
                table: "BankTransactions",
                column: "MatchedLoanPaymentId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_MatchedOwnerDistributionId",
                table: "BankTransactions",
                column: "MatchedOwnerDistributionId");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedBankTransactionId",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "MatchedBankTransactionId" },
                unique: true,
                filter: "\"MatchedBankTransactionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedLoanPaymentId",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "MatchedLoanPaymentId" },
                unique: true,
                filter: "\"MatchedLoanPaymentId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedOwnerDistributionId",
                table: "BankTransactions",
                columns: new[] { "PortfolioId", "MatchedOwnerDistributionId" },
                unique: true,
                filter: "\"MatchedOwnerDistributionId\" IS NOT NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_BankTransactions_MatchedBankTransactionId",
                table: "BankTransactions",
                column: "MatchedBankTransactionId",
                principalTable: "BankTransactions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_LoanPayments_MatchedLoanPaymentId",
                table: "BankTransactions",
                column: "MatchedLoanPaymentId",
                principalTable: "LoanPayments",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_BankTransactions_OwnerDistributions_MatchedOwnerDistributio~",
                table: "BankTransactions",
                column: "MatchedOwnerDistributionId",
                principalTable: "OwnerDistributions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_BankTransactions_MatchedBankTransactionId",
                table: "BankTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_LoanPayments_MatchedLoanPaymentId",
                table: "BankTransactions");

            migrationBuilder.DropForeignKey(
                name: "FK_BankTransactions_OwnerDistributions_MatchedOwnerDistributio~",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_MatchedBankTransactionId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_MatchedLoanPaymentId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_MatchedOwnerDistributionId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedBankTransactionId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedLoanPaymentId",
                table: "BankTransactions");

            migrationBuilder.DropIndex(
                name: "IX_BankTransactions_PortfolioId_MatchedOwnerDistributionId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "MatchedBankTransactionId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "MatchedLoanPaymentId",
                table: "BankTransactions");

            migrationBuilder.DropColumn(
                name: "MatchedOwnerDistributionId",
                table: "BankTransactions");
        }
    }
}
