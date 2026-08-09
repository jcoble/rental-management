using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260809011000_RestoreAuthSessionRevokeAudit")]
public partial class RestoreAuthSessionRevokeAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        // 20260802010000 reapplies the authority bundle from before the revoke-audit admission
        // was added. Restore the narrow admission after that refresh so logout can atomically
        // update the session and append its audit row in the same transaction.
        migrationBuilder.Sql(FoundationBaselinePostgreSql.RlsAuthorityFunctionSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260801);
    }
}
