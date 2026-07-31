using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260728223000_AddAssignedWorkOrderSafeDetailContext")]
public partial class AddAssignedWorkOrderSafeDetailContext : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            CREATE OR REPLACE FUNCTION public.rc_api_assigned_work_order_detail_context(
              target_portfolio_id integer,
              target_work_order_id integer)
            RETURNS TABLE (
              "WorkOrderId" integer,
              "PropertyName" text,
              "AddressLine1" text,
              "AddressLine2" text,
              "City" text,
              "State" text,
              "PostalCode" text,
              "UnitNumber" text,
              "TenantName" text)
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
              SELECT work_order."Id" AS "WorkOrderId",
                     property."Name"::text AS "PropertyName",
                     property."AddressLine1"::text AS "AddressLine1",
                     property."AddressLine2"::text AS "AddressLine2",
                     property."City"::text AS "City",
                     property."State"::text AS "State",
                     property."PostalCode"::text AS "PostalCode",
                     unit."UnitNumber"::text AS "UnitNumber",
                     NULLIF(BTRIM(work_order."RequesterName"), '')::text AS "TenantName"
              FROM public."WorkOrders" work_order
              JOIN public."Properties" property
                ON property."Id" = work_order."PropertyId"
               AND property."PortfolioId" = work_order."PortfolioId"
              LEFT JOIN public."Units" unit
                ON unit."Id" = work_order."UnitId"
               AND unit."PortfolioId" = work_order."PortfolioId"
              WHERE session_user = 'rentalcommand_api'
                AND work_order."PortfolioId" = target_portfolio_id
                AND (target_work_order_id IS NULL
                     OR work_order."Id" = target_work_order_id)
                AND work_order."DeletedAt" IS NULL
                AND public.rc_api_resource_scope_allows(
                      target_portfolio_id,
                      work_order."PropertyId",
                      work_order."UnitId",
                      work_order."Id",
                      work_order."LeaseManagementId",
                      NULL,
                      work_order."TenantId",
                      FALSE,
                      FALSE,
                      TRUE);
            $function$;

            ALTER FUNCTION public.rc_api_assigned_work_order_detail_context(integer, integer)
              OWNER TO rentalcommand_rls_authority;
            REVOKE ALL ON FUNCTION public.rc_api_assigned_work_order_detail_context(integer, integer)
              FROM PUBLIC;
            GRANT EXECUTE ON FUNCTION public.rc_api_assigned_work_order_detail_context(integer, integer)
              TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            DROP FUNCTION IF EXISTS
              public.rc_api_assigned_work_order_detail_context(integer, integer);
            """);
    }
}
