using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711260000_AddNativeEsignExecutionClaims")]
public sealed class AddNativeEsignExecutionClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "ExecutionAttemptCount",
            table: "SignatureRequests",
            type: "integer",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.AddColumn<DateTime>(
            name: "ExecutionClaimExpiresAtUtc",
            table: "SignatureRequests",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ExecutionClaimOwner",
            table: "SignatureRequests",
            type: "character varying(200)",
            maxLength: 200,
            nullable: true);
        migrationBuilder.AddColumn<Guid>(
            name: "ExecutionClaimToken",
            table: "SignatureRequests",
            type: "uuid",
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "ExecutionLastAttemptAtUtc",
            table: "SignatureRequests",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "ExecutionLastError",
            table: "SignatureRequests",
            type: "character varying(2000)",
            maxLength: 2000,
            nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_SignatureRequests_Status_ExecutionClaimExpiresAtUtc_CreatedAtUtc_Id",
            table: "SignatureRequests",
            columns: new[] { "Status", "ExecutionClaimExpiresAtUtc", "CreatedAtUtc", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_SignatureRequests_ExecutionClaimExpiresAtUtc_Id",
            table: "SignatureRequests",
            columns: new[] { "ExecutionClaimExpiresAtUtc", "Id" },
            filter: "\"ExecutionClaimToken\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(
            name: "IX_SignatureRequests_Status_ExecutionClaimExpiresAtUtc_CreatedAtUtc_Id",
            table: "SignatureRequests");
        migrationBuilder.DropIndex(
            name: "IX_SignatureRequests_ExecutionClaimExpiresAtUtc_Id",
            table: "SignatureRequests");
        migrationBuilder.DropColumn(name: "ExecutionAttemptCount", table: "SignatureRequests");
        migrationBuilder.DropColumn(name: "ExecutionClaimExpiresAtUtc", table: "SignatureRequests");
        migrationBuilder.DropColumn(name: "ExecutionClaimOwner", table: "SignatureRequests");
        migrationBuilder.DropColumn(name: "ExecutionClaimToken", table: "SignatureRequests");
        migrationBuilder.DropColumn(name: "ExecutionLastAttemptAtUtc", table: "SignatureRequests");
        migrationBuilder.DropColumn(name: "ExecutionLastError", table: "SignatureRequests");
    }
}
