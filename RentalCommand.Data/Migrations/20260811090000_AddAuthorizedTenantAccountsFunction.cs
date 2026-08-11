using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data.Authorization;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260811090000_AddAuthorizedTenantAccountsFunction")]
public sealed class AddAuthorizedTenantAccountsFunction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(AuthorizedTenantAccountsFunctionSql.Create);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(AuthorizedTenantAccountsFunctionSql.Drop);
}
