using System;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace RentalCommand.Data.Migrations;

/// <summary>
/// Destructively replaces pre-foundation lease/payment notice scope with the canonical continuous
/// relationship, tenant account, ledger entry, and recipient identity. No legacy data is retained.
/// </summary>
[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260712120000_CutNoticeDraftsToTenantAccounts")]
public sealed class CutNoticeDraftsToTenantAccounts : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "NoticeDrafts");

        migrationBuilder.CreateTable(
            name: "NoticeDrafts",
            columns: table => new
            {
                Id = table.Column<int>(type: "integer", nullable: false)
                    .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                PortfolioId = table.Column<int>(type: "integer", nullable: false),
                LeaseManagementId = table.Column<int>(type: "integer", nullable: false),
                TenantAccountId = table.Column<int>(type: "integer", nullable: false),
                TenantLedgerEntryId = table.Column<long>(type: "bigint", nullable: true),
                RecipientTenantId = table.Column<int>(type: "integer", nullable: false),
                PropertyId = table.Column<int>(type: "integer", nullable: true),
                NoticeType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                Status = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                Body = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                Reason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: false),
                GenerationPrompt = table.Column<string>(type: "character varying(8000)", maxLength: 8000, nullable: true),
                TriggerDate = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ConversationId = table.Column<int>(type: "integer", nullable: true),
                ApprovedChannels = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                ApprovedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                DismissedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_NoticeDrafts", x => x.Id);
                table.ForeignKey("FK_NoticeDrafts_Conversations_ConversationId", x => x.ConversationId, "Conversations", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey("FK_NoticeDrafts_Portfolios_PortfolioId", x => x.PortfolioId, "Portfolios", "Id", onDelete: ReferentialAction.Cascade);
                table.ForeignKey("FK_NoticeDrafts_Properties_PropertyId", x => x.PropertyId, "Properties", "Id", onDelete: ReferentialAction.SetNull);
                table.ForeignKey(
                    "FK_NoticeDrafts_LeaseManagements_LeaseManagementId_PortfolioId",
                    x => new { x.LeaseManagementId, x.PortfolioId },
                    "LeaseManagements",
                    new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_NoticeDrafts_TenantAccounts_TenantAccountId_PortfolioId",
                    x => new { x.TenantAccountId, x.PortfolioId },
                    "TenantAccounts",
                    new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_NoticeDrafts_TenantLedgerEntries_TenantLedgerEntryId_TenantAccountId_PortfolioId",
                    x => new { x.TenantLedgerEntryId, x.TenantAccountId, x.PortfolioId },
                    "TenantLedgerEntries",
                    new[] { "Id", "TenantAccountId", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    "FK_NoticeDrafts_Tenants_RecipientTenantId_PortfolioId",
                    x => new { x.RecipientTenantId, x.PortfolioId },
                    "Tenants",
                    new[] { "Id", "PortfolioId" },
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex("IX_NoticeDrafts_ConversationId", "NoticeDrafts", "ConversationId");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_LeaseManagementId", "NoticeDrafts", "LeaseManagementId");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_PortfolioId", "NoticeDrafts", "PortfolioId");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_PropertyId", "NoticeDrafts", "PropertyId");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_RecipientTenantId", "NoticeDrafts", "RecipientTenantId");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_Status", "NoticeDrafts", "Status");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_TenantAccountId", "NoticeDrafts", "TenantAccountId");
        migrationBuilder.CreateIndex("IX_NoticeDrafts_TenantLedgerEntryId", "NoticeDrafts", "TenantLedgerEntryId");
        migrationBuilder.CreateIndex(
            "IX_NoticeDrafts_LeaseManagementId_PortfolioId",
            "NoticeDrafts",
            new[] { "LeaseManagementId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            "IX_NoticeDrafts_TenantAccountId_PortfolioId",
            "NoticeDrafts",
            new[] { "TenantAccountId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            "IX_NoticeDrafts_TenantLedgerEntryId_TenantAccountId_PortfolioId",
            "NoticeDrafts",
            new[] { "TenantLedgerEntryId", "TenantAccountId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            "IX_NoticeDrafts_RecipientTenantId_PortfolioId",
            "NoticeDrafts",
            new[] { "RecipientTenantId", "PortfolioId" });
        migrationBuilder.CreateIndex(
            "IX_NoticeDrafts_PortfolioId_LeaseManagementId_NoticeType_Status",
            "NoticeDrafts",
            new[] { "PortfolioId", "LeaseManagementId", "NoticeType", "Status" });
        migrationBuilder.CreateIndex(
            "IX_NoticeDrafts_PortfolioId_TenantLedgerEntryId_NoticeType_Status",
            "NoticeDrafts",
            new[] { "PortfolioId", "TenantLedgerEntryId", "NoticeType", "Status" });

        migrationBuilder.Sql("""
            ALTER TABLE "NoticeDrafts" ENABLE ROW LEVEL SECURITY;
            ALTER TABLE "NoticeDrafts" FORCE ROW LEVEL SECURITY;
            CREATE POLICY tenant_isolation ON "NoticeDrafts"
            USING ("PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int
                   OR current_setting('app.is_admin', true) = 'true')
            WITH CHECK ("PortfolioId" = NULLIF(current_setting('app.current_portfolio_id', true), '')::int
                        OR current_setting('app.is_admin', true) = 'true');
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        throw new NotSupportedException(
            "The destructive notice-foundation cutover intentionally has no legacy-schema rollback.");
    }
}
