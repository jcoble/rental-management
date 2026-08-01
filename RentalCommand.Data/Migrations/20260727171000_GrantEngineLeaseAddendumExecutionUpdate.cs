using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260727171000_GrantEngineLeaseAddendumExecutionUpdate")]
public partial class GrantEngineLeaseAddendumExecutionUpdate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT UPDATE ON TABLE "LeaseAddenda" TO rentalcommand_engine;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE UPDATE ON TABLE "LeaseAddenda" FROM rentalcommand_engine;
            """);
    }
}
