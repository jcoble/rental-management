using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729014000_GrantApiVendorDispatchChronologyRecovery")]
public sealed class GrantApiVendorDispatchChronologyRecovery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT SELECT ON TABLE public."AtomicAuditLogs"
              TO rentalcommand_api;
            GRANT UPDATE ("Timestamp") ON TABLE public."AtomicAuditLogs"
              TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE UPDATE ("Timestamp") ON TABLE public."AtomicAuditLogs"
              FROM rentalcommand_api;
            """);
    }
}
