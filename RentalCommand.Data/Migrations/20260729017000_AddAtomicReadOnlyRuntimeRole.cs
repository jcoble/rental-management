using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729017000_AddAtomicReadOnlyRuntimeRole")]
public sealed class AddAtomicReadOnlyRuntimeRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.AtomicReadOnlyRoleSqlV20260729);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Runtime roles and membership are cluster-wide. Reverting one database must not remove
        // the restricted membership required by another database using the same runtime logins.
    }
}
