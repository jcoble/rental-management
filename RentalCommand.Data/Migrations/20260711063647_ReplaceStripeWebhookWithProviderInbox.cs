using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class ReplaceStripeWebhookWithProviderInbox : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StripeWebhookEvents");

            migrationBuilder.CreateTable(
                name: "ProviderInboxEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: true),
                    Provider = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ProviderEventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Payload = table.Column<string>(type: "jsonb", nullable: false),
                    ProviderObjectId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ReceivedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    LastAttemptAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ClaimOwner = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    ClaimToken = table.Column<Guid>(type: "uuid", nullable: true),
                    ClaimExpiresAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ProcessedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    DeadLetteredAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    FailureKind = table.Column<int>(type: "integer", nullable: true),
                    LastError = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProviderInboxEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_ClaimExpiresAtUtc_Id",
                table: "ProviderInboxEvents",
                columns: new[] { "ClaimExpiresAtUtc", "Id" },
                filter: "\"ProcessedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL AND \"ClaimToken\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_NextAttemptAtUtc_ReceivedAtUtc_Id",
                table: "ProviderInboxEvents",
                columns: new[] { "NextAttemptAtUtc", "ReceivedAtUtc", "Id" },
                filter: "\"ProcessedAtUtc\" IS NULL AND \"DeadLetteredAtUtc\" IS NULL AND \"ClaimToken\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_Provider_ProviderEventId",
                table: "ProviderInboxEvents",
                columns: new[] { "Provider", "ProviderEventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProviderInboxEvents_Provider_ProviderObjectId",
                table: "ProviderInboxEvents",
                columns: new[] { "Provider", "ProviderObjectId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProviderInboxEvents");

            migrationBuilder.CreateTable(
                name: "StripeWebhookEvents",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    EventId = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    EventType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    ProcessedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    ReceivedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StripeWebhookEvents", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StripeWebhookEvents_EventId",
                table: "StripeWebhookEvents",
                column: "EventId",
                unique: true);
        }
    }
}
