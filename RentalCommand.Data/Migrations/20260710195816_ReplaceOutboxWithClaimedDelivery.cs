using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>
/// Clean forward replacement. This application has no live outbox data to preserve, so the old
/// ambiguous sent/retry shape is deliberately removed instead of bridged or backfilled.
/// </summary>
public partial class ReplaceOutboxWithClaimedDelivery : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OutboxMessages");

        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                PortfolioId = table.Column<int>(type: "integer", nullable: true),
                MessageType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                AttemptCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                AcceptedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeliveredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DeadLetteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Provider = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                ProviderMessageId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                FailureKind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: true),
                LastError = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessages", row => row.Id);
                table.ForeignKey(
                    name: "FK_OutboxMessages_Portfolios_PortfolioId",
                    column: row => row.PortfolioId,
                    principalTable: "Portfolios",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_ExpiredClaim",
            table: "OutboxMessages",
            column: "ClaimExpiresAtUtc",
            filter: "\"ClaimToken\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_IdempotencyKey",
            table: "OutboxMessages",
            column: "IdempotencyKey",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_PortfolioId",
            table: "OutboxMessages",
            column: "PortfolioId");
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_ProviderReceipt",
            table: "OutboxMessages",
            columns: new[] { "Provider", "ProviderMessageId" },
            filter: "\"ProviderMessageId\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_Ready",
            table: "OutboxMessages",
            columns: new[] { "NextAttemptAtUtc", "CreatedAtUtc", "Id" },
            filter: "\"AcceptedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "OutboxMessages");

        migrationBuilder.CreateTable(
            name: "OutboxMessages",
            columns: table => new
            {
                Id = table.Column<long>(type: "bigint", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                PortfolioId = table.Column<int>(type: "integer", nullable: true),
                MessageType = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                Payload = table.Column<string>(type: "jsonb", nullable: false),
                DedupKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                RetryCount = table.Column<int>(type: "integer", nullable: false),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                SentAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                FailedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                Error = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_OutboxMessages", row => row.Id);
                table.ForeignKey(
                    name: "FK_OutboxMessages_Portfolios_PortfolioId",
                    column: row => row.PortfolioId,
                    principalTable: "Portfolios",
                    principalColumn: "Id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(name: "IX_OutboxMessages_PortfolioId", table: "OutboxMessages", column: "PortfolioId");
        migrationBuilder.CreateIndex(name: "IX_OutboxMessages_SentAt", table: "OutboxMessages", column: "SentAt");
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_DedupKey",
            table: "OutboxMessages",
            column: "DedupKey",
            filter: "\"DedupKey\" IS NOT NULL");
        migrationBuilder.CreateIndex(
            name: "IX_OutboxMessages_Unsent_CreatedAt",
            table: "OutboxMessages",
            columns: new[] { "CreatedAt", "RetryCount" },
            filter: "\"SentAt\" IS NULL");
    }
}
