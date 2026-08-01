using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

using RentalCommand.Data.Accounting;

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenAccountingPostingInvariants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_PortfolioId_SourceType_SourceId_PostingRuleV~",
                table: "JournalEntries");

            migrationBuilder.AlterColumn<decimal>(
                name: "DebitAmount",
                table: "JournalLines",
                type: "numeric(22,6)",
                precision: 22,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AlterColumn<decimal>(
                name: "CreditAmount",
                table: "JournalLines",
                type: "numeric(22,6)",
                precision: 22,
                scale: 6,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(18,2)",
                oldPrecision: 18,
                oldScale: 2);

            migrationBuilder.AddCheckConstraint(
                name: "CK_JournalLines_CreditScale",
                table: "JournalLines",
                sql: "\"CreditAmount\" = round(\"CreditAmount\", 2)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_JournalLines_DebitScale",
                table: "JournalLines",
                sql: "\"DebitAmount\" = round(\"DebitAmount\", 2)");

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_PortfolioId_SourceType_SourceBusinessKey_Pos~",
                table: "JournalEntries",
                columns: new[] { "PortfolioId", "SourceType", "SourceBusinessKey", "PostingRuleVersion" },
                unique: true);

            migrationBuilder.Sql(AccountingLedgerPostgreSql.HardenSql);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(AccountingLedgerPostgreSql.HardenDropSql);

            migrationBuilder.DropCheckConstraint(
                name: "CK_JournalLines_CreditScale",
                table: "JournalLines");

            migrationBuilder.DropCheckConstraint(
                name: "CK_JournalLines_DebitScale",
                table: "JournalLines");

            migrationBuilder.DropIndex(
                name: "IX_JournalEntries_PortfolioId_SourceType_SourceBusinessKey_Pos~",
                table: "JournalEntries");

            migrationBuilder.AlterColumn<decimal>(
                name: "DebitAmount",
                table: "JournalLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(22,6)",
                oldPrecision: 22,
                oldScale: 6);

            migrationBuilder.AlterColumn<decimal>(
                name: "CreditAmount",
                table: "JournalLines",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: false,
                oldClrType: typeof(decimal),
                oldType: "numeric(22,6)",
                oldPrecision: 22,
                oldScale: 6);

            migrationBuilder.CreateIndex(
                name: "IX_JournalEntries_PortfolioId_SourceType_SourceId_PostingRuleV~",
                table: "JournalEntries",
                columns: new[] { "PortfolioId", "SourceType", "SourceId", "PostingRuleVersion" },
                unique: true);
        }
    }
}
