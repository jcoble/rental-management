using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>
/// Enables Identity lockout for accounts created before lockout became a platform invariant.
/// </summary>
[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260809010000_EnableIdentityLockout")]
public partial class EnableIdentityLockout : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            "UPDATE \"AspNetUsers\" SET \"LockoutEnabled\" = TRUE WHERE NOT \"LockoutEnabled\";");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Lockout is intentionally fail-closed. A rollback must not disable protection for every
        // account, including accounts created after this migration was applied.
    }
}
