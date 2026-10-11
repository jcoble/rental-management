using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260724130000_GrantEngineAtomicAuditPolicyHelpers")]
public partial class GrantEngineAtomicAuditPolicyHelpers : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            GRANT EXECUTE ON FUNCTION rc_pre_auth_email_audit_allows(
              integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
              TO rentalcommand_engine;
            GRANT EXECUTE ON FUNCTION rc_pre_auth_account_security_audit_allows(
              integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
              TO rentalcommand_engine;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            REVOKE EXECUTE ON FUNCTION rc_pre_auth_account_security_audit_allows(
              integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
              FROM rentalcommand_engine;
            REVOKE EXECUTE ON FUNCTION rc_pre_auth_email_audit_allows(
              integer, uuid, text, text, bigint, integer, text, integer, integer, text, text, jsonb)
              FROM rentalcommand_engine;
            """);
    }
}
