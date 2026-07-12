using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Authorization;

namespace RentalCommand.Data.Authorization;

public sealed class AccessEnvelopeProjectionRow
{
    public int AccessContextId { get; set; }
    public int UserId { get; set; }
    public string EnvelopeJson { get; set; } = string.Empty;
}

/// <summary>
/// Reads a PostgreSQL-aggregated shell envelope. PostgreSQL performs every effective-state filter,
/// join, ordering, grouping, and JSON aggregation; .NET only deserializes the single result row.
/// Record authorization never consumes this display projection.
/// </summary>
public sealed class AccessEnvelopeQuery : IAccessEnvelopeQuery
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    static AccessEnvelopeQuery() => JsonOptions.Converters.Add(new JsonStringEnumConverter());

    private readonly RentalCommandDbContext _db;

    public AccessEnvelopeQuery(RentalCommandDbContext db) => _db = db;

    public async Task<AccessEnvelope?> GetAsync(
        int userId,
        int accessContextId,
        CancellationToken cancellationToken = default)
    {
        if (userId <= 0 || accessContextId <= 0)
        {
            return null;
        }

        var row = await _db.Set<AccessEnvelopeProjectionRow>()
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.AccessContextId == accessContextId &&
                        item.UserId == userId,
                cancellationToken);

        return row is null
            ? null
            : JsonSerializer.Deserialize<AccessEnvelope>(row.EnvelopeJson, JsonOptions)
              ?? throw new InvalidOperationException("The access envelope view returned invalid JSON.");
    }
}

internal static class AccessEnvelopeViewSql
{
    public const string Drop = "DROP VIEW IF EXISTS \"vw_access_envelopes\";";

    public const string Create =
        "CREATE VIEW \"vw_access_envelopes\" WITH (security_invoker = true) AS\n" + Definition;

    public const string Definition = """
        WITH effective_contexts AS (
            SELECT c."Id", c."UserId", c."PortfolioId", c."AccessRevision",
                   c."LastAuthorizedExperience", u."DisplayName", u."Email",
                   p."Name" AS "WorkspaceName", m."Id" AS "MembershipId",
                   m."DefaultExperience"
            FROM "WorkspaceAccessContexts" c
            JOIN "AspNetUsers" u ON u."Id" = c."UserId"
            JOIN "Portfolios" p ON p."Id" = c."PortfolioId" AND p."DeletedAt" IS NULL
            LEFT JOIN "WorkspaceMemberships" m
              ON m."AccessContextId" = c."Id" AND m."PortfolioId" = c."PortfolioId"
             AND m."Status" = 'Active' AND m."SuspendedAtUtc" IS NULL AND m."RevokedAtUtc" IS NULL
             AND m."EffectiveFromUtc" <= CURRENT_TIMESTAMP
             AND (m."EffectiveToUtc" IS NULL OR m."EffectiveToUtc" > CURRENT_TIMESTAMP)
            WHERE c."Status" = 'Active' AND c."SuspendedAtUtc" IS NULL AND c."RevokedAtUtc" IS NULL
              AND (m."Id" IS NOT NULL
                OR EXISTS (
                  SELECT 1 FROM "OwnerUserAccesses" oa
                  WHERE oa."AccessContextId" = c."Id" AND oa."ApplicationUserId" = c."UserId"
                    AND oa."PortfolioId" = c."PortfolioId" AND oa."RevokedAtUtc" IS NULL
                    AND oa."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                    AND (oa."EffectiveToUtc" IS NULL OR oa."EffectiveToUtc" > CURRENT_TIMESTAMP))
                OR EXISTS (
                  SELECT 1 FROM "TenantUserAccesses" ta
                  JOIN "LeaseManagementParties" lp
                    ON lp."Id" = ta."LeaseManagementPartyId" AND lp."PortfolioId" = ta."PortfolioId"
                  WHERE ta."AccessContextId" = c."Id" AND ta."ApplicationUserId" = c."UserId"
                    AND ta."PortfolioId" = c."PortfolioId" AND ta."RevokedAtUtc" IS NULL
                    AND lp."EffectiveFrom" <= rc_business_date(c."PortfolioId")
                    AND (lp."EffectiveThrough" IS NULL
                         OR lp."EffectiveThrough" >= rc_business_date(c."PortfolioId"))))
        ), effective_assignments AS (
            SELECT a."Id", a."WorkspaceMembershipId", a."PortfolioId", a."RoleProfileId",
                   a."Status", a."ScopeKind", r."Key" AS "RoleKey",
                   r."DisplayName" AS "RoleName", r."DefaultExperience"
            FROM "MembershipRoleAssignments" a
            JOIN "RoleProfiles" r ON r."Id" = a."RoleProfileId"
            WHERE a."Status" = 'Active' AND a."SuspendedAtUtc" IS NULL AND a."RevokedAtUtc" IS NULL
              AND a."EffectiveFromUtc" <= CURRENT_TIMESTAMP
              AND (a."EffectiveToUtc" IS NULL OR a."EffectiveToUtc" > CURRENT_TIMESTAMP)
        ), assignment_json AS (
            SELECT ec."Id" AS "AccessContextId",
                   jsonb_agg(
                     jsonb_build_object(
                       'assignmentId', a."Id", 'roleProfileKey', a."RoleKey",
                       'roleProfileName', a."RoleName", 'status', a."Status",
                       'scope', jsonb_build_object(
                         'kind', a."ScopeKind",
                         'selectedPropertyCount', COALESCE(props."Count", 0),
                         'selectedProperties', COALESCE(props."Properties", '[]'::jsonb)))
                     ORDER BY a."RoleName", a."Id") AS "Assignments"
            FROM effective_contexts ec
            JOIN effective_assignments a
              ON a."WorkspaceMembershipId" = ec."MembershipId" AND a."PortfolioId" = ec."PortfolioId"
            LEFT JOIN LATERAL (
              SELECT count(*)::int AS "Count",
                     jsonb_agg(jsonb_build_object('propertyId', p."Id", 'name', p."Name")
                               ORDER BY p."Name", p."Id") AS "Properties"
              FROM "MembershipRoleAssignmentProperties" ap
              JOIN "Properties" p
                ON p."Id" = ap."PropertyId" AND p."PortfolioId" = ap."PortfolioId"
               AND p."DeletedAt" IS NULL
              WHERE ap."MembershipRoleAssignmentId" = a."Id" AND ap."PortfolioId" = a."PortfolioId"
            ) props ON TRUE
            GROUP BY ec."Id"
        ), context_experiences AS (
            SELECT ec."Id" AS "AccessContextId", a."DefaultExperience" AS "Experience"
            FROM effective_contexts ec
            JOIN effective_assignments a
              ON a."WorkspaceMembershipId" = ec."MembershipId" AND a."PortfolioId" = ec."PortfolioId"
            UNION
            SELECT ec."Id", 'Owner'
            FROM effective_contexts ec
            WHERE EXISTS (
              SELECT 1 FROM "OwnerUserAccesses" oa
              WHERE oa."AccessContextId" = ec."Id" AND oa."ApplicationUserId" = ec."UserId"
                AND oa."PortfolioId" = ec."PortfolioId" AND oa."RevokedAtUtc" IS NULL
                AND oa."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                AND (oa."EffectiveToUtc" IS NULL OR oa."EffectiveToUtc" > CURRENT_TIMESTAMP))
            UNION
            SELECT ec."Id", 'Tenant'
            FROM effective_contexts ec
            WHERE EXISTS (
              SELECT 1 FROM "TenantUserAccesses" ta
              JOIN "LeaseManagementParties" lp
                ON lp."Id" = ta."LeaseManagementPartyId" AND lp."PortfolioId" = ta."PortfolioId"
              WHERE ta."AccessContextId" = ec."Id" AND ta."ApplicationUserId" = ec."UserId"
                AND ta."PortfolioId" = ec."PortfolioId" AND ta."RevokedAtUtc" IS NULL
                AND lp."EffectiveFrom" <= rc_business_date(ec."PortfolioId")
                AND (lp."EffectiveThrough" IS NULL
                     OR lp."EffectiveThrough" >= rc_business_date(ec."PortfolioId")))
        ), experience_json AS (
            SELECT ec."Id" AS "AccessContextId",
                   jsonb_agg(x."Experience" ORDER BY x."SortOrder") AS "Experiences"
            FROM effective_contexts ec
            JOIN LATERAL (
              SELECT ce."Experience",
                     CASE ce."Experience" WHEN 'Management' THEN 1 WHEN 'Leasing' THEN 2
                       WHEN 'Maintenance' THEN 3 WHEN 'Owner' THEN 4 ELSE 5 END AS "SortOrder"
              FROM context_experiences ce
              WHERE ce."AccessContextId" = ec."Id"
            ) x ON TRUE
            GROUP BY ec."Id"
        ), navigation_json AS (
            SELECT ec."Id" AS "AccessContextId",
                   jsonb_agg(jsonb_build_object(
                     'experience', nav."Experience", 'capabilityKeys', nav."Capabilities")
                     ORDER BY nav."SortOrder") AS "Navigation"
            FROM effective_contexts ec
            JOIN LATERAL (
              SELECT a."DefaultExperience" AS "Experience",
                     CASE a."DefaultExperience" WHEN 'Management' THEN 1 WHEN 'Leasing' THEN 2
                       WHEN 'Maintenance' THEN 3 WHEN 'Owner' THEN 4 ELSE 5 END AS "SortOrder",
                     jsonb_agg(DISTINCT cd."Key" ORDER BY cd."Key") AS "Capabilities"
              FROM effective_assignments a
              JOIN "RoleProfileCapabilities" rpc ON rpc."RoleProfileId" = a."RoleProfileId"
              JOIN "CapabilityDefinitions" cd ON cd."Id" = rpc."CapabilityDefinitionId"
              WHERE a."WorkspaceMembershipId" = ec."MembershipId" AND a."PortfolioId" = ec."PortfolioId"
              GROUP BY a."DefaultExperience"
              UNION ALL
              SELECT ce."Experience",
                     CASE ce."Experience" WHEN 'Owner' THEN 4 ELSE 5 END,
                     '[]'::jsonb
              FROM context_experiences ce
              WHERE ce."AccessContextId" = ec."Id" AND ce."Experience" IN ('Owner', 'Tenant')
            ) nav ON TRUE
            GROUP BY ec."Id"
        )
        SELECT ec."Id" AS "AccessContextId", ec."UserId" AS "UserId",
               jsonb_build_object(
                 'identity', jsonb_build_object(
                   'userId', ec."UserId", 'displayName', ec."DisplayName", 'email', ec."Email"),
                 'selectedContext', jsonb_build_object(
                   'accessContextId', ec."Id", 'portfolioId', ec."PortfolioId",
                   'workspaceName', ec."WorkspaceName", 'accessRevision', ec."AccessRevision",
                   'activeExperience', CASE
                     WHEN ex."Experiences" ? ec."LastAuthorizedExperience" THEN ec."LastAuthorizedExperience"
                     WHEN ex."Experiences" ? ec."DefaultExperience" THEN ec."DefaultExperience"
                     ELSE ex."Experiences"->>0 END),
                 'defaultExperience', CASE
                   WHEN ex."Experiences" ? ec."DefaultExperience" THEN ec."DefaultExperience"
                   ELSE ex."Experiences"->>0 END,
                 'availableExperiences', ex."Experiences",
                 'assignments', COALESCE(assignments."Assignments", '[]'::jsonb),
                 'navigation', navigation."Navigation")::text AS "EnvelopeJson"
        FROM effective_contexts ec
        LEFT JOIN assignment_json assignments ON assignments."AccessContextId" = ec."Id"
        JOIN experience_json ex ON ex."AccessContextId" = ec."Id"
        JOIN navigation_json navigation ON navigation."AccessContextId" = ec."Id";
        """;
}
