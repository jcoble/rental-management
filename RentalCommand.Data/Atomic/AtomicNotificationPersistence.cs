using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;

namespace RentalCommand.Data.Atomic;

internal sealed class AtomicNotificationPersistence : IAtomicNotificationPersistence
{
    private readonly RentalCommandDbContext _db;
    private readonly AtomicAuditScope _scope;

    public AtomicNotificationPersistence(RentalCommandDbContext db, AtomicAuditScope scope)
    {
        _db = db;
        _scope = scope;
    }

    public async Task<int> MarkAllReadAsync(
        int portfolioId,
        int userId,
        bool includeStaffOnlyNotifications,
        DateTime readAtUtc,
        CancellationToken ct = default)
    {
        using var lease = _scope.BeginInternalRawDml("Notifications", AtomicRawDmlOperation.Update);
        return await _db.Database.ExecuteSqlInterpolatedAsync($$"""
            UPDATE "Notifications"
            SET "IsRead" = TRUE,
                "ReadAt" = {{readAtUtc}}
            WHERE "PortfolioId" = {{portfolioId}}
              AND ("UserId" IS NULL OR "UserId" = {{userId}})
              AND "IsRead" = FALSE
              AND ({{includeStaffOnlyNotifications}} OR "Type" <> 'TenantMessage')
            """, ct);
    }

    public async Task<IReadOnlyList<AtomicMorningBriefingDigest>> ReadDueMorningBriefingsAsync(
        DateTime evaluationUtc,
        CancellationToken ct = default) =>
        await _db.Database.SqlQuery<AtomicMorningBriefingDigest>($"""
            WITH due_rules AS MATERIALIZED (
                SELECT rule."Id" AS rule_id, rule."PortfolioId" AS portfolio_id,
                       portfolio."Name" AS portfolio_name,
                       settings."MorningBriefingIncludeEmpty" AS include_empty,
                       COALESCE(NULLIF(portfolio."TimeZone", ''), 'America/New_York') AS time_zone,
                       to_char({evaluationUtc} AT TIME ZONE
                           COALESCE(NULLIF(portfolio."TimeZone", ''), 'America/New_York'), 'YYYY-MM-DD') AS local_date
                FROM "AutomationSettings" settings
                JOIN "Portfolios" portfolio ON portfolio."Id" = settings."PortfolioId"
                    AND portfolio."DeletedAt" IS NULL
                JOIN "TeamRoutingRules" rule ON rule."PortfolioId" = settings."PortfolioId"
                    AND rule."Topic" = 'MorningBriefing' AND rule."PropertyId" IS NULL
                WHERE settings."EnableMorningBriefing"
                  AND EXTRACT(hour FROM {evaluationUtc} AT TIME ZONE
                      COALESCE(NULLIF(portfolio."TimeZone", ''), 'America/New_York'))::integer
                      >= settings."MorningBriefingSendHourLocal"
            ), active_members AS MATERIALIZED (
                SELECT DISTINCT context."PortfolioId" AS portfolio_id, context."UserId" AS user_id,
                       assignment."Id" AS assignment_id, assignment."RoleProfileId" AS role_profile_id,
                       assignment."ScopeKind" AS scope_kind
                FROM "WorkspaceAccessContexts" context
                JOIN "WorkspaceMemberships" membership ON membership."AccessContextId" = context."Id"
                    AND membership."PortfolioId" = context."PortfolioId"
                JOIN "MembershipRoleAssignments" assignment
                    ON assignment."WorkspaceMembershipId" = membership."Id"
                    AND assignment."PortfolioId" = membership."PortfolioId"
                WHERE context."Status" = 'Active' AND context."SuspendedAtUtc" IS NULL
                  AND context."RevokedAtUtc" IS NULL AND membership."Status" = 'Active'
                  AND membership."SuspendedAtUtc" IS NULL AND membership."RevokedAtUtc" IS NULL
                  AND membership."EffectiveFromUtc" <= {evaluationUtc}
                  AND (membership."EffectiveToUtc" IS NULL OR membership."EffectiveToUtc" > {evaluationUtc})
                  AND assignment."Status" = 'Active' AND assignment."SuspendedAtUtc" IS NULL
                  AND assignment."RevokedAtUtc" IS NULL AND assignment."EffectiveFromUtc" <= {evaluationUtc}
                  AND (assignment."EffectiveToUtc" IS NULL OR assignment."EffectiveToUtc" > {evaluationUtc})
            ), eligible_properties AS MATERIALIZED (
                SELECT DISTINCT member.portfolio_id, member.user_id, property."Id" AS property_id,
                       capability."Key" AS capability_key
                FROM active_members member
                JOIN "RoleProfileCapabilities" profile_capability
                    ON profile_capability."RoleProfileId" = member.role_profile_id
                JOIN "CapabilityDefinitions" capability
                    ON capability."Id" = profile_capability."CapabilityDefinitionId"
                    AND capability."AuthorizationTargetKind" = 'Property'
                    AND capability."Key" IN ('rentals.read', 'work.read', 'money.balances.read',
                                               'leasing.showings.manage', 'leasing.onboarding.manage')
                JOIN "Properties" property ON property."PortfolioId" = member.portfolio_id
                    AND property."DeletedAt" IS NULL
                    AND (member.scope_kind = 'AllProperties' OR
                        (member.scope_kind = 'SelectedProperties' AND EXISTS (
                            SELECT 1 FROM "MembershipRoleAssignmentProperties" selected
                            WHERE selected."MembershipRoleAssignmentId" = member.assignment_id
                              AND selected."PortfolioId" = member.portfolio_id
                              AND selected."PropertyId" = property."Id")))
            ), explicit_recipients AS MATERIALIZED (
                SELECT DISTINCT due.rule_id, due.portfolio_id, due.portfolio_name,
                       due.include_empty, due.time_zone, due.local_date, recipient."UserId" AS user_id
                FROM due_rules due
                JOIN "TeamRoutingRuleRecipients" recipient ON recipient."TeamRoutingRuleId" = due.rule_id
                    AND recipient."PortfolioId" = due.portfolio_id
                JOIN active_members member ON member.portfolio_id = due.portfolio_id
                    AND member.user_id = recipient."UserId"
                WHERE EXISTS (SELECT 1 FROM eligible_properties eligible
                    WHERE eligible.portfolio_id = due.portfolio_id
                      AND eligible.user_id = recipient."UserId")
            ), routed_recipients AS MATERIALIZED (
                SELECT * FROM explicit_recipients
                UNION
                SELECT DISTINCT due.rule_id, due.portfolio_id, due.portfolio_name,
                       due.include_empty, due.time_zone, due.local_date, member.user_id
                FROM due_rules due
                JOIN "TeamRoutingRules" rule ON rule."Id" = due.rule_id
                JOIN active_members member ON member.portfolio_id = due.portfolio_id
                JOIN "RoleProfiles" profile ON profile."Id" = member.role_profile_id
                    AND profile."Key" = 'workspace-administrator'
                WHERE rule."UseWorkspaceAdministratorFallback"
                  AND NOT EXISTS (SELECT 1 FROM explicit_recipients explicit
                      WHERE explicit.rule_id = due.rule_id)
            ), digests AS MATERIALIZED (
                SELECT routed.portfolio_id, routed.portfolio_name, routed.user_id,
                       routed.local_date, routed.include_empty, user_row."Email" AS email,
                       user_row."PhoneNumber" AS phone_number,
                       COALESCE(preference."EnableEmail", TRUE) AS enable_email,
                       COALESCE(preference."EnableSms", FALSE) AS enable_sms,
                       COALESCE(preference."EnableMobilePush", TRUE) AS enable_push,
                       COALESCE(candidate_rows.item_count, 0)::integer AS item_count,
                       COALESCE(candidate_rows.items_json, '[]'::jsonb)::text AS items_json,
                       COALESCE(device_rows.tokens_json, '[]'::jsonb)::text AS device_tokens_json
                FROM routed_recipients routed
                JOIN "AspNetUsers" user_row ON user_row."Id" = routed.user_id
                LEFT JOIN "UserAlertPreferences" preference ON preference."PortfolioId" = routed.portfolio_id
                    AND preference."UserId" = routed.user_id
                LEFT JOIN LATERAL (
                    SELECT COUNT(*)::integer AS item_count,
                           jsonb_agg(jsonb_build_object(
                               'category', item."Category", 'severityOrder', item."EffectiveSeverityOrder",
                               'entityType', item."EntityType", 'entityId', item."EntityId", 'unitId', item."UnitId",
                               'titleText', item."TitleText", 'detailText', item."DetailText",
                               'leaseNumber', item."LeaseNumber", 'unitNumber', item."UnitNumber",
                               'tenantName', item."TenantName", 'propertyName', item."PropertyName",
                               'amount', item."Amount", 'eventDateTime', item."EventDateTime",
                               'eventDateOnly', item."EventDateOnly", 'typeValue', item."TypeValue")
                               ORDER BY item."EffectiveSeverityOrder", item."SortOrder",
                                        item."EventDateOnly", item."EventDateTime", item."EntityId") AS items_json
                    FROM (
                        SELECT candidate.*,
                               CASE WHEN candidate."Category" = 'RentLate'
                                        AND candidate."EventDateOnly" <= routed.local_date::date - 5 THEN 0
                                    WHEN candidate."Category" = 'Inspection'
                                        AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date
                                            < routed.local_date::date + 2 THEN 1
                                    ELSE candidate."SeverityOrder" END AS "EffectiveSeverityOrder"
                        FROM "vw_morning_briefing_candidates" candidate
                        JOIN eligible_properties eligible ON eligible.portfolio_id = candidate."PortfolioId"
                            AND eligible.user_id = routed.user_id
                            AND eligible.property_id = candidate."PropertyId"
                            AND eligible.capability_key = candidate."RequiredCapability"
                        WHERE candidate."PortfolioId" = routed.portfolio_id
                          AND (candidate."Category" = 'Maintenance'
                            OR (candidate."Category" = 'RentLate'
                                AND candidate."EventDateOnly" < routed.local_date::date)
                            OR (candidate."Category" = 'RentDue'
                                AND candidate."EventDateOnly" = routed.local_date::date)
                            OR (candidate."Category" = 'Appointment'
                                AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date
                                    = routed.local_date::date)
                            OR (candidate."Category" = 'Inspection'
                                AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date
                                    >= routed.local_date::date
                                AND (candidate."EventDateTime" AT TIME ZONE routed.time_zone)::date
                                    < routed.local_date::date + 8)
                            OR (candidate."Category" = 'LeaseExpiring'
                                AND candidate."EventDateOnly" > routed.local_date::date
                                AND candidate."EventDateOnly" <= routed.local_date::date + 60))
                        ORDER BY "EffectiveSeverityOrder", candidate."SortOrder",
                                 candidate."EventDateOnly", candidate."EventDateTime", candidate."EntityId"
                        LIMIT 25
                    ) item
                ) candidate_rows ON TRUE
                LEFT JOIN LATERAL (
                    SELECT jsonb_agg(DISTINCT token."Token") AS tokens_json
                    FROM "DeviceTokens" token
                    WHERE token."PortfolioId" = routed.portfolio_id AND token."UserId" = routed.user_id
                ) device_rows ON TRUE
            )
            SELECT portfolio_id AS "PortfolioId", portfolio_name AS "PortfolioName",
                   user_id AS "UserId", local_date AS "LocalDate", email AS "Email",
                   phone_number AS "PhoneNumber", enable_email AS "EnableEmail",
                   enable_sms AS "EnableSms", enable_push AS "EnablePush", item_count AS "ItemCount",
                   items_json AS "ItemsJson", device_tokens_json AS "DeviceTokensJson"
            FROM digests
            WHERE (include_empty OR item_count > 0)
              AND NOT EXISTS (
                  SELECT 1 FROM "OutboxMessages" sent
                  WHERE sent."IdempotencyKey" LIKE 'morning-briefing:' || portfolio_id::text || ':'
                      || user_id::text || ':' || local_date || ':%')
            ORDER BY portfolio_id, user_id
            """).ToListAsync(ct);
}
