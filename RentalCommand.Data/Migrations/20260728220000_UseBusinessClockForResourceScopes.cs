using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728220000_UseBusinessClockForResourceScopes")]
public partial class UseBusinessClockForResourceScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260728);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260727);
    }
}
