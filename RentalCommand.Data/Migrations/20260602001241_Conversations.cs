using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations
{
    /// <inheritdoc />
    public partial class Conversations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Conversations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PortfolioId = table.Column<int>(type: "integer", nullable: false),
                    TenantId = table.Column<int>(type: "integer", nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PropertyId = table.Column<int>(type: "integer", nullable: true),
                    StartedByLandlord = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessageAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    LastMessagePreview = table.Column<string>(type: "character varying(280)", maxLength: 280, nullable: true),
                    LandlordUnreadCount = table.Column<int>(type: "integer", nullable: false),
                    TenantUnreadCount = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Conversations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Conversations_Portfolios_PortfolioId",
                        column: x => x.PortfolioId,
                        principalTable: "Portfolios",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Conversations_Properties_PropertyId",
                        column: x => x.PropertyId,
                        principalTable: "Properties",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Conversations_Tenants_TenantId",
                        column: x => x.TenantId,
                        principalTable: "Tenants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ConversationMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ConversationId = table.Column<int>(type: "integer", nullable: false),
                    SenderRole = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    Channels = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ConversationMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ConversationMessages_Conversations_ConversationId",
                        column: x => x.ConversationId,
                        principalTable: "Conversations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ConversationMessages_ConversationId",
                table: "ConversationMessages",
                column: "ConversationId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_LastMessageAt",
                table: "Conversations",
                column: "LastMessageAt");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_PortfolioId_TenantId",
                table: "Conversations",
                columns: new[] { "PortfolioId", "TenantId" });

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_PropertyId",
                table: "Conversations",
                column: "PropertyId");

            migrationBuilder.CreateIndex(
                name: "IX_Conversations_TenantId",
                table: "Conversations",
                column: "TenantId");

            // ---------------------------------------------------------------------------------------
            // Backfill: migrate existing PortalMessages (the deprecated flat "ticket" model) into the
            // new threaded Conversation model so current test threads survive. Each eligible PortalMessage
            // becomes ONE Conversation plus a ConversationMessage for its Body and, if present, a second
            // ConversationMessage for its Reply (opposite sender role).
            //
            // Tenant resolution: RecipientTenantId when set (landlord-authored), otherwise the TenantId
            // of the authoring portal UserAccount. Messages where neither resolves are skipped — they
            // cannot be attributed to a tenant under the per-tenant topic-thread model.
            //
            // SenderRole mapping: FromLandlord => first message Landlord / reply Tenant; otherwise the
            // first message is Tenant and the reply (a landlord response) is Landlord.
            //
            // The PortalMessages table is intentionally left in place (deprecated, no longer surfaced).
            // ---------------------------------------------------------------------------------------
            migrationBuilder.Sql("""
                WITH src AS (
                    SELECT
                        pm."Id"            AS pm_id,
                        pm."PortfolioId"   AS portfolio_id,
                        COALESCE(pm."RecipientTenantId", ua."TenantId") AS tenant_id,
                        pm."PropertyId"    AS property_id,
                        pm."FromLandlord"  AS from_landlord,
                        pm."Subject"       AS subject,
                        pm."Body"          AS body,
                        pm."Reply"         AS reply,
                        pm."Channels"      AS channels,
                        pm."CreatedAt"     AS created_at,
                        pm."UpdatedAt"     AS updated_at
                    FROM "PortalMessages" pm
                    LEFT JOIN "UserAccounts" ua ON ua."Id" = pm."UserAccountId"
                ),
                eligible AS (
                    SELECT * FROM src WHERE tenant_id IS NOT NULL
                ),
                ins_conv AS (
                    INSERT INTO "Conversations"
                        ("PortfolioId", "TenantId", "Subject", "PropertyId", "StartedByLandlord",
                         "CreatedAt", "LastMessageAt", "LastMessagePreview",
                         "LandlordUnreadCount", "TenantUnreadCount")
                    SELECT
                        e.portfolio_id,
                        e.tenant_id,
                        LEFT(e.subject, 200),
                        e.property_id,
                        e.from_landlord,
                        e.created_at,
                        e.updated_at,
                        LEFT(COALESCE(NULLIF(e.reply, ''), e.body), 280),
                        0,
                        0
                    FROM eligible e
                    ORDER BY e.pm_id
                    RETURNING "Id" AS conv_id, "CreatedAt" AS conv_created_at
                ),
                -- Re-pair inserted conversations back to their source PortalMessage by row order.
                -- Both sides are ordered by pm_id, so ROW_NUMBER() lines them up deterministically.
                conv_numbered AS (
                    SELECT conv_id, ROW_NUMBER() OVER (ORDER BY conv_id) AS rn FROM ins_conv
                ),
                src_numbered AS (
                    SELECT e.*, ROW_NUMBER() OVER (ORDER BY e.pm_id) AS rn FROM eligible e
                ),
                paired AS (
                    SELECT c.conv_id, s.*
                    FROM conv_numbered c
                    JOIN src_numbered s ON s.rn = c.rn
                )
                INSERT INTO "ConversationMessages"
                    ("ConversationId", "SenderRole", "Body", "Channels", "CreatedAt")
                -- Original body message.
                SELECT
                    p.conv_id,
                    CASE WHEN p.from_landlord THEN 'Landlord' ELSE 'Tenant' END,
                    LEFT(p.body, 4000),
                    CASE WHEN p.from_landlord THEN p.channels ELSE NULL END,
                    p.created_at
                FROM paired p
                UNION ALL
                -- Reply message (opposite role), only when a non-empty reply exists.
                SELECT
                    p.conv_id,
                    CASE WHEN p.from_landlord THEN 'Tenant' ELSE 'Landlord' END,
                    LEFT(p.reply, 4000),
                    NULL,
                    p.updated_at
                FROM paired p
                WHERE p.reply IS NOT NULL AND p.reply <> '';
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ConversationMessages");

            migrationBuilder.DropTable(
                name: "Conversations");
        }
    }
}
