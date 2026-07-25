using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260724150000_AddEffectiveCapabilityScopeAuthority")]
public partial class AddEffectiveCapabilityScopeAuthority : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.EffectiveCapabilityScopeAuthoritySqlV20260725);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "20260724150000_AddEffectiveCapabilityScopeAuthority is intentionally irreversible: " +
            "restoring the expanded per-request authorization graph would reintroduce the reviewed " +
            "cross-page latency defect. Restore an earlier database backup if a full rollback is required.");
    }
}
