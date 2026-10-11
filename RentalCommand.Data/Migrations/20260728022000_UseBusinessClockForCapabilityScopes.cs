using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728022000_UseBusinessClockForCapabilityScopes")]
public partial class UseBusinessClockForCapabilityScopes : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260727);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260725);
    }
}
