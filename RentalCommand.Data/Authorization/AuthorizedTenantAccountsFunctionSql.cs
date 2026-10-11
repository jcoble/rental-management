namespace RentalCommand.Data.Authorization;

/// <summary>
/// PostgreSQL authority relation for staff tenant-account reads. The function is security-definer so
/// the API does not invoke the per-row resource RLS predicate after it has already resolved the
/// caller's property capability set. Its result is still a translated relational source for EF.
/// </summary>
internal static class AuthorizedTenantAccountsFunctionSql
{
    internal const string Signature =
        "rc_api_authorized_tenant_accounts(integer, uuid, integer, integer, bigint, text[])";

    internal const string Create = """
        CREATE OR REPLACE FUNCTION rc_api_authorized_tenant_accounts(
          target_portfolio_id integer,
          expected_session_id uuid,
          expected_user_id integer,
          expected_access_context_id integer,
          expected_access_revision bigint,
          capability_keys text[])
        RETURNS TABLE (
          "Id" integer,
          "PublicId" uuid,
          "PortfolioId" integer,
          "LeaseManagementId" integer,
          "AccountNumber" text,
          "Currency" text,
          "RentTrackingStartOn" date,
          "OpenedAtUtc" timestamp with time zone,
          "ClosedAtUtc" timestamp with time zone,
          "CloseReasonCode" text,
          "CloseNote" text,
          "CreatedAtUtc" timestamp with time zone,
          "CreatedByUserId" integer)
        LANGUAGE sql
        STABLE
        SECURITY DEFINER
        SET search_path = pg_catalog, public
        AS $function$
          WITH effective_scopes AS MATERIALIZED (
            SELECT effective_scope."ScopeKind", effective_scope."PropertyId"
            FROM public.rc_api_effective_capability_scopes(
              target_portfolio_id,
              expected_session_id,
              expected_user_id,
              expected_access_context_id,
              expected_access_revision,
              capability_keys,
              'Property') AS effective_scope
          ),
          authorized_properties AS MATERIALIZED (
            SELECT DISTINCT property."Id" AS "PropertyId"
            FROM public."Properties" AS property
            CROSS JOIN effective_scopes AS effective_scope
            WHERE property."PortfolioId" = target_portfolio_id
              AND property."DeletedAt" IS NULL
              AND (effective_scope."ScopeKind" = 'AllProperties'
                   OR (effective_scope."ScopeKind" = 'SelectedProperties'
                       AND effective_scope."PropertyId" = property."Id"))
          )
          SELECT account."Id",
                 account."PublicId",
                 account."PortfolioId",
                 account."LeaseManagementId",
                 account."AccountNumber",
                 account."Currency",
                 account."RentTrackingStartOn",
                 account."OpenedAtUtc",
                 account."ClosedAtUtc",
                 account."CloseReasonCode",
                 account."CloseNote",
                 account."CreatedAtUtc",
                 account."CreatedByUserId"
          FROM public."TenantAccounts" AS account
          JOIN public."LeaseManagements" AS management
            ON management."PortfolioId" = account."PortfolioId"
           AND management."Id" = account."LeaseManagementId"
          JOIN authorized_properties AS authorized_property
            ON authorized_property."PropertyId" = management."PropertyId"
          WHERE account."PortfolioId" = target_portfolio_id;
        $function$;

        ALTER FUNCTION rc_api_authorized_tenant_accounts(
          integer, uuid, integer, integer, bigint, text[])
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION rc_api_authorized_tenant_accounts(
          integer, uuid, integer, integer, bigint, text[]) FROM PUBLIC;
        GRANT EXECUTE ON FUNCTION rc_api_authorized_tenant_accounts(
          integer, uuid, integer, integer, bigint, text[])
          TO rentalcommand_api, rentalcommand_engine;
        """;

    internal const string Drop = """
        DROP FUNCTION IF EXISTS rc_api_authorized_tenant_accounts(
          integer, uuid, integer, integer, bigint, text[]);
        """;
}
