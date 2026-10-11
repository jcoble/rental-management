using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260805010000_AddDemoSeedPortfolioDiscoveryFunction")]
public sealed class AddDemoSeedPortfolioDiscoveryFunction : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.rc_demo_seed_portfolio_ids()
            RETURNS TABLE ("Value" integer)
            LANGUAGE sql
            STABLE
            SECURITY DEFINER
            SET search_path = pg_catalog, public
            AS $function$
              SELECT DISTINCT relationship."PortfolioId" AS "Value"
              FROM public."LeaseManagements" AS relationship
              WHERE relationship."RelationshipNumber" LIKE 'DEMO-LM-%'
            $function$;

            ALTER FUNCTION public.rc_demo_seed_portfolio_ids()
              OWNER TO rentalcommand_rls_authority;
            REVOKE ALL ON FUNCTION public.rc_demo_seed_portfolio_ids()
              FROM PUBLIC;
            REVOKE ALL ON FUNCTION public.rc_demo_seed_portfolio_ids()
              FROM rentalcommand_engine;
            GRANT EXECUTE ON FUNCTION public.rc_demo_seed_portfolio_ids()
              TO rentalcommand_api;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // Forward-only: keep the additive helper installed during a partial rollback.
    }
}
