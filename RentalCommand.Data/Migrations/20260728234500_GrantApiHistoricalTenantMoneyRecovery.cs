using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728234500_GrantApiHistoricalTenantMoneyRecovery")]
public sealed class GrantApiHistoricalTenantMoneyRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT SELECT, UPDATE ON TABLE "TenantAccounts" TO rentalcommand_api;
            GRANT SELECT, INSERT ON TABLE "TenantLedgerEntries" TO rentalcommand_api;
            GRANT SELECT, INSERT ON TABLE "TenantLedgerAllocations" TO rentalcommand_api;
            GRANT USAGE, SELECT ON SEQUENCE "TenantLedgerEntries_Id_seq" TO rentalcommand_api;
            GRANT USAGE, SELECT ON SEQUENCE "TenantLedgerAllocations_Id_seq" TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE USAGE, SELECT ON SEQUENCE "TenantLedgerAllocations_Id_seq" FROM rentalcommand_api;
            REVOKE USAGE, SELECT ON SEQUENCE "TenantLedgerEntries_Id_seq" FROM rentalcommand_api;
            REVOKE SELECT, INSERT ON TABLE "TenantLedgerAllocations" FROM rentalcommand_api;
            REVOKE SELECT, INSERT ON TABLE "TenantLedgerEntries" FROM rentalcommand_api;
            REVOKE SELECT, UPDATE ON TABLE "TenantAccounts" FROM rentalcommand_api;
            """);
    }
}
