using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>Installs the atomic retry scheduling function used by the Engine reconciler.</summary>
public partial class InteractivePaymentReconciliation : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            TenantAccountPostgreSqlContract.InteractivePaymentReconciliationScheduleCreateStatement);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(
            TenantAccountPostgreSqlContract.InteractivePaymentReconciliationScheduleDropStatement);
}
