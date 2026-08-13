namespace RentalCommand.Data.Authorization;

/// <summary>
/// Canonical PostgreSQL rendering of the effective property-authorization policy for consumers
/// whose security timestamp is supplied by the application rather than the simulation clock.
/// Keep this database-side rendering aligned with <see cref="WorkspaceAuthorizationQuery.AuthorizedAssignmentsForScope"/>.
/// Raw-SQL aggregates call this function instead of copying the authorization joins and predicates.
/// </summary>
internal static class AuthorizedPropertyIdsAtSecurityTimeFunctionSql
{
    internal const string Signature =
        "rc_api_authorized_property_ids_at_security_time(integer, uuid, integer, integer, bigint, text[], timestamp with time zone)";

    internal const string Create = """
        CREATE OR REPLACE FUNCTION public.rc_api_authorized_property_ids_at_security_time(
          target_portfolio_id integer,
          expected_session_id uuid,
          expected_user_id integer,
          expected_access_context_id integer,
          expected_access_revision bigint,
          capability_keys text[],
          security_now_utc timestamp with time zone)
        RETURNS TABLE ("Value" integer)
        LANGUAGE sql
        STABLE
        SECURITY INVOKER
        SET search_path = pg_catalog, public
        AS $function$
          WITH effective_assignments AS MATERIALIZED (
            SELECT assignment."Id", assignment."ScopeKind"
            FROM public."AuthSessions" session
            JOIN public."WorkspaceAccessContexts" access_context
              ON access_context."Id" = expected_access_context_id
             AND access_context."Id" = session."ActiveAccessContextId"
             AND access_context."UserId" = session."UserId"
             AND access_context."PortfolioId" = target_portfolio_id
             AND access_context."AccessRevision" = expected_access_revision
             AND access_context."Status" = 'Active'
             AND access_context."SuspendedAtUtc" IS NULL
             AND access_context."RevokedAtUtc" IS NULL
            JOIN public."WorkspaceMemberships" membership
              ON membership."AccessContextId" = access_context."Id"
             AND membership."PortfolioId" = access_context."PortfolioId"
             AND membership."Status" = 'Active'
             AND membership."SuspendedAtUtc" IS NULL
             AND membership."RevokedAtUtc" IS NULL
             AND membership."EffectiveFromUtc" <= security_now_utc
             AND (membership."EffectiveToUtc" IS NULL
                  OR membership."EffectiveToUtc" > security_now_utc)
            JOIN public."MembershipRoleAssignments" assignment
              ON assignment."WorkspaceMembershipId" = membership."Id"
             AND assignment."PortfolioId" = membership."PortfolioId"
             AND assignment."Status" = 'Active'
             AND assignment."SuspendedAtUtc" IS NULL
             AND assignment."RevokedAtUtc" IS NULL
             AND assignment."EffectiveFromUtc" <= security_now_utc
             AND (assignment."EffectiveToUtc" IS NULL
                  OR assignment."EffectiveToUtc" > security_now_utc)
            JOIN public."RoleProfileCapabilities" role_capability
              ON role_capability."RoleProfileId" = assignment."RoleProfileId"
            JOIN public."CapabilityDefinitions" capability
              ON capability."Id" = role_capability."CapabilityDefinitionId"
             AND capability."Key" = ANY(capability_keys)
             AND capability."AuthorizationTargetKind" = 'Property'
            WHERE session."Id" = expected_session_id
             AND session."UserId" = expected_user_id
             AND session."ActiveAccessContextId" = expected_access_context_id
             AND session."Status" = 'Active'
             AND session."RevokedAtUtc" IS NULL
             AND session."ExpiresAtUtc" > security_now_utc
          )
          SELECT DISTINCT property."Id" AS "Value"
          FROM public."Properties" property
          CROSS JOIN effective_assignments assignment
          WHERE property."PortfolioId" = target_portfolio_id
            AND property."DeletedAt" IS NULL
            AND (assignment."ScopeKind" = 'AllProperties'
                 OR (assignment."ScopeKind" = 'SelectedProperties'
                     AND EXISTS (
                       SELECT 1
                       FROM public."MembershipRoleAssignmentProperties" selected_property
                       WHERE selected_property."MembershipRoleAssignmentId" = assignment."Id"
                         AND selected_property."PortfolioId" = property."PortfolioId"
                         AND selected_property."PropertyId" = property."Id")));
        $function$;

        ALTER FUNCTION public.rc_api_authorized_property_ids_at_security_time(
          integer, uuid, integer, integer, bigint, text[], timestamp with time zone)
          OWNER TO rentalcommand_rls_authority;
        REVOKE ALL ON FUNCTION public.rc_api_authorized_property_ids_at_security_time(
          integer, uuid, integer, integer, bigint, text[], timestamp with time zone) FROM PUBLIC;
        REVOKE ALL ON FUNCTION public.rc_api_authorized_property_ids_at_security_time(
          integer, uuid, integer, integer, bigint, text[], timestamp with time zone)
          FROM rentalcommand_engine;
        GRANT EXECUTE ON FUNCTION public.rc_api_authorized_property_ids_at_security_time(
          integer, uuid, integer, integer, bigint, text[], timestamp with time zone)
          TO rentalcommand_api;
        """;

    internal const string Drop = """
        DROP FUNCTION IF EXISTS public.rc_api_authorized_property_ids_at_security_time(
          integer, uuid, integer, integer, bigint, text[], timestamp with time zone);
        """;
}
