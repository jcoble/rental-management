using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260719154500_OptimizeRlsRequestScope")]
public partial class OptimizeRlsRequestScope : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260719);
        migrationBuilder.Sql(FoundationBaselinePostgreSql.ResourcePoliciesSqlV20260719);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "20260719154500_OptimizeRlsRequestScope is intentionally irreversible: restoring the prior " +
            "per-row authority policies would reintroduce the reviewed production latency defect. Restore " +
            "an earlier database backup if a full rollback is required.");
    }
}
