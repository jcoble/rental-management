using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using RentalCommand.Data.Notifications;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260724140000_AddSuppliedLegalNoticeTemplateV2")]
public partial class AddSuppliedLegalNoticeTemplateV2 : Migration
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
                    6,
                    SuppliedNoticeTemplateBaseline.V2Legal[0].Body,
                    "Legal",
                    null,
                    SuppliedNoticeTemplateBaseline.LegalV2Provenance,
                    SuppliedNoticeTemplateBaseline.LegalV2PublishedAtUtc,
                    SuppliedNoticeTemplateBaseline.V2Legal[0].Subject,
                    SuppliedNoticeTemplateBaseline.V2Legal[0].SystemKey,
                    SuppliedNoticeTemplateBaseline.LegalVersion,
                },
                {
                    7,
                    SuppliedNoticeTemplateBaseline.V2Legal[1].Body,
                    "Legal",
                    null,
                    SuppliedNoticeTemplateBaseline.LegalV2Provenance,
                    SuppliedNoticeTemplateBaseline.LegalV2PublishedAtUtc,
                    SuppliedNoticeTemplateBaseline.V2Legal[1].Subject,
                    SuppliedNoticeTemplateBaseline.V2Legal[1].SystemKey,
                    SuppliedNoticeTemplateBaseline.LegalVersion,
                },
            });

        migrationBuilder.Sql(FoundationBaselinePostgreSql.RlsAuthorityFunctionSqlV20260724);
        migrationBuilder.Sql("""
            GRANT DELETE ON TABLE public."WorkspaceNoticeTemplateVersions"
              TO rentalcommand_rls_authority;
            GRANT SELECT ON TABLE public."TenantNoticePolicies"
              TO rentalcommand_rls_authority;

            CREATE OR REPLACE FUNCTION rc_delete_fresh_workspace_notice_templates(
              target_user_id integer,
              target_portfolio_id integer,
              target_template_ids integer[])
            RETURNS integer
            LANGUAGE plpgsql
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
            DECLARE
              deleted_count integer;
            BEGIN
              IF session_user IS DISTINCT FROM 'rentalcommand_api' THEN
                RAISE EXCEPTION 'Fresh workspace notice replacement is API-only'
                  USING ERRCODE = '42501';
              END IF;
              IF target_user_id IS NULL OR target_user_id <= 0
                 OR target_portfolio_id IS NULL OR target_portfolio_id <= 0
                 OR cardinality(target_template_ids) <> 2
                 OR (SELECT COUNT(DISTINCT template_id)
                     FROM unnest(target_template_ids) AS template_id) <> 2 THEN
                RAISE EXCEPTION 'Fresh workspace notice replacement requires two distinct templates'
                  USING ERRCODE = '22023';
              END IF;
              IF NOT EXISTS (
                SELECT 1
                FROM public."AtomicCommandReceipts" receipt
                JOIN public."AspNetUsers" user_row
                  ON user_row."Id" = target_user_id
                 AND user_row.xmin = pg_current_xact_id()::xid
                JOIN public."Portfolios" portfolio
                  ON portfolio."Id" = target_portfolio_id
                 AND portfolio.xmin = pg_current_xact_id()::xid
                JOIN public."WorkspaceAccessContexts" access_context
                  ON access_context."PortfolioId" = portfolio."Id"
                 AND access_context."UserId" = user_row."Id"
                 AND access_context.xmin = pg_current_xact_id()::xid
                WHERE receipt."CommandType" = 'auth.account.bootstrap'
                  AND receipt."IdempotencyKey" ~ '^email:[0-9a-f]{64}$'
                  AND receipt.xmin = pg_current_xact_id()::xid
              ) THEN
                RAISE EXCEPTION 'Fresh workspace notice replacement lacks transaction-bound authority'
                  USING ERRCODE = '42501';
              END IF;

              DELETE FROM public."WorkspaceNoticeTemplateVersions" current_template
              WHERE current_template."Id" = ANY(target_template_ids)
                AND current_template."PortfolioId" = target_portfolio_id
                AND current_template."Version" = 1
                AND current_template."IsCustomized" = FALSE
                AND current_template.xmin = pg_current_xact_id()::xid
                AND current_template."SystemKey" IN (
                  'lease-non-renewal',
                  'late-rent-late-fee')
                AND EXISTS (
                  SELECT 1
                  FROM public."SystemNoticeTemplateVersions" latest
                  JOIN public."WorkspaceNoticeTemplateVersions" replacement
                    ON replacement."PortfolioId" = target_portfolio_id
                   AND replacement."SystemKey" = latest."SystemKey"
                   AND replacement."Version" = latest."Version"
                   AND replacement."BasedOnSystemTemplateVersionId" = latest."Id"
                   AND replacement."IsCustomized" = FALSE
                   AND replacement.xmin = pg_current_xact_id()::xid
                  JOIN public."TenantNoticePolicies" policy
                    ON policy."PortfolioId" = replacement."PortfolioId"
                   AND policy."AutomationKey" = replacement."SystemKey"
                   AND policy."WorkspaceNoticeTemplateVersionId" = replacement."Id"
                   AND policy."Mode" = 'Draft'
                   AND policy.xmin = pg_current_xact_id()::xid
                  WHERE latest."SystemKey" = current_template."SystemKey"
                    AND latest."Classification" = 'Legal'
                    AND latest."Version" > current_template."Version"
                    AND NOT EXISTS (
                      SELECT 1
                      FROM public."SystemNoticeTemplateVersions" candidate
                      WHERE candidate."SystemKey" = latest."SystemKey"
                        AND candidate."Version" > latest."Version")
                );
              GET DIAGNOSTICS deleted_count = ROW_COUNT;
              IF deleted_count <> 2 THEN
                RAISE EXCEPTION 'Fresh workspace notice replacement did not delete exactly two v1 rows'
                  USING ERRCODE = 'P0001';
              END IF;
              RETURN deleted_count;
            END;
            $function$;

            ALTER FUNCTION rc_delete_fresh_workspace_notice_templates(integer, integer, integer[])
              OWNER TO rentalcommand_rls_authority;
            REVOKE ALL ON FUNCTION rc_delete_fresh_workspace_notice_templates(integer, integer, integer[])
              FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION rc_delete_fresh_workspace_notice_templates(integer, integer, integer[])
              TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Supplied legal notice v2 is append-only and may be referenced by immutable workspace history.");
}
