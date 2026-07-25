using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

public partial class AddTypedNotificationNavigationIntent : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "NavigationAccessContextId",
            table: "Notifications",
            type: "integer",
            nullable: true);
        migrationBuilder.AddColumn<long>(
            name: "NavigationAccessRevision",
            table: "Notifications",
            type: "bigint",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationAction",
            table: "Notifications",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "NavigationChildResourceId",
            table: "Notifications",
            type: "integer",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationChildResourceKind",
            table: "Notifications",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationDestination",
            table: "Notifications",
            type: "character varying(40)",
            maxLength: 40,
            nullable: true);
        migrationBuilder.AddColumn<DateTime>(
            name: "NavigationExpiresAtUtc",
            table: "Notifications",
            type: "timestamp with time zone",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationExperience",
            table: "Notifications",
            type: "character varying(20)",
            maxLength: 20,
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationFallbackDestination",
            table: "Notifications",
            type: "character varying(40)",
            maxLength: 40,
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "NavigationParentResourceId",
            table: "Notifications",
            type: "integer",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationParentResourceKind",
            table: "Notifications",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);
        migrationBuilder.AddColumn<int>(
            name: "NavigationResourceId",
            table: "Notifications",
            type: "integer",
            nullable: true);
        migrationBuilder.AddColumn<string>(
            name: "NavigationResourceKind",
            table: "Notifications",
            type: "character varying(120)",
            maxLength: 120,
            nullable: true);

        // Backfill only exact historical routes whose user, access context, experience,
        // resource identity, and workspace ownership can all be proven in PostgreSQL.
        // Every other legacy value intentionally remains a null/safe-home intent.
        migrationBuilder.Sql("""
            WITH context_candidates AS (
                SELECT notification."Id" AS notification_id,
                       context."Id" AS access_context_id,
                       context."AccessRevision" AS access_revision,
                       context."UserId" AS user_id,
                       membership."Id" AS membership_id,
                       COALESCE(
                           context."LastAuthorizedExperience",
                           membership."DefaultExperience",
                           CASE
                               WHEN EXISTS (
                                   SELECT 1
                                   FROM "TenantUserAccesses" tenant_access
                                   WHERE tenant_access."AccessContextId" = context."Id"
                                     AND tenant_access."PortfolioId" = context."PortfolioId"
                                     AND tenant_access."ApplicationUserId" = context."UserId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                               )
                               AND NOT EXISTS (
                                   SELECT 1
                                   FROM "OwnerUserAccesses" owner_access
                                   WHERE owner_access."AccessContextId" = context."Id"
                                     AND owner_access."PortfolioId" = context."PortfolioId"
                                     AND owner_access."ApplicationUserId" = context."UserId"
                                     AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (owner_access."EffectiveToUtc" IS NULL
                                          OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                     AND owner_access."RevokedAtUtc" IS NULL
                               ) THEN 'Tenant'
                               WHEN EXISTS (
                                   SELECT 1
                                   FROM "OwnerUserAccesses" owner_access
                                   WHERE owner_access."AccessContextId" = context."Id"
                                     AND owner_access."PortfolioId" = context."PortfolioId"
                                     AND owner_access."ApplicationUserId" = context."UserId"
                                     AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (owner_access."EffectiveToUtc" IS NULL
                                          OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                     AND owner_access."RevokedAtUtc" IS NULL
                               )
                               AND NOT EXISTS (
                                   SELECT 1
                                   FROM "TenantUserAccesses" tenant_access
                                   WHERE tenant_access."AccessContextId" = context."Id"
                                     AND tenant_access."PortfolioId" = context."PortfolioId"
                                     AND tenant_access."ApplicationUserId" = context."UserId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                               ) THEN 'Owner'
                           END
                       ) AS experience,
                       ROW_NUMBER() OVER (
                           PARTITION BY notification."Id"
                           ORDER BY context."UpdatedAtUtc" DESC, context."Id" DESC
                       ) AS candidate_order,
                       COUNT(*) OVER (PARTITION BY notification."Id") AS candidate_count
                FROM "Notifications" notification
                JOIN "WorkspaceAccessContexts" context
                  ON context."PortfolioId" = notification."PortfolioId"
                 AND context."UserId" = notification."UserId"
                 AND context."Status" = 'Active'
                 AND context."SuspendedAtUtc" IS NULL
                 AND context."RevokedAtUtc" IS NULL
                LEFT JOIN "WorkspaceMemberships" membership
                  ON membership."AccessContextId" = context."Id"
                 AND membership."PortfolioId" = context."PortfolioId"
                 AND membership."Status" = 'Active'
                 AND membership."SuspendedAtUtc" IS NULL
                 AND membership."RevokedAtUtc" IS NULL
                 AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                 AND (membership."EffectiveToUtc" IS NULL
                      OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                WHERE notification."UserId" IS NOT NULL
            ),
            authorized_candidates AS (
                SELECT candidate.*,
                       CASE
                           WHEN candidate.experience IN ('Management', 'Leasing', 'Maintenance')
                               THEN EXISTS (
                                   SELECT 1
                                   FROM "MembershipRoleAssignments" assignment
                                   JOIN "RoleProfiles" role
                                     ON role."Id" = assignment."RoleProfileId"
                                    AND role."DefaultExperience" = candidate.experience
                                   WHERE assignment."WorkspaceMembershipId" = candidate.membership_id
                                     AND assignment."PortfolioId" = notification."PortfolioId"
                                     AND assignment."Status" = 'Active'
                                     AND assignment."SuspendedAtUtc" IS NULL
                                     AND assignment."RevokedAtUtc" IS NULL
                                     AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (assignment."EffectiveToUtc" IS NULL
                                          OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                               )
                           WHEN candidate.experience = 'Owner'
                               THEN EXISTS (
                                   SELECT 1
                                   FROM "OwnerUserAccesses" owner_access
                                   JOIN "OwnerEntities" owner_entity
                                     ON owner_entity."Id" = owner_access."OwnerEntityId"
                                    AND owner_entity."PortfolioId" = owner_access."PortfolioId"
                                    AND owner_entity."DeletedAt" IS NULL
                                   WHERE owner_access."AccessContextId" = candidate.access_context_id
                                     AND owner_access."ApplicationUserId" = candidate.user_id
                                     AND owner_access."PortfolioId" = notification."PortfolioId"
                                     AND owner_access."RevokedAtUtc" IS NULL
                                     AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (owner_access."EffectiveToUtc" IS NULL
                                          OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
                               )
                           WHEN candidate.experience = 'Tenant'
                               THEN EXISTS (
                                   SELECT 1
                                   FROM "TenantUserAccesses" tenant_access
                                   JOIN "LeaseManagementParties" party
                                     ON party."Id" = tenant_access."LeaseManagementPartyId"
                                    AND party."PortfolioId" = tenant_access."PortfolioId"
                                   WHERE tenant_access."AccessContextId" = candidate.access_context_id
                                     AND tenant_access."ApplicationUserId" = candidate.user_id
                                     AND tenant_access."PortfolioId" = notification."PortfolioId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                                     AND party."EffectiveFrom" <= rc_business_date(notification."PortfolioId")
                                     AND (party."EffectiveThrough" IS NULL
                                          OR party."EffectiveThrough" >= rc_business_date(notification."PortfolioId"))
                               )
                           ELSE FALSE
                       END AS experience_authorized
                FROM context_candidates candidate
                JOIN "Notifications" notification ON notification."Id" = candidate.notification_id
                WHERE candidate.candidate_order = 1
                  AND candidate.candidate_count = 1
            ),
            routed AS (
                SELECT notification."Id" AS notification_id,
                       context.access_context_id,
                       context.access_revision,
                       context.experience,
                       CASE
                           WHEN notification."ActionUrl" = (
                                    '/portal/messages?conversation=' || notification."RelatedEntityId"::text)
                                AND notification."RelatedEntityType" = 'Conversation'
                                AND context.experience = 'Tenant'
                                AND EXISTS (
                                    SELECT 1
                                    FROM "Conversations" resource
                                    JOIN "TenantUserAccesses" tenant_access
                                     ON tenant_access."AccessContextId" = context.access_context_id
                                     AND tenant_access."ApplicationUserId" = context.user_id
                                     AND tenant_access."PortfolioId" = resource."PortfolioId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                                    JOIN "LeaseManagementParties" party
                                      ON party."Id" = tenant_access."LeaseManagementPartyId"
                                     AND party."PortfolioId" = tenant_access."PortfolioId"
                                     AND party."TenantId" = resource."TenantId"
                                     AND party."EffectiveFrom" <= rc_business_date(resource."PortfolioId")
                                     AND (party."EffectiveThrough" IS NULL
                                          OR party."EffectiveThrough" >= rc_business_date(resource."PortfolioId"))
                                    WHERE resource."Id" = notification."RelatedEntityId"
                                      AND resource."PortfolioId" = notification."PortfolioId"
                                ) THEN 'Message'
                           WHEN notification."ActionUrl" = (
                                    '/messages/' || notification."RelatedEntityId"::text)
                                AND notification."RelatedEntityType" = 'Conversation'
                                AND context.experience IN ('Management', 'Leasing', 'Tenant')
                                AND (
                                    (context.experience IN ('Management', 'Leasing')
                                     AND EXISTS (
                                         SELECT 1
                                         FROM "Conversations" resource
                                         JOIN "MembershipRoleAssignments" assignment
                                           ON assignment."WorkspaceMembershipId" = context.membership_id
                                          AND assignment."PortfolioId" = resource."PortfolioId"
                                          AND assignment."Status" = 'Active'
                                          AND assignment."SuspendedAtUtc" IS NULL
                                          AND assignment."RevokedAtUtc" IS NULL
                                          AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                          AND (assignment."EffectiveToUtc" IS NULL
                                               OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                         JOIN "RoleProfiles" role
                                           ON role."Id" = assignment."RoleProfileId"
                                          AND role."DefaultExperience" = context.experience
                                         WHERE resource."Id" = notification."RelatedEntityId"
                                           AND resource."PortfolioId" = notification."PortfolioId"
                                           AND (
                                               assignment."ScopeKind" = 'AllProperties'
                                               OR (
                                                   resource."PropertyId" IS NOT NULL
                                                   AND assignment."ScopeKind" = 'SelectedProperties'
                                                   AND EXISTS (
                                                       SELECT 1
                                                       FROM "MembershipRoleAssignmentProperties" selected
                                                       WHERE selected."MembershipRoleAssignmentId" = assignment."Id"
                                                         AND selected."PortfolioId" = resource."PortfolioId"
                                                         AND selected."PropertyId" = resource."PropertyId"
                                                   )
                                               )
                                           )
                                     ))
                                    OR
                                    (context.experience = 'Tenant'
                                     AND EXISTS (
                                         SELECT 1
                                         FROM "Conversations" resource
                                         JOIN "TenantUserAccesses" tenant_access
                                           ON tenant_access."AccessContextId" = context.access_context_id
                                          AND tenant_access."ApplicationUserId" = context.user_id
                                          AND tenant_access."PortfolioId" = resource."PortfolioId"
                                          AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                          AND tenant_access."RevokedAtUtc" IS NULL
                                         JOIN "LeaseManagementParties" party
                                           ON party."Id" = tenant_access."LeaseManagementPartyId"
                                          AND party."PortfolioId" = tenant_access."PortfolioId"
                                          AND party."TenantId" = resource."TenantId"
                                          AND party."EffectiveFrom" <= rc_business_date(resource."PortfolioId")
                                          AND (party."EffectiveThrough" IS NULL
                                               OR party."EffectiveThrough" >= rc_business_date(resource."PortfolioId"))
                                         WHERE resource."Id" = notification."RelatedEntityId"
                                           AND resource."PortfolioId" = notification."PortfolioId"
                                     ))
                                ) THEN 'Message'
                           WHEN notification."ActionUrl" = (
                                    '/maintenance/' || notification."RelatedEntityId"::text)
                                AND notification."RelatedEntityType" = 'WorkOrder'
                                AND context.experience IN ('Management', 'Maintenance')
                                AND EXISTS (
                                    SELECT 1
                                    FROM "WorkOrders" resource
                                    JOIN "MembershipRoleAssignments" assignment
                                      ON assignment."WorkspaceMembershipId" = context.membership_id
                                     AND assignment."PortfolioId" = resource."PortfolioId"
                                     AND assignment."Status" = 'Active'
                                     AND assignment."SuspendedAtUtc" IS NULL
                                     AND assignment."RevokedAtUtc" IS NULL
                                     AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (assignment."EffectiveToUtc" IS NULL
                                          OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                    JOIN "RoleProfiles" role
                                      ON role."Id" = assignment."RoleProfileId"
                                     AND role."DefaultExperience" = context.experience
                                    WHERE resource."Id" = notification."RelatedEntityId"
                                      AND resource."PortfolioId" = notification."PortfolioId"
                                      AND (
                                          (context.experience = 'Management'
                                           AND (
                                               assignment."ScopeKind" = 'AllProperties'
                                               OR (
                                                   assignment."ScopeKind" = 'SelectedProperties'
                                                   AND EXISTS (
                                                       SELECT 1
                                                       FROM "MembershipRoleAssignmentProperties" selected
                                                       WHERE selected."MembershipRoleAssignmentId" = assignment."Id"
                                                         AND selected."PortfolioId" = resource."PortfolioId"
                                                         AND selected."PropertyId" = resource."PropertyId"
                                                   )
                                               )
                                           ))
                                          OR
                                          (context.experience = 'Maintenance'
                                           AND assignment."ScopeKind" = 'AssignedWorkOrders'
                                           AND EXISTS (
                                               SELECT 1
                                               FROM "WorkOrderResponsibilities" responsibility
                                               WHERE responsibility."PortfolioId" = resource."PortfolioId"
                                                 AND responsibility."WorkOrderId" = resource."Id"
                                                 AND responsibility."WorkspaceMembershipId" =
                                                     assignment."WorkspaceMembershipId"
                                                 AND responsibility."MembershipRoleAssignmentId" = assignment."Id"
                                                 AND responsibility."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                                 AND (responsibility."EffectiveToUtc" IS NULL
                                                      OR responsibility."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                           ))
                                      )
                                ) THEN CASE WHEN context.experience = 'Maintenance'
                                           THEN 'TechnicianWork' ELSE 'WorkOrder' END
                           WHEN notification."ActionUrl" = (
                                    '/owners/' || notification."RelatedEntityId"::text)
                                AND notification."RelatedEntityType" = 'OwnerEntity'
                                AND context.experience = 'Management'
                                AND EXISTS (
                                    SELECT 1
                                    FROM "OwnerEntities" resource
                                    JOIN "MembershipRoleAssignments" assignment
                                      ON assignment."WorkspaceMembershipId" = context.membership_id
                                     AND assignment."PortfolioId" = resource."PortfolioId"
                                     AND assignment."ScopeKind" = 'AllProperties'
                                     AND assignment."Status" = 'Active'
                                     AND assignment."SuspendedAtUtc" IS NULL
                                     AND assignment."RevokedAtUtc" IS NULL
                                     AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (assignment."EffectiveToUtc" IS NULL
                                          OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                    JOIN "RoleProfiles" role
                                      ON role."Id" = assignment."RoleProfileId"
                                     AND role."DefaultExperience" = 'Management'
                                    WHERE resource."Id" = notification."RelatedEntityId"
                                      AND resource."PortfolioId" = notification."PortfolioId"
                                      AND resource."DeletedAt" IS NULL
                                ) THEN 'Owners'
                           WHEN notification."ActionUrl" IN ('/banking', '/settings/accounting')
                                AND context.experience_authorized
                                AND context.experience = 'Management' THEN 'Money'
                           WHEN notification."ActionUrl" = '/today'
                                AND context.experience_authorized
                                AND context.experience IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')
                                THEN 'Home'
                           WHEN notification."ActionUrl" = '/notices'
                                AND context.experience_authorized
                                AND context.experience IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')
                                THEN 'Notifications'
                       END AS destination
                FROM "Notifications" notification
                JOIN authorized_candidates context
                  ON context.notification_id = notification."Id"
            ),
            classified AS (
                SELECT routed.*,
                       CASE
                           WHEN routed.destination IN ('Message', 'WorkOrder', 'TechnicianWork', 'Owners')
                               THEN notification."RelatedEntityType"
                       END AS resource_kind,
                       CASE
                           WHEN routed.destination IN ('Message', 'WorkOrder', 'TechnicianWork', 'Owners')
                               THEN notification."RelatedEntityId"
                       END AS resource_id
                FROM routed
                JOIN "Notifications" notification ON notification."Id" = routed.notification_id
            )
            UPDATE "Notifications" notification
               SET "NavigationExperience" = classified.experience,
                   "NavigationDestination" = classified.destination,
                   "NavigationAccessContextId" = classified.access_context_id,
                   "NavigationAccessRevision" = classified.access_revision,
                   "NavigationResourceKind" = classified.resource_kind,
                   "NavigationResourceId" = classified.resource_id,
                   "NavigationAction" = 'Open',
                   "NavigationExpiresAtUtc" = notification."CreatedAt" + INTERVAL '7 days',
                   "NavigationFallbackDestination" = 'Home'
              FROM classified
             WHERE classified.notification_id = notification."Id"
               AND classified.destination IS NOT NULL;
            """);

        // Durable push rows are part of the delivery contract. Convert recognized, access-bound
        // payloads and strip every legacy raw-routing key from all queued push work. Unknown or
        // unbound rows remain deliverable but intentionally open only the client's safe home.
        migrationBuilder.Sql("""
            WITH push_candidates AS (
                SELECT outbox."Id" AS outbox_id,
                       context."Id" AS access_context_id,
                       context."AccessRevision" AS access_revision,
                       context."UserId" AS user_id,
                       membership."Id" AS membership_id,
                       COALESCE(
                           context."LastAuthorizedExperience",
                           membership."DefaultExperience",
                           CASE
                               WHEN EXISTS (
                                   SELECT 1 FROM "TenantUserAccesses" tenant_access
                                   WHERE tenant_access."AccessContextId" = context."Id"
                                     AND tenant_access."PortfolioId" = context."PortfolioId"
                                     AND tenant_access."ApplicationUserId" = context."UserId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                               )
                               AND NOT EXISTS (
                                   SELECT 1 FROM "OwnerUserAccesses" owner_access
                                   WHERE owner_access."AccessContextId" = context."Id"
                                     AND owner_access."PortfolioId" = context."PortfolioId"
                                     AND owner_access."ApplicationUserId" = context."UserId"
                                     AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (owner_access."EffectiveToUtc" IS NULL
                                          OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                     AND owner_access."RevokedAtUtc" IS NULL
                               ) THEN 'Tenant'
                               WHEN EXISTS (
                                   SELECT 1 FROM "OwnerUserAccesses" owner_access
                                   WHERE owner_access."AccessContextId" = context."Id"
                                     AND owner_access."PortfolioId" = context."PortfolioId"
                                     AND owner_access."ApplicationUserId" = context."UserId"
                                     AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (owner_access."EffectiveToUtc" IS NULL
                                          OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                     AND owner_access."RevokedAtUtc" IS NULL
                               )
                               AND NOT EXISTS (
                                   SELECT 1 FROM "TenantUserAccesses" tenant_access
                                   WHERE tenant_access."AccessContextId" = context."Id"
                                     AND tenant_access."PortfolioId" = context."PortfolioId"
                                     AND tenant_access."ApplicationUserId" = context."UserId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                               ) THEN 'Owner'
                           END
                       ) AS experience,
                       ROW_NUMBER() OVER (
                           PARTITION BY outbox."Id"
                           ORDER BY context."UpdatedAtUtc" DESC, context."Id" DESC
                       ) AS candidate_order,
                       COUNT(*) OVER (PARTITION BY outbox."Id") AS candidate_count
                FROM "OutboxMessages" outbox
                JOIN "DeviceTokens" device
                  ON device."PortfolioId" = outbox."PortfolioId"
                 AND device."Token" = (outbox."Payload"::jsonb ->> 'deviceToken')
                JOIN "WorkspaceAccessContexts" context
                  ON context."PortfolioId" = device."PortfolioId"
                 AND context."UserId" = device."UserId"
                 AND context."Status" = 'Active'
                 AND context."SuspendedAtUtc" IS NULL
                 AND context."RevokedAtUtc" IS NULL
                LEFT JOIN "WorkspaceMemberships" membership
                  ON membership."AccessContextId" = context."Id"
                 AND membership."PortfolioId" = context."PortfolioId"
                 AND membership."Status" = 'Active'
                 AND membership."SuspendedAtUtc" IS NULL
                 AND membership."RevokedAtUtc" IS NULL
                 AND membership."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                 AND (membership."EffectiveToUtc" IS NULL
                      OR membership."EffectiveToUtc" > CURRENT_TIMESTAMP)
                WHERE lower(outbox."MessageType") = 'push'
            ),
            authorized_candidates AS (
                SELECT candidate.*,
                       CASE
                           WHEN candidate.experience IN ('Management', 'Leasing', 'Maintenance')
                               THEN EXISTS (
                                   SELECT 1
                                   FROM "MembershipRoleAssignments" assignment
                                   JOIN "RoleProfiles" role
                                     ON role."Id" = assignment."RoleProfileId"
                                    AND role."DefaultExperience" = candidate.experience
                                   WHERE assignment."WorkspaceMembershipId" = candidate.membership_id
                                     AND assignment."PortfolioId" = outbox."PortfolioId"
                                     AND assignment."Status" = 'Active'
                                     AND assignment."SuspendedAtUtc" IS NULL
                                     AND assignment."RevokedAtUtc" IS NULL
                                     AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (assignment."EffectiveToUtc" IS NULL
                                          OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                               )
                           WHEN candidate.experience = 'Owner'
                               THEN EXISTS (
                                   SELECT 1
                                   FROM "OwnerUserAccesses" owner_access
                                   JOIN "OwnerEntities" owner_entity
                                     ON owner_entity."Id" = owner_access."OwnerEntityId"
                                    AND owner_entity."PortfolioId" = owner_access."PortfolioId"
                                    AND owner_entity."DeletedAt" IS NULL
                                   WHERE owner_access."AccessContextId" = candidate.access_context_id
                                     AND owner_access."ApplicationUserId" = candidate.user_id
                                     AND owner_access."PortfolioId" = outbox."PortfolioId"
                                     AND owner_access."RevokedAtUtc" IS NULL
                                     AND owner_access."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (owner_access."EffectiveToUtc" IS NULL
                                          OR owner_access."EffectiveToUtc" > CURRENT_TIMESTAMP)
                               )
                           WHEN candidate.experience = 'Tenant'
                               THEN EXISTS (
                                   SELECT 1
                                   FROM "TenantUserAccesses" tenant_access
                                   JOIN "LeaseManagementParties" party
                                     ON party."Id" = tenant_access."LeaseManagementPartyId"
                                    AND party."PortfolioId" = tenant_access."PortfolioId"
                                   WHERE tenant_access."AccessContextId" = candidate.access_context_id
                                     AND tenant_access."ApplicationUserId" = candidate.user_id
                                     AND tenant_access."PortfolioId" = outbox."PortfolioId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                                     AND party."EffectiveFrom" <= rc_business_date(outbox."PortfolioId")
                                     AND (party."EffectiveThrough" IS NULL
                                          OR party."EffectiveThrough" >= rc_business_date(outbox."PortfolioId"))
                               )
                           ELSE FALSE
                       END AS experience_authorized
                FROM push_candidates candidate
                JOIN "OutboxMessages" outbox ON outbox."Id" = candidate.outbox_id
                WHERE candidate.candidate_order = 1
                  AND candidate.candidate_count = 1
            ),
            parsed_pushes AS (
                SELECT outbox."Id" AS outbox_id,
                       outbox."Payload"::jsonb ->> 'actionUrl' AS action_url,
                       outbox."Payload"::jsonb ->> 'relatedEntityType' AS resource_kind,
                       CASE
                           WHEN outbox."Payload"::jsonb ->> 'relatedEntityId' ~ '^[1-9][0-9]*$'
                                AND length(outbox."Payload"::jsonb ->> 'relatedEntityId') <= 10
                                AND (
                                    length(outbox."Payload"::jsonb ->> 'relatedEntityId') < 10
                                    OR outbox."Payload"::jsonb ->> 'relatedEntityId' <= '2147483647'
                                )
                               THEN (outbox."Payload"::jsonb ->> 'relatedEntityId')::integer
                       END AS resource_id
                FROM "OutboxMessages" outbox
                WHERE lower(outbox."MessageType") = 'push'
            ),
            routed AS (
                SELECT outbox."Id" AS outbox_id,
                       candidate.access_context_id,
                       candidate.access_revision,
                       candidate.experience,
                       parsed.resource_kind,
                       parsed.resource_id,
                       CASE
                           WHEN parsed.action_url = '/notices'
                                AND candidate.experience_authorized
                                AND candidate.experience IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')
                               THEN 'Notifications'
                           WHEN parsed.action_url = '/today'
                                AND candidate.experience_authorized
                                AND candidate.experience IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')
                               THEN 'Home'
                           WHEN parsed.action_url IN ('/banking', '/settings/accounting')
                                AND candidate.experience_authorized
                                AND candidate.experience = 'Management' THEN 'Money'
                           WHEN parsed.resource_kind = 'Conversation'
                                AND parsed.resource_id IS NOT NULL
                                AND parsed.action_url = '/messages/' || parsed.resource_id::text
                                AND candidate.experience IN ('Management', 'Leasing', 'Tenant')
                                AND (
                                    (candidate.experience IN ('Management', 'Leasing')
                                     AND EXISTS (
                                         SELECT 1
                                         FROM "Conversations" resource
                                         JOIN "MembershipRoleAssignments" assignment
                                           ON assignment."WorkspaceMembershipId" = candidate.membership_id
                                          AND assignment."PortfolioId" = resource."PortfolioId"
                                          AND assignment."Status" = 'Active'
                                          AND assignment."SuspendedAtUtc" IS NULL
                                          AND assignment."RevokedAtUtc" IS NULL
                                          AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                          AND (assignment."EffectiveToUtc" IS NULL
                                               OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                         JOIN "RoleProfiles" role
                                           ON role."Id" = assignment."RoleProfileId"
                                          AND role."DefaultExperience" = candidate.experience
                                         WHERE resource."Id" = parsed.resource_id
                                           AND resource."PortfolioId" = outbox."PortfolioId"
                                           AND (
                                               assignment."ScopeKind" = 'AllProperties'
                                               OR (
                                                   resource."PropertyId" IS NOT NULL
                                                   AND assignment."ScopeKind" = 'SelectedProperties'
                                                   AND EXISTS (
                                                       SELECT 1
                                                       FROM "MembershipRoleAssignmentProperties" selected
                                                       WHERE selected."MembershipRoleAssignmentId" = assignment."Id"
                                                         AND selected."PortfolioId" = resource."PortfolioId"
                                                         AND selected."PropertyId" = resource."PropertyId"
                                                   )
                                               )
                                           )
                                     ))
                                    OR
                                    (candidate.experience = 'Tenant'
                                     AND EXISTS (
                                         SELECT 1
                                         FROM "Conversations" resource
                                         JOIN "TenantUserAccesses" tenant_access
                                          ON tenant_access."AccessContextId" = candidate.access_context_id
                                          AND tenant_access."ApplicationUserId" = candidate.user_id
                                          AND tenant_access."PortfolioId" = resource."PortfolioId"
                                          AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                          AND tenant_access."RevokedAtUtc" IS NULL
                                         JOIN "LeaseManagementParties" party
                                           ON party."Id" = tenant_access."LeaseManagementPartyId"
                                          AND party."PortfolioId" = tenant_access."PortfolioId"
                                          AND party."TenantId" = resource."TenantId"
                                          AND party."EffectiveFrom" <= rc_business_date(resource."PortfolioId")
                                          AND (party."EffectiveThrough" IS NULL
                                               OR party."EffectiveThrough" >= rc_business_date(resource."PortfolioId"))
                                         WHERE resource."Id" = parsed.resource_id
                                           AND resource."PortfolioId" = outbox."PortfolioId"
                                     ))
                                )
                               THEN 'Message'
                           WHEN parsed.resource_kind = 'Conversation'
                                AND parsed.resource_id IS NOT NULL
                                AND parsed.action_url =
                                    '/portal/messages?conversation=' || parsed.resource_id::text
                                AND candidate.experience = 'Tenant'
                                AND EXISTS (
                                    SELECT 1
                                    FROM "Conversations" resource
                                    JOIN "TenantUserAccesses" tenant_access
                                      ON tenant_access."AccessContextId" = candidate.access_context_id
                                     AND tenant_access."ApplicationUserId" = candidate.user_id
                                     AND tenant_access."PortfolioId" = resource."PortfolioId"
                                     AND tenant_access."GrantedAtUtc" <= CURRENT_TIMESTAMP
                                     AND tenant_access."RevokedAtUtc" IS NULL
                                    JOIN "LeaseManagementParties" party
                                      ON party."Id" = tenant_access."LeaseManagementPartyId"
                                     AND party."PortfolioId" = tenant_access."PortfolioId"
                                     AND party."TenantId" = resource."TenantId"
                                     AND party."EffectiveFrom" <= rc_business_date(resource."PortfolioId")
                                     AND (party."EffectiveThrough" IS NULL
                                          OR party."EffectiveThrough" >= rc_business_date(resource."PortfolioId"))
                                    WHERE resource."Id" = parsed.resource_id
                                      AND resource."PortfolioId" = outbox."PortfolioId"
                                ) THEN 'Message'
                           WHEN parsed.resource_kind = 'WorkOrder'
                                AND parsed.resource_id IS NOT NULL
                                AND parsed.action_url = '/maintenance/' || parsed.resource_id::text
                                AND candidate.experience IN ('Management', 'Maintenance')
                                AND EXISTS (
                                    SELECT 1
                                    FROM "WorkOrders" resource
                                    JOIN "MembershipRoleAssignments" assignment
                                      ON assignment."WorkspaceMembershipId" = candidate.membership_id
                                     AND assignment."PortfolioId" = resource."PortfolioId"
                                     AND assignment."Status" = 'Active'
                                     AND assignment."SuspendedAtUtc" IS NULL
                                     AND assignment."RevokedAtUtc" IS NULL
                                     AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (assignment."EffectiveToUtc" IS NULL
                                          OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                    JOIN "RoleProfiles" role
                                      ON role."Id" = assignment."RoleProfileId"
                                     AND role."DefaultExperience" = candidate.experience
                                    WHERE resource."Id" = parsed.resource_id
                                      AND resource."PortfolioId" = outbox."PortfolioId"
                                      AND (
                                          (candidate.experience = 'Management'
                                           AND (
                                               assignment."ScopeKind" = 'AllProperties'
                                               OR (
                                                   assignment."ScopeKind" = 'SelectedProperties'
                                                   AND EXISTS (
                                                       SELECT 1
                                                       FROM "MembershipRoleAssignmentProperties" selected
                                                       WHERE selected."MembershipRoleAssignmentId" = assignment."Id"
                                                         AND selected."PortfolioId" = resource."PortfolioId"
                                                         AND selected."PropertyId" = resource."PropertyId"
                                                   )
                                               )
                                           ))
                                          OR
                                          (candidate.experience = 'Maintenance'
                                           AND assignment."ScopeKind" = 'AssignedWorkOrders'
                                           AND EXISTS (
                                               SELECT 1
                                               FROM "WorkOrderResponsibilities" responsibility
                                               WHERE responsibility."PortfolioId" = resource."PortfolioId"
                                                 AND responsibility."WorkOrderId" = resource."Id"
                                                 AND responsibility."WorkspaceMembershipId" =
                                                     assignment."WorkspaceMembershipId"
                                                 AND responsibility."MembershipRoleAssignmentId" = assignment."Id"
                                                 AND responsibility."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                                 AND (responsibility."EffectiveToUtc" IS NULL
                                                      OR responsibility."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                           ))
                                      )
                                )
                               THEN CASE WHEN candidate.experience = 'Maintenance'
                                         THEN 'TechnicianWork' ELSE 'WorkOrder' END
                           WHEN parsed.resource_kind = 'OwnerEntity'
                                AND parsed.resource_id IS NOT NULL
                                AND parsed.action_url = '/owners/' || parsed.resource_id::text
                                AND candidate.experience = 'Management'
                                AND EXISTS (
                                    SELECT 1
                                    FROM "OwnerEntities" resource
                                    JOIN "MembershipRoleAssignments" assignment
                                      ON assignment."WorkspaceMembershipId" = candidate.membership_id
                                     AND assignment."PortfolioId" = resource."PortfolioId"
                                     AND assignment."ScopeKind" = 'AllProperties'
                                     AND assignment."Status" = 'Active'
                                     AND assignment."SuspendedAtUtc" IS NULL
                                     AND assignment."RevokedAtUtc" IS NULL
                                     AND assignment."EffectiveFromUtc" <= CURRENT_TIMESTAMP
                                     AND (assignment."EffectiveToUtc" IS NULL
                                          OR assignment."EffectiveToUtc" > CURRENT_TIMESTAMP)
                                    JOIN "RoleProfiles" role
                                      ON role."Id" = assignment."RoleProfileId"
                                     AND role."DefaultExperience" = 'Management'
                                    WHERE resource."Id" = parsed.resource_id
                                      AND resource."PortfolioId" = outbox."PortfolioId"
                                      AND resource."DeletedAt" IS NULL
                                ) THEN 'Owners'
                       END AS destination
                FROM "OutboxMessages" outbox
                JOIN parsed_pushes parsed ON parsed.outbox_id = outbox."Id"
                LEFT JOIN authorized_candidates candidate
                  ON candidate.outbox_id = outbox."Id"
                WHERE lower(outbox."MessageType") = 'push'
            )
            UPDATE "OutboxMessages" outbox
               SET "Payload" = (
                   outbox."Payload"::jsonb
                       - 'actionUrl'
                       - 'type'
                       - 'relatedEntityType'
                       - 'relatedEntityId'
                   || CASE WHEN routed.destination IS NULL THEN '{}'::jsonb ELSE
                       jsonb_build_object(
                           'navigationIntent',
                           jsonb_build_object(
                               'experience', routed.experience,
                               'destination', routed.destination,
                               'accessContextId', routed.access_context_id,
                               'accessRevision', routed.access_revision,
                               'resource', CASE WHEN routed.destination IN (
                                   'Message', 'WorkOrder', 'TechnicianWork', 'Owners') THEN
                                   jsonb_build_object(
                                       'kind', routed.resource_kind,
                                       'id', routed.resource_id)
                                   ELSE NULL
                                   END,
                               'parentResource', NULL,
                               'childResource', NULL,
                               'action', 'Open',
                               'expiresAtUtc', to_jsonb(outbox."CreatedAtUtc" + INTERVAL '7 days'),
                               'fallbackDestination', 'Home'
                           )
                       )
                   END
               )
              FROM routed
             WHERE routed.outbox_id = outbox."Id";
            """);

        migrationBuilder.AddCheckConstraint(
            name: "CK_Notifications_NavigationIntentShape",
            table: "Notifications",
            sql: """
                (
                    "NavigationExperience" IS NULL
                    AND "NavigationDestination" IS NULL
                    AND "NavigationAccessContextId" IS NULL
                    AND "NavigationAccessRevision" IS NULL
                    AND "NavigationResourceKind" IS NULL
                    AND "NavigationResourceId" IS NULL
                    AND "NavigationParentResourceKind" IS NULL
                    AND "NavigationParentResourceId" IS NULL
                    AND "NavigationChildResourceKind" IS NULL
                    AND "NavigationChildResourceId" IS NULL
                    AND "NavigationAction" IS NULL
                    AND "NavigationExpiresAtUtc" IS NULL
                    AND "NavigationFallbackDestination" IS NULL
                )
                OR
                (
                    "NavigationExperience" IS NOT NULL
                    AND "NavigationExperience" IN ('Management', 'Leasing', 'Maintenance', 'Owner', 'Tenant')
                    AND "NavigationDestination" IS NOT NULL
                    AND "NavigationDestination" IN (
                        'Home', 'Notifications', 'Rentals', 'Owners', 'Money', 'Work', 'Inbox',
                        'UnitSummary', 'UnitTenantLease', 'UnitMoney', 'UnitMaintenance', 'UnitRecords',
                        'TenantLedgerEntry', 'Expense', 'ScanDraft', 'Message', 'WorkOrder',
                        'TechnicianWork', 'LeasingRental', 'LeasingApplication', 'LeasingAppointment',
                        'LeasingConversation', 'LeasingMoveIn'
                    )
                    AND "NavigationAccessContextId" IS NOT NULL
                    AND "NavigationAccessContextId" > 0
                    AND "NavigationAccessRevision" IS NOT NULL
                    AND "NavigationAccessRevision" > 0
                    AND "NavigationAction" IS NOT NULL
                    AND "NavigationAction" IN ('Open', 'Review', 'Resolve')
                    AND "NavigationExpiresAtUtc" IS NOT NULL
                    AND "NavigationFallbackDestination" IS NOT NULL
                    AND "NavigationFallbackDestination" IN ('Home', 'Notifications')
                    AND (("NavigationResourceKind" IS NULL AND "NavigationResourceId" IS NULL)
                        OR ("NavigationResourceKind" IS NOT NULL
                            AND btrim("NavigationResourceKind") <> ''
                            AND "NavigationResourceId" IS NOT NULL
                            AND "NavigationResourceId" > 0))
                    AND (("NavigationParentResourceKind" IS NULL AND "NavigationParentResourceId" IS NULL)
                        OR ("NavigationParentResourceKind" IS NOT NULL
                            AND btrim("NavigationParentResourceKind") <> ''
                            AND "NavigationParentResourceId" IS NOT NULL
                            AND "NavigationParentResourceId" > 0))
                    AND (("NavigationChildResourceKind" IS NULL AND "NavigationChildResourceId" IS NULL)
                        OR ("NavigationChildResourceKind" IS NOT NULL
                            AND btrim("NavigationChildResourceKind") <> ''
                            AND "NavigationChildResourceId" IS NOT NULL
                            AND "NavigationChildResourceId" > 0))
                )
                """);

        migrationBuilder.DropColumn(
            name: "ActionUrl",
            table: "Notifications");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "ActionUrl",
            table: "Notifications",
            type: "character varying(500)",
            maxLength: 500,
            nullable: true);

        migrationBuilder.Sql("""
            UPDATE "OutboxMessages"
               SET "Payload" = CASE
                   WHEN "Payload"::jsonb -> 'navigationIntent' ->> 'destination' IN ('Home', 'Notifications', 'Money')
                       THEN jsonb_set(
                           "Payload"::jsonb - 'navigationIntent',
                           '{actionUrl}',
                           to_jsonb(CASE "Payload"::jsonb -> 'navigationIntent' ->> 'destination'
                               WHEN 'Home' THEN '/today'
                               WHEN 'Notifications' THEN '/notices'
                               ELSE '/banking'
                           END),
                           true)
                   WHEN "Payload"::jsonb -> 'navigationIntent' ->> 'destination' = 'Message'
                       THEN jsonb_set(
                           "Payload"::jsonb - 'navigationIntent',
                           '{actionUrl}',
                           to_jsonb('/messages/' ||
                               ("Payload"::jsonb -> 'navigationIntent' -> 'resource' ->> 'id')),
                           true)
                   WHEN "Payload"::jsonb -> 'navigationIntent' ->> 'destination'
                        IN ('WorkOrder', 'TechnicianWork')
                       THEN jsonb_set(
                           "Payload"::jsonb - 'navigationIntent',
                           '{actionUrl}',
                           to_jsonb('/maintenance/' ||
                               ("Payload"::jsonb -> 'navigationIntent' -> 'resource' ->> 'id')),
                           true)
                   WHEN "Payload"::jsonb -> 'navigationIntent' ->> 'destination' = 'Owners'
                       THEN jsonb_set(
                           "Payload"::jsonb - 'navigationIntent',
                           '{actionUrl}',
                           to_jsonb('/owners/' ||
                               ("Payload"::jsonb -> 'navigationIntent' -> 'resource' ->> 'id')),
                           true)
                   ELSE "Payload"::jsonb - 'navigationIntent'
               END
             WHERE lower("MessageType") = 'push'
               AND "Payload"::jsonb ? 'navigationIntent';

            UPDATE "Notifications"
               SET "ActionUrl" = CASE
                   WHEN "NavigationDestination" = 'Message'
                        AND "NavigationResourceKind" = 'Conversation'
                        AND "NavigationExperience" = 'Tenant'
                       THEN '/portal/messages?conversation=' || "NavigationResourceId"::text
                   WHEN "NavigationDestination" = 'Message'
                        AND "NavigationResourceKind" = 'Conversation'
                       THEN '/messages/' || "NavigationResourceId"::text
                   WHEN "NavigationDestination" IN ('WorkOrder', 'TechnicianWork')
                        AND "NavigationResourceKind" = 'WorkOrder'
                       THEN '/maintenance/' || "NavigationResourceId"::text
                   WHEN "NavigationDestination" = 'Owners'
                        AND "NavigationResourceKind" = 'OwnerEntity'
                       THEN '/owners/' || "NavigationResourceId"::text
                   WHEN "NavigationDestination" = 'Money'
                        AND "Type" = 'AccountingMappingConfirmed'
                       THEN '/settings/accounting'
                   WHEN "NavigationDestination" = 'Money' THEN '/banking'
                   WHEN "NavigationDestination" = 'Home' THEN '/today'
                   WHEN "NavigationDestination" = 'Notifications' THEN '/notices'
               END;
            """);

        migrationBuilder.DropCheckConstraint(
            name: "CK_Notifications_NavigationIntentShape",
            table: "Notifications");

        foreach (var column in new[]
        {
            "NavigationAccessContextId",
            "NavigationAccessRevision",
            "NavigationAction",
            "NavigationChildResourceId",
            "NavigationChildResourceKind",
            "NavigationDestination",
            "NavigationExpiresAtUtc",
            "NavigationExperience",
            "NavigationFallbackDestination",
            "NavigationParentResourceId",
            "NavigationParentResourceKind",
            "NavigationResourceId",
            "NavigationResourceKind",
        })
        {
            migrationBuilder.DropColumn(name: column, table: "Notifications");
        }
    }
}
