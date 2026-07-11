using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Infrastructure;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <inheritdoc />
[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711200000_AddScanAndSimulationClaims")]
public partial class AddScanAndSimulationClaims : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_ScanDrafts_Status", table: "ScanDrafts");
        migrationBuilder.DropIndex(name: "IX_SimWorkerCommands_Status", table: "SimWorkerCommands");

        migrationBuilder.AddColumn<string>(name: "ProcessingClaimOwner", table: "ScanDrafts", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "ProcessingClaimToken", table: "ScanDrafts", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "ProcessingClaimExpiresAtUtc", table: "ScanDrafts", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<int>(name: "ProcessingAttemptCount", table: "ScanDrafts", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTime>(name: "ProcessingLastAttemptAtUtc", table: "ScanDrafts", type: "timestamp with time zone", nullable: true);

        migrationBuilder.AddColumn<string>(name: "ClaimOwner", table: "SimWorkerCommands", type: "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<Guid>(name: "ClaimToken", table: "SimWorkerCommands", type: "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>(name: "ClaimExpiresAtUtc", table: "SimWorkerCommands", type: "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<int>(name: "AttemptCount", table: "SimWorkerCommands", type: "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTime>(name: "LastAttemptAtUtc", table: "SimWorkerCommands", type: "timestamp with time zone", nullable: true);

        migrationBuilder.CreateIndex(
            name: "IX_ScanDrafts_Status_ProcessingClaimExpiresAtUtc_CreatedAt_Id",
            table: "ScanDrafts",
            columns: new[] { "Status", "ProcessingClaimExpiresAtUtc", "CreatedAt", "Id" });
        migrationBuilder.CreateIndex(
            name: "IX_SimWorkerCommands_Status_ClaimExpiresAtUtc_CreatedRealUtc_Id",
            table: "SimWorkerCommands",
            columns: new[] { "Status", "ClaimExpiresAtUtc", "CreatedRealUtc", "Id" });
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex(name: "IX_ScanDrafts_Status_ProcessingClaimExpiresAtUtc_CreatedAt_Id", table: "ScanDrafts");
        migrationBuilder.DropIndex(name: "IX_SimWorkerCommands_Status_ClaimExpiresAtUtc_CreatedRealUtc_Id", table: "SimWorkerCommands");

        migrationBuilder.DropColumn(name: "ProcessingClaimOwner", table: "ScanDrafts");
        migrationBuilder.DropColumn(name: "ProcessingClaimToken", table: "ScanDrafts");
        migrationBuilder.DropColumn(name: "ProcessingClaimExpiresAtUtc", table: "ScanDrafts");
        migrationBuilder.DropColumn(name: "ProcessingAttemptCount", table: "ScanDrafts");
        migrationBuilder.DropColumn(name: "ProcessingLastAttemptAtUtc", table: "ScanDrafts");
        migrationBuilder.DropColumn(name: "ClaimOwner", table: "SimWorkerCommands");
        migrationBuilder.DropColumn(name: "ClaimToken", table: "SimWorkerCommands");
        migrationBuilder.DropColumn(name: "ClaimExpiresAtUtc", table: "SimWorkerCommands");
        migrationBuilder.DropColumn(name: "AttemptCount", table: "SimWorkerCommands");
        migrationBuilder.DropColumn(name: "LastAttemptAtUtc", table: "SimWorkerCommands");

        migrationBuilder.CreateIndex(name: "IX_ScanDrafts_Status", table: "ScanDrafts", column: "Status");
        migrationBuilder.CreateIndex(name: "IX_SimWorkerCommands_Status", table: "SimWorkerCommands", column: "Status");
    }
}
