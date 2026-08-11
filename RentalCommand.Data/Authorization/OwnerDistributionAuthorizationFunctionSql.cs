namespace RentalCommand.Data.Authorization;

/// <summary>
/// Privileged relational source for the owner-distribution scope check. Property RLS hides
/// unselected properties from the API, but the fail-closed owner rule must count every current,
/// non-deleted ownership in the portfolio before allowing a propertyless payout.
/// </summary>
internal static class OwnerDistributionAuthorizationFunctionSql
{
    internal const string Signature =
        "rc_api_owner_distribution_active_properties(integer, timestamp with time zone)";

    internal const string Create = """
        CREATE OR REPLACE FUNCTION rc_api_owner_distribution_active_properties(
          target_portfolio_id integer,
          active_at timestamp with time zone)
        RETURNS TABLE (
          "OwnerEntityId" integer,
          "PropertyId" integer)
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          SELECT ownership."OwnerEntityId",
                 ownership."PropertyId"
          FROM public."PropertyOwnerships" AS ownership
          INNER JOIN public."Properties" AS property
            ON property."PortfolioId" = ownership."PortfolioId"
           AND property."Id" = ownership."PropertyId"
           AND property."DeletedAt" IS NULL
          INNER JOIN public."Portfolios" AS portfolio
            ON portfolio."Id" = ownership."PortfolioId"
           AND portfolio."DeletedAt" IS NULL
          WHERE session_user = 'rentalcommand_api'
            AND target_portfolio_id > 0
            AND public.rc_api_scope_allows(target_portfolio_id)
            AND ownership."PortfolioId" = target_portfolio_id
            AND ownership."EffectiveFromUtc" <= active_at
            AND (ownership."EffectiveToUtc" IS NULL
                 OR ownership."EffectiveToUtc" > active_at);
        $function$;

        ALTER FUNCTION rc_api_owner_distribution_active_properties(
          integer, timestamp with time zone)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_api_owner_distribution_active_properties(
          integer, timestamp with time zone) FROM PUBLIC;
        REVOKE ALL ON FUNCTION rc_api_owner_distribution_active_properties(
          integer, timestamp with time zone) FROM rentalcommand_engine;
        GRANT EXECUTE ON FUNCTION rc_api_owner_distribution_active_properties(
          integer, timestamp with time zone) TO rentalcommand_api;
        """;

    internal const string Drop = """
        DROP FUNCTION IF EXISTS rc_api_owner_distribution_active_properties(
          integer, timestamp with time zone);
        """;
}
