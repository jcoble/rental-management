using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729016000_RemoveRetiredVendorDispatchChronologyFunction")]
public sealed class RemoveRetiredVendorDispatchChronologyFunction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            DROP FUNCTION IF EXISTS public.rc_recover_vendor_dispatch_audit_chronology(
              integer,
              text,
              integer,
              integer,
              integer,
              timestamp with time zone,
              timestamp with time zone,
              uuid,
              integer);

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
