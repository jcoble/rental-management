using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728235500_GrantRuntimeTenantMoneyRowLockUpdates")]
public sealed class GrantRuntimeTenantMoneyRowLockUpdates : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT UPDATE ON TABLE "TenantLedgerEntries" TO rentalcommand_api;
            GRANT UPDATE ON TABLE "TenantLedgerAllocations" TO rentalcommand_api;
            GRANT UPDATE ON TABLE "TenantLedgerEntries" TO rentalcommand_engine;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE UPDATE ON TABLE "TenantLedgerEntries" FROM rentalcommand_engine;
            REVOKE UPDATE ON TABLE "TenantLedgerAllocations" FROM rentalcommand_api;
            REVOKE UPDATE ON TABLE "TenantLedgerEntries" FROM rentalcommand_api;
            """);
    }
}
