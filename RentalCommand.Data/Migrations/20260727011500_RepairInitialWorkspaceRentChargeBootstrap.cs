using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260727011500_RepairInitialWorkspaceRentChargeBootstrap")]
public partial class RepairInitialWorkspaceRentChargeBootstrap : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // Keep the deployed SECURITY DEFINER authority bundle aligned with its canonical source.
        // The prior rent-charge migration added an always-on constraint but did not replace the
        // already-installed bootstrap function, whose stale FALSE default then rolled back every
        // first-time password/Google account bootstrap.
        migrationBuilder.Sql(FoundationBaselinePostgreSql.RlsAuthorityFunctionSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "The initial workspace bootstrap cannot be restored to create invalid automation settings.");
    }
}
