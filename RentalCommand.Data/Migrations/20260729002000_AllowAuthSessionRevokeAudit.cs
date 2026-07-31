using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace RentalCommand.Data.Migrations;

[DbContext(typeof(RentalCommandDbContext))]
[Migration("20260729002000_AllowAuthSessionRevokeAudit")]
public partial class AllowAuthSessionRevokeAudit : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.rc_pre_auth_account_security_audit_allows(target_portfolio_id integer, target_attempt_id uuid, target_command_type text, target_command_idempotency_key text, target_mutation_ordinal bigint, target_user_id integer, target_entity_type text, target_entity_id integer, target_operation integer, target_actor_label text, target_change_reason text, target_new_values jsonb)
             RETURNS boolean
             LANGUAGE sql
             STABLE SECURITY DEFINER
             SET search_path TO 'pg_catalog', 'public'
            AS $function$
              SELECT (
                session_user = 'rentalcommand_api'
                AND target_portfolio_id IS NOT NULL
                AND target_portfolio_id > 0
                AND target_attempt_id IS NOT NULL
                AND target_command_type IN (
                  'auth.email.confirm',
                  'auth.email.google-confirm',
                  'auth.password.reset',
                  'workspace-invitation.activate')
                AND target_command_idempotency_key ~
                  ('^' || target_user_id::text || ':[0-9a-f]{64}$')
                AND target_mutation_ordinal = 1
                AND target_user_id IS NOT NULL
                AND target_user_id > 0
                AND target_entity_type = 'ApplicationUser'
                AND target_entity_id = target_user_id
                AND target_operation = 1
                AND target_actor_label = 'authentication:account-security'
                AND target_new_values IS NOT NULL
                AND target_new_values ->> 'TargetUserId' = target_user_id::text
                AND target_new_values ->> 'SecurityIntentHash' ~ '^[0-9a-f]{64}$'
                AND (
                  (target_command_type = 'auth.email.confirm'
                   AND target_new_values ->> 'SecurityEvent' = 'EmailConfirmed'
                   AND target_change_reason = 'Account email confirmed')
                  OR
                  (target_command_type = 'auth.email.google-confirm'
                   AND target_new_values ->> 'SecurityEvent' = 'GoogleEmailConfirmed'
                   AND target_change_reason = 'Google-verified account email confirmed')
                  OR
                  (target_command_type = 'auth.password.reset'
                   AND target_new_values ->> 'SecurityEvent' = 'PasswordReset'
                   AND target_change_reason = 'Password reset completed')
                  OR
                  (target_command_type = 'workspace-invitation.activate'
                   AND target_new_values ->> 'SecurityEvent' = 'WorkspaceInvitationActivated'
                   AND target_change_reason = 'Workspace invitation activated'))
                AND (target_command_type <> 'auth.email.google-confirm'
                     OR split_part(target_command_idempotency_key, ':', 2) =
                        target_new_values ->> 'SecurityIntentHash')
                AND EXISTS (
                  SELECT 1
                  FROM public."AspNetUsers" user_row
                  WHERE user_row."Id" = target_user_id
                    AND user_row."EmailConfirmed" = TRUE
                    AND (target_command_type NOT IN (
                           'auth.password.reset', 'workspace-invitation.activate')
                         OR (user_row."PasswordHash" IS NOT NULL
                             AND user_row."SecurityStamp" IS NOT NULL
                             AND user_row."AccessFailedCount" = 0
                             AND user_row."LockoutEnd" IS NULL))
                    AND user_row.xmin = pg_current_xact_id()::xid)
                AND (
                  (target_command_type = 'workspace-invitation.activate'
                   AND split_part(target_command_idempotency_key, ':', 2) =
                       target_new_values ->> 'SecurityIntentHash'
                   AND EXISTS (
                     SELECT 1
                     FROM public."WorkspaceInvitations" invitation
                     JOIN public."WorkspaceMemberships" membership
                       ON membership."Id" = invitation."WorkspaceMembershipId"
                      AND membership."PortfolioId" = invitation."PortfolioId"
                     WHERE invitation."Id" = (target_new_values ->> 'InvitationId')::bigint
                       AND invitation."InvitedUserId" = target_user_id
                       AND invitation."PortfolioId" = target_portfolio_id
                       AND invitation."AcceptedAtUtc" IS NOT NULL
                       AND invitation.xmin = pg_current_xact_id()::xid
                       AND membership."Id" =
                           (target_new_values ->> 'WorkspaceMembershipId')::integer
                       AND membership."AccessContextId" =
                           (target_new_values ->> 'AuditRootAccessContextId')::integer))
                  OR
                  (target_command_type <> 'workspace-invitation.activate'
                   AND EXISTS (
                     SELECT 1
                     FROM (
                       SELECT option."AccessContextId", option."PortfolioId"
                       FROM public.rc_list_effective_access_contexts(
                         target_user_id, clock_timestamp()) option
                       ORDER BY option."AccessContextId"
                       LIMIT 1) root
                     WHERE root."PortfolioId" = target_portfolio_id
                       AND root."AccessContextId" =
                         (target_new_values ->> 'AuditRootAccessContextId')::integer)))
                AND EXISTS (
                  SELECT 1
                  FROM public."AtomicCommandReceipts" receipt
                  WHERE receipt."AttemptId" = target_attempt_id
                    AND receipt."CommandType" = target_command_type
                    AND receipt."IdempotencyKey" = target_command_idempotency_key
                    AND receipt.xmin = pg_current_xact_id()::xid)
              )
              OR (
                session_user = 'rentalcommand_api'
                AND target_portfolio_id IS NOT NULL
                AND target_portfolio_id > 0
                AND target_attempt_id IS NOT NULL
                AND target_command_type = 'auth-session:revoke'
                AND target_command_idempotency_key ~ '^operation:[0-9a-f]{32}$'
                AND target_mutation_ordinal = 1
                AND target_user_id IS NOT NULL
                AND target_user_id > 0
                AND target_entity_type = 'AuthSession'
                AND target_operation = 1
                AND target_actor_label = 'authentication:logout'
                AND target_change_reason = 'Authentication session revoked'
                AND target_new_values IS NOT NULL
                AND target_new_values ->> 'AuthSessionId' =
                    NULLIF(current_setting('app.auth_session_id', true), '')
                AND target_user_id::text =
                    NULLIF(current_setting('app.current_user_id', true), '')
                AND target_entity_id::text =
                    NULLIF(current_setting('app.current_access_context_id', true), '')
                AND EXISTS (
                  SELECT 1
                  FROM public."AuthSessions" session
                  JOIN public."WorkspaceAccessContexts" access_context
                    ON access_context."Id" = session."ActiveAccessContextId"
                   AND access_context."UserId" = session."UserId"
                  WHERE session."Id"::text = target_new_values ->> 'AuthSessionId'
                    AND session."UserId" = target_user_id
                    AND session."ActiveAccessContextId" = target_entity_id
                    AND session."Status" = 'Revoked'
                    AND session."RevokedAtUtc" IS NOT NULL
                    AND session.xmin = pg_current_xact_id()::xid
                    AND access_context."Id" = target_entity_id
                    AND access_context."PortfolioId" = target_portfolio_id
                    AND access_context."AccessRevision"::text =
                        NULLIF(current_setting('app.access_revision', true), ''))
                AND EXISTS (
                  SELECT 1
                  FROM public."AtomicCommandReceipts" receipt
                  WHERE receipt."AttemptId" = target_attempt_id
                    AND receipt."CommandType" = target_command_type
                    AND receipt."IdempotencyKey" = target_command_idempotency_key
                    AND receipt.xmin = pg_current_xact_id()::xid)
              );
            $function$;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql(
            """
            CREATE OR REPLACE FUNCTION public.rc_pre_auth_account_security_audit_allows(target_portfolio_id integer, target_attempt_id uuid, target_command_type text, target_command_idempotency_key text, target_mutation_ordinal bigint, target_user_id integer, target_entity_type text, target_entity_id integer, target_operation integer, target_actor_label text, target_change_reason text, target_new_values jsonb)
             RETURNS boolean
             LANGUAGE sql
             STABLE SECURITY DEFINER
             SET search_path TO 'pg_catalog', 'public'
            AS $function$
              SELECT session_user = 'rentalcommand_api'
                 AND target_portfolio_id IS NOT NULL
                 AND target_portfolio_id > 0
                 AND target_attempt_id IS NOT NULL
                 AND target_command_type IN (
                   'auth.email.confirm',
                   'auth.email.google-confirm',
                   'auth.password.reset',
                   'workspace-invitation.activate')
                 AND target_command_idempotency_key ~
                   ('^' || target_user_id::text || ':[0-9a-f]{64}$')
                 AND target_mutation_ordinal = 1
                 AND target_user_id IS NOT NULL
                 AND target_user_id > 0
                 AND target_entity_type = 'ApplicationUser'
                 AND target_entity_id = target_user_id
                 AND target_operation = 1
                 AND target_actor_label = 'authentication:account-security'
                 AND target_new_values IS NOT NULL
                 AND target_new_values ->> 'TargetUserId' = target_user_id::text
                 AND target_new_values ->> 'SecurityIntentHash' ~ '^[0-9a-f]{64}$'
                 AND (
                   (target_command_type = 'auth.email.confirm'
                    AND target_new_values ->> 'SecurityEvent' = 'EmailConfirmed'
                    AND target_change_reason = 'Account email confirmed')
                   OR
                   (target_command_type = 'auth.email.google-confirm'
                    AND target_new_values ->> 'SecurityEvent' = 'GoogleEmailConfirmed'
                    AND target_change_reason = 'Google-verified account email confirmed')
                   OR
                   (target_command_type = 'auth.password.reset'
                    AND target_new_values ->> 'SecurityEvent' = 'PasswordReset'
                    AND target_change_reason = 'Password reset completed')
                   OR
                   (target_command_type = 'workspace-invitation.activate'
                    AND target_new_values ->> 'SecurityEvent' = 'WorkspaceInvitationActivated'
                    AND target_change_reason = 'Workspace invitation activated'))
                 AND (target_command_type <> 'auth.email.google-confirm'
                      OR split_part(target_command_idempotency_key, ':', 2) =
                         target_new_values ->> 'SecurityIntentHash')
                 AND EXISTS (
                   SELECT 1
                   FROM public."AspNetUsers" user_row
                   WHERE user_row."Id" = target_user_id
                     AND user_row."EmailConfirmed" = TRUE
                     AND (target_command_type NOT IN (
                            'auth.password.reset', 'workspace-invitation.activate')
                          OR (user_row."PasswordHash" IS NOT NULL
                              AND user_row."SecurityStamp" IS NOT NULL
                              AND user_row."AccessFailedCount" = 0
                              AND user_row."LockoutEnd" IS NULL))
                     AND user_row.xmin = pg_current_xact_id()::xid)
                 AND (
                   (target_command_type = 'workspace-invitation.activate'
                    AND split_part(target_command_idempotency_key, ':', 2) =
                        target_new_values ->> 'SecurityIntentHash'
                    AND EXISTS (
                      SELECT 1
                      FROM public."WorkspaceInvitations" invitation
                      JOIN public."WorkspaceMemberships" membership
                        ON membership."Id" = invitation."WorkspaceMembershipId"
                       AND membership."PortfolioId" = invitation."PortfolioId"
                      WHERE invitation."Id" = (target_new_values ->> 'InvitationId')::bigint
                        AND invitation."InvitedUserId" = target_user_id
                        AND invitation."PortfolioId" = target_portfolio_id
                        AND invitation."AcceptedAtUtc" IS NOT NULL
                        AND invitation.xmin = pg_current_xact_id()::xid
                        AND membership."Id" =
                            (target_new_values ->> 'WorkspaceMembershipId')::integer
                        AND membership."AccessContextId" =
                            (target_new_values ->> 'AuditRootAccessContextId')::integer))
                   OR
                   (target_command_type <> 'workspace-invitation.activate'
                    AND EXISTS (
                      SELECT 1
                      FROM (
                        SELECT option."AccessContextId", option."PortfolioId"
                        FROM public.rc_list_effective_access_contexts(
                          target_user_id, clock_timestamp()) option
                        ORDER BY option."AccessContextId"
                        LIMIT 1) root
                      WHERE root."PortfolioId" = target_portfolio_id
                        AND root."AccessContextId" =
                          (target_new_values ->> 'AuditRootAccessContextId')::integer)))
                 AND EXISTS (
                   SELECT 1
                   FROM public."AtomicCommandReceipts" receipt
                   WHERE receipt."AttemptId" = target_attempt_id
                     AND receipt."CommandType" = target_command_type
                     AND receipt."IdempotencyKey" = target_command_idempotency_key
                     AND receipt.xmin = pg_current_xact_id()::xid);
            $function$;
            """);
    }
}
