using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data.Authorization;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260811100000_AddOwnerDistributionAuthorizationFunction")]
public sealed class AddOwnerDistributionAuthorizationFunction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(OwnerDistributionAuthorizationFunctionSql.Create);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(OwnerDistributionAuthorizationFunctionSql.Drop);
}
