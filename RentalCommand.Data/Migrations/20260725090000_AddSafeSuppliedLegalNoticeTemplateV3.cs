using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data.Notifications;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260725090000_AddSafeSuppliedLegalNoticeTemplateV3")]
public partial class AddSafeSuppliedLegalNoticeTemplateV3 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.InsertData(
            table: "SystemNoticeTemplateVersions",
            columns:
            [
                "Id",
                "Body",
                "Classification",
                "JurisdictionCode",
                "Provenance",
                "PublishedAtUtc",
                "Subject",
                "SystemKey",
                "Version",
            ],
            columnTypes:
            [
                "integer",
                "character varying(8000)",
                "character varying(30)",
                "character varying(80)",
                "character varying(1000)",
                "timestamp with time zone",
                "character varying(200)",
                "character varying(80)",
                "integer",
            ],
            values: new object[,]
            {
                {
                    8,
                    SuppliedNoticeTemplateBaseline.V3Legal[0].Body,
                    "Legal",
                    null,
                    SuppliedNoticeTemplateBaseline.SafeLegalV3Provenance,
                    SuppliedNoticeTemplateBaseline.SafeLegalV3PublishedAtUtc,
                    SuppliedNoticeTemplateBaseline.V3Legal[0].Subject,
                    SuppliedNoticeTemplateBaseline.V3Legal[0].SystemKey,
                    SuppliedNoticeTemplateBaseline.SafeLegalVersion,
                },
                {
                    9,
                    SuppliedNoticeTemplateBaseline.V3Legal[1].Body,
                    "Legal",
                    null,
                    SuppliedNoticeTemplateBaseline.SafeLegalV3Provenance,
                    SuppliedNoticeTemplateBaseline.SafeLegalV3PublishedAtUtc,
                    SuppliedNoticeTemplateBaseline.V3Legal[1].Subject,
                    SuppliedNoticeTemplateBaseline.V3Legal[1].SystemKey,
                    SuppliedNoticeTemplateBaseline.SafeLegalVersion,
                },
            });

        migrationBuilder.Sql("""
            WITH qualifying AS MATERIALIZED
            (
                SELECT
                    policy."Id" AS "PolicyId",
                    policy."PortfolioId",
                    policy."AutomationKey",
                    policy."WorkspaceNoticeTemplateVersionId" AS "CurrentTemplateId",
                    current_template."CreatedByUserId",
                    latest_system."Id" AS "LatestSystemTemplateId",
                    latest_system."Version" AS "LatestVersion",
                    latest_system."Subject" AS "LatestSubject",
                    latest_system."Body" AS "LatestBody",
                    latest_system."JurisdictionCode" AS "LatestJurisdictionCode"
                FROM "TenantNoticePolicies" AS policy
                JOIN "WorkspaceNoticeTemplateVersions" AS current_template
                  ON current_template."Id" = policy."WorkspaceNoticeTemplateVersionId"
                 AND current_template."PortfolioId" = policy."PortfolioId"
                 AND current_template."SystemKey" = policy."AutomationKey"
                JOIN "SystemNoticeTemplateVersions" AS supplied_v2
                  ON supplied_v2."Id" = current_template."BasedOnSystemTemplateVersionId"
                 AND supplied_v2."SystemKey" = current_template."SystemKey"
                 AND supplied_v2."Version" = current_template."Version"
                JOIN "SystemNoticeTemplateVersions" AS latest_system
                  ON latest_system."SystemKey" = supplied_v2."SystemKey"
                 AND latest_system."Version" = 3
                WHERE policy."Mode" = 'Draft'
                  AND policy."Classification" = 'Legal'
                  AND policy."ReviewedJurisdictionCode" IS NULL
                  AND policy."JurisdictionReviewedAtUtc" IS NULL
                  AND policy."JurisdictionReviewedByUserId" IS NULL
                  AND current_template."Version" = 2
                  AND current_template."BasedOnSystemTemplateVersionId" IN (6, 7)
                  AND current_template."IsCustomized" = FALSE
                  AND current_template."Subject" = supplied_v2."Subject"
                  AND current_template."Body" = supplied_v2."Body"
                  AND current_template."JurisdictionCode" IS NOT DISTINCT FROM supplied_v2."JurisdictionCode"
                  AND current_template."JurisdictionCode" IS NULL
                  AND current_template."JurisdictionReviewedAtUtc" IS NULL
                  AND current_template."JurisdictionReviewedByUserId" IS NULL
                  AND supplied_v2."Classification" = 'Legal'
                  AND latest_system."Classification" = 'Legal'
            ),
            inserted AS
            (
                INSERT INTO "WorkspaceNoticeTemplateVersions"
                (
                    "PortfolioId",
                    "SystemKey",
                    "Version",
                    "BasedOnSystemTemplateVersionId",
                    "IsCustomized",
                    "Subject",
                    "Body",
                    "JurisdictionCode",
                    "JurisdictionReviewedAtUtc",
                    "JurisdictionReviewedByUserId",
                    "CreatedByUserId",
                    "CreatedAtUtc"
                )
                SELECT
                    qualifying."PortfolioId",
                    qualifying."AutomationKey",
                    qualifying."LatestVersion",
                    qualifying."LatestSystemTemplateId",
                    FALSE,
                    qualifying."LatestSubject",
                    qualifying."LatestBody",
                    qualifying."LatestJurisdictionCode",
                    NULL,
                    NULL,
                    qualifying."CreatedByUserId",
                    TIMESTAMPTZ '2026-07-25 09:00:00+00'
                FROM qualifying
                RETURNING "Id", "PortfolioId", "SystemKey"
            )
            UPDATE "TenantNoticePolicies" AS policy
            SET
                "WorkspaceNoticeTemplateVersionId" = inserted."Id",
                "UpdatedAtUtc" = TIMESTAMPTZ '2026-07-25 09:00:00+00'
            FROM qualifying
            JOIN inserted
              ON inserted."PortfolioId" = qualifying."PortfolioId"
             AND inserted."SystemKey" = qualifying."AutomationKey"
            WHERE policy."Id" = qualifying."PolicyId"
              AND policy."PortfolioId" = qualifying."PortfolioId"
              AND policy."AutomationKey" = qualifying."AutomationKey"
              AND policy."WorkspaceNoticeTemplateVersionId" = qualifying."CurrentTemplateId";
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Supplied legal notice v3 is append-only and may be referenced by immutable workspace history.");
}
