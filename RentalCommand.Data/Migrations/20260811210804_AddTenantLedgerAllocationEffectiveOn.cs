using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantLedgerAllocationEffectiveOn : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateOnly>(
                name: "EffectiveOn",
                table: "TenantLedgerAllocations",
                type: "date",
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE "TenantLedgerAllocations" AS allocation
                SET "EffectiveOn" = credit."EffectiveOn"
                FROM "TenantLedgerEntries" AS credit
                WHERE credit."PortfolioId" = allocation."PortfolioId"
                  AND credit."TenantAccountId" = allocation."TenantAccountId"
                  AND credit."Id" = allocation."CreditEntryId"
                  AND credit."Direction" = 'Credit'
                  AND allocation."EffectiveOn" IS NULL;
                """);

            migrationBuilder.Sql(
                "CREATE OR REPLACE VIEW \"vw_tenant_charge_balances\" WITH (security_invoker = true) AS\n"
                + TenantChargeBalanceViewSql.Definition);
            migrationBuilder.Sql(
                "CREATE OR REPLACE VIEW \"vw_tenant_account_balances\" WITH (security_invoker = true) AS\n"
                + TenantAccountBalanceViewSql.Definition);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "CREATE OR REPLACE VIEW \"vw_tenant_charge_balances\" WITH (security_invoker = true) AS\n"
                + PreviousTenantChargeBalanceDefinition);
            migrationBuilder.Sql(
                "CREATE OR REPLACE VIEW \"vw_tenant_account_balances\" WITH (security_invoker = true) AS\n"
                + PreviousTenantAccountBalanceDefinition);

            migrationBuilder.DropColumn(
                name: "EffectiveOn",
                table: "TenantLedgerAllocations");
        }

        private static string PreviousTenantChargeBalanceDefinition =>
            TenantChargeBalanceViewSql.Definition.Replace(
                "COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\")",
                "credit.\"EffectiveOn\"");

        private static string PreviousTenantAccountBalanceDefinition =>
            TenantAccountBalanceViewSql.Definition.Replace(
                "COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\")",
                "credit.\"EffectiveOn\"");
    }
}
