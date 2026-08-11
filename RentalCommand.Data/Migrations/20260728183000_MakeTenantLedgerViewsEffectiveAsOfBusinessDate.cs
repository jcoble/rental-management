using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728183000_MakeTenantLedgerViewsEffectiveAsOfBusinessDate")]
public partial class MakeTenantLedgerViewsEffectiveAsOfBusinessDate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "CREATE OR REPLACE VIEW \"vw_tenant_charge_balances\" WITH (security_invoker = true) AS\n"
            + TenantChargeBalanceViewSql.Definition.Replace(
                "COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\")",
                "credit.\"EffectiveOn\""));
        migrationBuilder.Sql(
            "CREATE OR REPLACE VIEW \"vw_tenant_account_balances\" WITH (security_invoker = true) AS\n"
            + TenantAccountBalanceViewSql.Definition.Replace(
                "COALESCE(allocation.\"EffectiveOn\", credit.\"EffectiveOn\")",
                "credit.\"EffectiveOn\""));
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "The prior tenant ledger views included future-effective money in current balances; rollback is unsafe.");
    }
}
