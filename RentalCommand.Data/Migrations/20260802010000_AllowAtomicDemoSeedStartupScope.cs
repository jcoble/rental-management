using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260802010000_AllowAtomicDemoSeedStartupScope")]
public partial class AllowAtomicDemoSeedStartupScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260801);
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.AtomicDemoSeedResourcePoliciesSqlV20260801);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260728);
    }
}
