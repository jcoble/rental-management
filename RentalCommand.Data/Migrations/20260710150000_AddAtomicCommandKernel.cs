using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>
/// Opt-in atomic command kernel. Its audit table is separate from the existing application audit
/// table so unconverted write paths keep their established behavior during incremental adoption.
/// </summary>
[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260710150000_AddAtomicCommandKernel")]
public partial class AddAtomicCommandKernel : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "AtomicAuditLogs",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", Npgsql.EntityFrameworkCore.PostgreSQL.Metadata.NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                AttemptId = table.Column<Guid>(type: "uuid", nullable: false),
                CommandType = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                CommandIdempotencyKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                MutationOrdinal = table.Column<long>(type: "bigint", nullable: false),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                UserId = table.Column<int>(type: "integer", nullable: true),
                ActorLabel = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: true),
                EntityType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                EntityId = table.Column<int>(type: "integer", nullable: false),
                Operation = table.Column<int>(type: "integer", nullable: false),
                OldValues = table.Column<string>(type: "jsonb", nullable: true),
                NewValues = table.Column<string>(type: "jsonb", nullable: true),
                ChangeReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                Timestamp = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                IpAddress = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_AtomicAuditLogs", x => x.Id);
            });

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
            name: "IX_AtomicAuditLogs_CommandType_CommandIdempotencyKey_MutationOrdinal",
            table: "AtomicAuditLogs",
            columns: new[] { "CommandType", "CommandIdempotencyKey", "MutationOrdinal" },
            unique: true);

        migrationBuilder.CreateIndex(
            name: "IX_AtomicAuditLogs_EntityType_EntityId",
            table: "AtomicAuditLogs",
            columns: new[] { "EntityType", "EntityId" });

        migrationBuilder.CreateIndex(
            name: "IX_AtomicAuditLogs_PortfolioId_Timestamp",
            table: "AtomicAuditLogs",
            columns: new[] { "PortfolioId", "Timestamp" });

        migrationBuilder.CreateIndex(
            name: "IX_AtomicCommandReceipts_CommandType_IdempotencyKey",
            table: "AtomicCommandReceipts",
            columns: new[] { "CommandType", "IdempotencyKey" },
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "AtomicAuditLogs");
        migrationBuilder.DropTable(name: "AtomicCommandReceipts");
    }
}
