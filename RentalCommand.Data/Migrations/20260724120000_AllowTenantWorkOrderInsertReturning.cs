using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260724120000_AllowTenantWorkOrderInsertReturning")]
public partial class AllowTenantWorkOrderInsertReturning : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FoundationBaselinePostgreSql.WorkOrderReadPolicySqlV20260724);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FoundationBaselinePostgreSql.WorkOrderReadPolicySqlV20260719);
    }
}
