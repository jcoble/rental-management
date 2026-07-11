using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260711210000_AddAccountingWorkerClaims")]
public partial class AddAccountingWorkerClaims : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<DateTime>("NextPullAtUtc", "AccountingConnections", "timestamp with time zone", nullable: false);
        AddClaimColumns(migrationBuilder, "Pull");
        AddClaimColumns(migrationBuilder, "TokenRotation");
        migrationBuilder.AddColumn<long>("TokenGeneration", "AccountingConnections", "bigint", nullable: false, defaultValue: 0L);
        migrationBuilder.AddColumn<int>("TokenRotationState", "AccountingConnections", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.CreateIndex(
            "IX_AccountingConnections_PullEligibility", "AccountingConnections",
            new[] { "Status", "PullEnabled", "NextPullAtUtc", "Id" });
        migrationBuilder.CreateIndex(
            "IX_AccountingConnections_ExpiredPullClaim", "AccountingConnections",
            new[] { "PullClaimExpiresAtUtc", "Id" }, filter: "\"PullClaimToken\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            "IX_AccountingConnections_ExpiredTokenRotationClaim", "AccountingConnections",
            new[] { "TokenRotationClaimExpiresAtUtc", "Id" }, filter: "\"TokenRotationClaimToken\" IS NOT NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropIndex("IX_AccountingConnections_PullEligibility", "AccountingConnections");
        migrationBuilder.DropIndex("IX_AccountingConnections_ExpiredPullClaim", "AccountingConnections");
        migrationBuilder.DropIndex("IX_AccountingConnections_ExpiredTokenRotationClaim", "AccountingConnections");
        DropClaimColumns(migrationBuilder, "Pull");
        DropClaimColumns(migrationBuilder, "TokenRotation");
        migrationBuilder.DropColumn("TokenGeneration", "AccountingConnections");
        migrationBuilder.DropColumn("TokenRotationState", "AccountingConnections");
        migrationBuilder.DropColumn("NextPullAtUtc", "AccountingConnections");
    }

    private static void AddClaimColumns(MigrationBuilder migrationBuilder, string prefix)
    {
        migrationBuilder.AddColumn<int>($"{prefix}AttemptCount", "AccountingConnections", "integer", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<DateTime>($"{prefix}ClaimExpiresAtUtc", "AccountingConnections", "timestamp with time zone", nullable: true);
        migrationBuilder.AddColumn<string>($"{prefix}ClaimOwner", "AccountingConnections", "character varying(200)", maxLength: 200, nullable: true);
        migrationBuilder.AddColumn<Guid>($"{prefix}ClaimToken", "AccountingConnections", "uuid", nullable: true);
        migrationBuilder.AddColumn<DateTime>($"{prefix}LastAttemptAtUtc", "AccountingConnections", "timestamp with time zone", nullable: true);
    }

    private static void DropClaimColumns(MigrationBuilder migrationBuilder, string prefix)
    {
        migrationBuilder.DropColumn($"{prefix}AttemptCount", "AccountingConnections");
        migrationBuilder.DropColumn($"{prefix}ClaimExpiresAtUtc", "AccountingConnections");
        migrationBuilder.DropColumn($"{prefix}ClaimOwner", "AccountingConnections");
        migrationBuilder.DropColumn($"{prefix}ClaimToken", "AccountingConnections");
        migrationBuilder.DropColumn($"{prefix}LastAttemptAtUtc", "AccountingConnections");
    }
}
