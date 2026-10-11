using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728164500_GrantApiRenderedNoticeChronologyRepair")]
public partial class GrantApiRenderedNoticeChronologyRepair : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT UPDATE ON TABLE "RenderedNotices" TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE UPDATE ON TABLE "RenderedNotices" FROM rentalcommand_api;
            """);
    }
}
