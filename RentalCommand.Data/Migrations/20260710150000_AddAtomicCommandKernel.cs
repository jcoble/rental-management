using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>
/// Forward-only atomic command kernel. The final baseline is recreated, so this intentionally has
/// no legacy audit backfill or dual-writer compatibility path.
/// </summary>
[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260710150000_AddAtomicCommandKernel")]
public partial class AddAtomicCommandKernel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<Guid>(
            name: "CommandAttemptId",
            table: "AuditLogs",
            type: "uuid",
            nullable: false);

        migrationBuilder.AddColumn<string>(
            name: "CommandIdempotencyKey",
            table: "AuditLogs",
            type: "character varying(200)",
            maxLength: 200,
            nullable: false);

        migrationBuilder.AddColumn<string>(
            name: "CommandType",
            table: "AuditLogs",
            type: "character varying(160)",
            maxLength: 160,
            nullable: false);

        migrationBuilder.AddColumn<long>(
            name: "MutationOrdinal",
            table: "AuditLogs",
            type: "bigint",
            nullable: false);

        migrationBuilder.CreateTable(
            name: "AtomicCommandReceipts",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                CommandType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Status = table.Column<int>(type: "integer", nullable: false),
                ResultContract = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                ResultJson = table.Column<string>(type: "jsonb", nullable: true),
                StartedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                CompletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AtomicCommandReceipts", x => x.Id);
            });

        migrationBuilder.CreateIndex(
            name: "IX_AuditLogs_CommandType_CommandIdempotencyKey_MutationOrdinal",
            table: "AuditLogs",
            columns: new[] { "CommandType", "CommandIdempotencyKey", "MutationOrdinal" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AtomicCommandReceipts_CommandType_IdempotencyKey",
            table: "AtomicCommandReceipts",
            columns: new[] { "CommandType", "IdempotencyKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AtomicCommandReceipts");

        migrationBuilder.DropIndex(
            name: "IX_AuditLogs_CommandType_CommandIdempotencyKey_MutationOrdinal",
            table: "AuditLogs");

        migrationBuilder.DropColumn(name: "CommandAttemptId", table: "AuditLogs");
        migrationBuilder.DropColumn(name: "CommandIdempotencyKey", table: "AuditLogs");
        migrationBuilder.DropColumn(name: "CommandType", table: "AuditLogs");
        migrationBuilder.DropColumn(name: "MutationOrdinal", table: "AuditLogs");
    }
}
