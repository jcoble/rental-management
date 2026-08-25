using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;
using RentalCommand.Data.Atomic;

namespace RentalCommand.Data.Operations;

public sealed class RecoverVendorDispatchChronologyRule
{
    public const string ResultContract = "vendor-dispatch.chronology-recovery.v1";

    private readonly RentalCommandDbContext _db;

    public RecoverVendorDispatchChronologyRule(RentalCommandDbContext db) => _db = db;

    private const string OriginalCommandType = "vendor-dispatch.create";
    private const string RecoveryCommandType = "vendor-dispatch.recover-chronology";

    public static TransactionalWrite<RecoverVendorDispatchChronologyCommand, RecoverVendorDispatchChronologyResult> Write(
        RecoverVendorDispatchChronologyCommand command,
        RentalCommandDbContext db)
    {
        var handler = new RecoverVendorDispatchChronologyRule(db);
        return new TransactionalWrite<RecoverVendorDispatchChronologyCommand, RecoverVendorDispatchChronologyResult>(
            "vendor-dispatch.recover-chronology",  command,
            ResultContract, WriteLockPlan.None, handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<RecoverVendorDispatchChronologyResult> ExecuteAsync(
        RecoverVendorDispatchChronologyCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        ValidateCommand(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        await AuthorizeAsync(command, _db, times.WallClockUtc, ct);
        if (command.CorrectDispatchedAtUtc > times.EffectiveNowUtc)
        {
            throw new InvalidOperationException(
                "CorrectDispatchedAtUtc cannot be after the portfolio's effective business time.");
        }

        var rows = await _db.ExecuteAtomicSqlMutationAsync<ChronologyRecoveryMutation>(context, $"""
            WITH exact_graph AS MATERIALIZED (
                SELECT work_order."Id" AS work_order_id,
                       dispatch."Id" AS dispatch_id,
                       status_event."Id" AS status_event_id,
                       outbox."Id" AS outbox_id,
                       vendor."Id" AS vendor_id,
                       work_order."UpdatedAt" AS work_order_updated_at,
                       work_order."Status" AS work_order_status,
                       work_order."ChronologyRepairOriginalUpdatedAtUtc"
                           AS work_order_original_updated_at,
                       dispatch."Message" AS dispatch_message,
                       status_event."ChangedByUserId" AS status_event_user_id,
                       status_event."ChronologyRepairOriginalCreatedAtUtc"
                           AS status_event_original_created_at,
                       (
                         session_user = 'rentalcommand_api'
                         AND work_order."VendorId" = dispatch."VendorId"
                         AND dispatch."Status" = {(int)VendorDispatchStatus.Dispatched}
                         AND dispatch."RespondedAtUtc" IS NULL
                         AND dispatch."DispatchedAtUtc" = {command.ExpectedContaminatedDispatchedAtUtc}
                         AND status_event."Kind" = 'Dispatch'
                         AND status_event."Visibility" = 'Public'
                         AND status_event."Note" = 'Vendor dispatch sent by SMS.'
                         AND status_event."FromStatus" = status_event."ToStatus"
                         AND status_event."ToStatus" = work_order."Status"
                         AND status_event."CreatedAtUtc" =
                             {command.ExpectedContaminatedDispatchedAtUtc}
                         AND status_event."ChronologyRepairOriginalCreatedAtUtc" IS NULL
                         AND outbox."MessageType" = 'sms'
                         AND outbox."Payload" ->> 'message' IS NOT DISTINCT FROM dispatch."Message"
                         AND NULLIF(BTRIM(outbox."Payload" ->> 'to'), '') IS NOT NULL
                         AND outbox."CreatedAtUtc" = {command.ExpectedContaminatedDispatchedAtUtc}
                         AND outbox."NextAttemptAtUtc" =
                             {command.ExpectedContaminatedDispatchedAtUtc}
                         AND outbox."AttemptCount" = 0
                         AND outbox."LastAttemptAtUtc" IS NULL
                         AND outbox."ClaimOwner" IS NULL
                         AND outbox."ClaimToken" IS NULL
                         AND outbox."ClaimExpiresAtUtc" IS NULL
                         AND outbox."AcceptedAtUtc" IS NULL
                         AND outbox."DeliveredAtUtc" IS NULL
                         AND outbox."DeadLetteredAtUtc" IS NULL
                         AND outbox."Provider" IS NULL
                         AND outbox."ProviderMessageId" IS NULL
                         AND outbox."FailureKind" IS NULL
                         AND outbox."LastError" IS NULL
                         AND (
                           work_order."UpdatedAt" <>
                               {command.ExpectedContaminatedDispatchedAtUtc}
                           OR work_order."ChronologyRepairOriginalUpdatedAtUtc" IS NULL)
                       ) AS graph_matches
                FROM "WorkOrders" AS work_order
                JOIN "VendorDispatches" AS dispatch
                  ON dispatch."Id" = {command.DispatchId}
                 AND dispatch."PortfolioId" = work_order."PortfolioId"
                 AND dispatch."WorkOrderId" = work_order."Id"
                JOIN "Vendors" AS vendor
                  ON vendor."Id" = dispatch."VendorId"
                 AND vendor."PortfolioId" = dispatch."PortfolioId"
                JOIN "WorkOrderStatusEvents" AS status_event
                  ON status_event."Id" = {command.ExpectedStatusEventId}
                 AND status_event."PortfolioId" = work_order."PortfolioId"
                 AND status_event."WorkOrderId" = work_order."Id"
                JOIN "OutboxMessages" AS outbox
                  ON outbox."Id" = {command.ExpectedOutboxId}
                 AND outbox."PortfolioId" = work_order."PortfolioId"
                 AND outbox."IdempotencyKey" = {command.ExpectedOutboxIdempotencyKey}
                WHERE work_order."Id" = {command.WorkOrderId}
                  AND work_order."PortfolioId" = {command.PortfolioId}
                FOR UPDATE OF work_order, dispatch, status_event, outbox
            ),
            graph_summary AS MATERIALIZED (
                SELECT COUNT(*)::integer AS graph_count,
                       COALESCE(BOOL_AND(graph.graph_matches), FALSE) AS graph_matches,
                       COALESCE(BOOL_OR(
                           graph.work_order_updated_at =
                               {command.ExpectedContaminatedDispatchedAtUtc}), FALSE)
                           AS work_order_updated_at_repaired
                FROM exact_graph AS graph
            ),
            exact_command_rows AS MATERIALIZED (
                SELECT audit."Id",
                       audit."AttemptId",
                       audit."UserId",
                       audit."EntityType",
                       audit."EntityId",
                       audit."Operation",
                       audit."Timestamp"
                FROM "AtomicAuditLogs" AS audit
                WHERE audit."PortfolioId" = {command.PortfolioId}
                  AND audit."CommandType" = {OriginalCommandType}
                  AND audit."CommandIdempotencyKey" =
                      {command.OriginalCommandIdempotencyKey}
                FOR UPDATE
            ),
            audit_summary AS MATERIALIZED (
                SELECT COUNT(*)::integer AS audit_count,
                       COUNT(DISTINCT audit."AttemptId")::integer AS attempt_count,
                       COUNT(DISTINCT COALESCE(audit."UserId", -1))::integer AS user_count,
                       MAX(audit."UserId") AS audit_user_id,
                       (COUNT(*) FILTER (
                         WHERE audit."EntityType" = {nameof(WorkOrder)}
                           AND audit."EntityId" = {command.WorkOrderId}
                           AND audit."Operation" = {(int)AuditLogOperation.Updated}
                           AND audit."Timestamp" >=
                               {command.ExpectedContaminatedDispatchedAtUtc}
                           AND audit."Timestamp" < {command.CorrectDispatchedAtUtc}))
                           ::integer AS work_order_audit_count,
                       (COUNT(*) FILTER (
                         WHERE audit."EntityType" = {nameof(VendorDispatch)}
                           AND audit."EntityId" = {command.DispatchId}
                           AND audit."Operation" = {(int)AuditLogOperation.Created}
                           AND audit."Timestamp" >=
                               {command.ExpectedContaminatedDispatchedAtUtc}
                           AND audit."Timestamp" < {command.CorrectDispatchedAtUtc}))
                           ::integer AS dispatch_audit_count,
                       (COUNT(*) FILTER (
                         WHERE audit."EntityType" = {nameof(WorkOrderStatusEvent)}
                           AND audit."EntityId" = {command.ExpectedStatusEventId}
                           AND audit."Operation" = {(int)AuditLogOperation.Created}
                           AND audit."Timestamp" >=
                               {command.ExpectedContaminatedDispatchedAtUtc}
                           AND audit."Timestamp" < {command.CorrectDispatchedAtUtc}))
                           ::integer AS status_event_audit_count,
                       ARRAY_AGG(audit."Id") FILTER (
                         WHERE audit."Timestamp" >=
                               {command.ExpectedContaminatedDispatchedAtUtc}
                           AND audit."Timestamp" < {command.CorrectDispatchedAtUtc})
                           AS repair_audit_ids
                FROM exact_command_rows AS audit
            ),
            validated AS MATERIALIZED (
                SELECT graph.work_order_id,
                       graph.dispatch_id,
                       graph.status_event_id,
                       graph.outbox_id,
                       graph_summary.work_order_updated_at_repaired,
                       (
                         graph_summary.graph_count = 1
                         AND graph_summary.graph_matches
                         AND audit_summary.audit_count = 3
                         AND audit_summary.attempt_count = 1
                         AND audit_summary.user_count = 1
                         AND audit_summary.work_order_audit_count = 1
                         AND audit_summary.dispatch_audit_count = 1
                         AND audit_summary.status_event_audit_count = 1
                         AND CARDINALITY(audit_summary.repair_audit_ids) = 3
                         AND graph.status_event_user_id IS NOT DISTINCT FROM
                             audit_summary.audit_user_id
                       ) AS is_valid,
                       audit_summary.repair_audit_ids
                FROM exact_graph AS graph
                CROSS JOIN graph_summary
                CROSS JOIN audit_summary
                WHERE graph_summary.graph_count = 1
            ),
            repaired_audits AS (
                UPDATE "AtomicAuditLogs" AS audit
                   SET "Timestamp" = {command.CorrectDispatchedAtUtc}
                  FROM validated
                 WHERE validated.is_valid
                   AND audit."Id" = ANY(validated.repair_audit_ids)
                RETURNING audit."Id"
            ),
            repaired_dispatch AS (
                UPDATE "VendorDispatches" AS dispatch
                   SET "DispatchedAtUtc" = {command.CorrectDispatchedAtUtc}
                  FROM validated
                 WHERE validated.is_valid
                   AND dispatch."Id" = validated.dispatch_id
                   AND dispatch."PortfolioId" = {command.PortfolioId}
                RETURNING dispatch."Id"
            ),
            repaired_status_event AS (
                UPDATE "WorkOrderStatusEvents" AS status_event
                   SET "CreatedAtUtc" = {command.CorrectDispatchedAtUtc},
                       "ChronologyRepairOriginalCreatedAtUtc" =
                           {command.ExpectedContaminatedDispatchedAtUtc}
                  FROM validated
                 WHERE validated.is_valid
                   AND status_event."Id" = validated.status_event_id
                   AND status_event."PortfolioId" = {command.PortfolioId}
                RETURNING status_event."Id"
            ),
            repaired_work_order AS (
                UPDATE "WorkOrders" AS work_order
                   SET "UpdatedAt" = {command.CorrectDispatchedAtUtc},
                       "ChronologyRepairOriginalUpdatedAtUtc" =
                           {command.ExpectedContaminatedDispatchedAtUtc}
                  FROM validated
                 WHERE validated.is_valid
                   AND validated.work_order_updated_at_repaired
                   AND work_order."Id" = validated.work_order_id
                   AND work_order."PortfolioId" = {command.PortfolioId}
                RETURNING work_order."Id"
            ),
            repaired_outbox AS (
                UPDATE "OutboxMessages" AS outbox
                   SET "CreatedAtUtc" = {command.CorrectDispatchedAtUtc},
                       "NextAttemptAtUtc" = {command.CorrectDispatchedAtUtc}
                  FROM validated
                 WHERE validated.is_valid
                   AND outbox."Id" = validated.outbox_id
                   AND outbox."PortfolioId" = {command.PortfolioId}
                RETURNING outbox."Id"
            ),
            recovery_audit AS (
                INSERT INTO "AtomicAuditLogs" (
                    "AttemptId", "CommandType", "CommandIdempotencyKey",
                    "MutationOrdinal", "PortfolioId", "UserId", "ActorLabel",
                    "EntityType", "EntityId", "Operation", "OldValues",
                    "NewValues", "ChangeReason", "Timestamp", "IpAddress")
                SELECT {context.AttemptId},
                       {RecoveryCommandType},
                       {command.DeliveryIdempotencyKey},
                       0::bigint,
                       {command.PortfolioId},
                       {command.ActorUserId},
                       NULL::text,
                       {nameof(WorkOrder)},
                       validated.work_order_id,
                       {(int)AuditLogOperation.Updated},
                       NULL::jsonb,
                       jsonb_build_object(
                         'WorkOrderId', {command.WorkOrderId},
                         'DispatchId', {command.DispatchId},
                         'ExpectedStatusEventId', {command.ExpectedStatusEventId},
                         'ExpectedOutboxId', {command.ExpectedOutboxId},
                         'ExpectedContaminatedDispatchedAtUtc',
                            {command.ExpectedContaminatedDispatchedAtUtc},
                         'CorrectDispatchedAtUtc', {command.CorrectDispatchedAtUtc},
                         'OriginalCommandIdempotencyKey',
                            {command.OriginalCommandIdempotencyKey},
                         'WorkOrderUpdatedAtRepaired',
                            validated.work_order_updated_at_repaired),
                       'Recovered exact pending vendor-dispatch business chronology.',
                       {times.EffectiveNowUtc},
                       NULL::text
                FROM validated
                WHERE validated.is_valid
                RETURNING "Id"
            ),
            data_update_outbox AS (
                INSERT INTO "OutboxMessages" (
                    "PortfolioId", "MessageType", "Payload", "IdempotencyKey",
                    "AttemptCount", "CreatedAtUtc", "NextAttemptAtUtc",
                    "LastAttemptAtUtc", "ClaimOwner", "ClaimToken",
                    "ClaimExpiresAtUtc", "AcceptedAtUtc", "DeliveredAtUtc",
                    "DeadLetteredAtUtc", "Provider", "ProviderMessageId",
                    "FailureKind", "LastError")
                SELECT {command.PortfolioId},
                       'data-update',
                       jsonb_build_object(
                         'entityType', {nameof(VendorDispatch)},
                         'entityId', validated.dispatch_id,
                         'operation', 'recover-chronology'),
                       {OutboxIdempotency.Create(
                           "vendor-dispatch-chronology-recovery",
                           command.DeliveryIdempotencyKey)},
                       0,
                       {times.EffectiveNowUtc},
                       {times.EffectiveNowUtc},
                       NULL::timestamp with time zone,
                       NULL::text,
                       NULL::uuid,
                       NULL::timestamp with time zone,
                       NULL::timestamp with time zone,
                       NULL::timestamp with time zone,
                       NULL::timestamp with time zone,
                       NULL::text,
                       NULL::text,
                       NULL::text,
                       NULL::text
                FROM validated
                WHERE validated.is_valid
                RETURNING "Id"
            )
            SELECT COALESCE((SELECT COUNT(*) FROM repaired_audits), 0)::integer
                       AS "RepairedAuditCount",
                   COALESCE((SELECT COUNT(*) FROM repaired_dispatch), 0)::integer
                       AS "RepairedDispatchCount",
                   COALESCE((SELECT COUNT(*) FROM repaired_status_event), 0)::integer
                       AS "RepairedStatusEventCount",
                   COALESCE((SELECT COUNT(*) FROM repaired_work_order), 0)::integer
                       AS "RepairedWorkOrderCount",
                   COALESCE((SELECT COUNT(*) FROM repaired_outbox), 0)::integer
                       AS "RepairedOutboxCount",
                   COALESCE((SELECT COUNT(*) FROM recovery_audit), 0)::integer
                       AS "RecoveryAuditCount",
                   COALESCE((SELECT COUNT(*) FROM data_update_outbox), 0)::integer
                       AS "DataUpdateOutboxCount",
                   COALESCE((SELECT work_order_id FROM validated WHERE is_valid), 0)::integer
                       AS "WorkOrderId",
                   COALESCE((SELECT dispatch_id FROM validated WHERE is_valid), 0)::integer
                       AS "DispatchId",
                   COALESCE((SELECT status_event_id FROM validated WHERE is_valid), 0)::integer
                       AS "StatusEventId",
                   COALESCE((SELECT outbox_id FROM validated WHERE is_valid), 0)::bigint
                       AS "OutboxId",
                   {command.CorrectDispatchedAtUtc} AS "DispatchedAtUtc",
                   COALESCE((
                       SELECT work_order_updated_at_repaired
                       FROM validated
                       WHERE is_valid), FALSE) AS "WorkOrderUpdatedAtRepaired"
            """,
            [
                new AtomicSqlMutationTarget(
                    "AtomicAuditLogs",
                    AtomicSqlMutationOperation.Insert),
                new AtomicSqlMutationTarget(
                    "AtomicAuditLogs",
                    AtomicSqlMutationOperation.Update),
                new AtomicSqlMutationTarget(
                    "VendorDispatches",
                    AtomicSqlMutationOperation.Update),
                new AtomicSqlMutationTarget(
                    "WorkOrderStatusEvents",
                    AtomicSqlMutationOperation.Update),
                new AtomicSqlMutationTarget(
                    "WorkOrders",
                    AtomicSqlMutationOperation.Update),
                new AtomicSqlMutationTarget(
                    "OutboxMessages",
                    AtomicSqlMutationOperation.Insert),
                new AtomicSqlMutationTarget(
                    "OutboxMessages",
                    AtomicSqlMutationOperation.Update),
            ],
            ct);
        var result = rows.SingleOrDefault();
        if (result is null
            || result.RepairedAuditCount != 3
            || result.RepairedDispatchCount != 1
            || result.RepairedStatusEventCount != 1
            || result.RepairedOutboxCount != 1
            || result.RepairedWorkOrderCount != (result.WorkOrderUpdatedAtRepaired ? 1 : 0)
            || result.RecoveryAuditCount != 1
            || result.DataUpdateOutboxCount != 1
            || result.WorkOrderId != command.WorkOrderId
            || result.DispatchId != command.DispatchId
            || result.StatusEventId != command.ExpectedStatusEventId
            || result.OutboxId != command.ExpectedOutboxId
            || result.DispatchedAtUtc != command.CorrectDispatchedAtUtc)
        {
            throw ExpectedStateMismatch();
        }

        return new RecoverVendorDispatchChronologyResult(
            result.WorkOrderId,
            result.DispatchId,
            result.StatusEventId,
            result.OutboxId,
            result.DispatchedAtUtc,
            result.WorkOrderUpdatedAtRepaired);
    }

    public async Task AuthorizeAsync(
        RecoverVendorDispatchChronologyCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ValidateCommand(command);
        await AuthorizeAsync(
            command,
            _db,
            await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct),
            ct);
    }

    private static async Task AuthorizeAsync(
        RecoverVendorDispatchChronologyCommand command,
        RentalCommandDbContext db,
        DateTime securityNowUtc,
        CancellationToken ct)
    {
        var authorized = await DispatchWorkOrderToVendorRule.WhereManagementAuthorized(
                db.Set<WorkOrder>().Where(workOrder =>
                    workOrder.Id == command.WorkOrderId
                    && workOrder.PortfolioId == command.PortfolioId),
                db,
                command.PortfolioId,
                command.ManagementAccess,
                securityNowUtc)
            .AnyAsync(ct);
        if (!authorized)
        {
            throw new UnauthorizedAccessException(
                "The current workspace access does not authorize vendor-dispatch chronology recovery.");
        }
    }

    private static void ValidateCommand(RecoverVendorDispatchChronologyCommand command)
    {
        if (command.PortfolioId <= 0
            || command.WorkOrderId <= 0
            || command.DispatchId <= 0
            || command.ExpectedStatusEventId <= 0
            || command.ExpectedOutboxId <= 0
            || command.ActorUserId <= 0
            || command.ManagementAccess.UserId != command.ActorUserId
            || command.ManagementAccess.SessionId == Guid.Empty
            || command.ManagementAccess.AccessContextId <= 0
            || command.ManagementAccess.AccessRevision <= 0
            || command.ExpectedContaminatedDispatchedAtUtc == default
            || command.CorrectDispatchedAtUtc == default
            || command.ExpectedContaminatedDispatchedAtUtc.Kind != DateTimeKind.Utc
            || command.CorrectDispatchedAtUtc.Kind != DateTimeKind.Utc
            || command.CorrectDispatchedAtUtc <= command.ExpectedContaminatedDispatchedAtUtc
            || string.IsNullOrWhiteSpace(command.ExpectedOutboxIdempotencyKey)
            || command.ExpectedOutboxIdempotencyKey.Length > 300
            || command.ExpectedOutboxIdempotencyKey
                != $"vendor-dispatch:{command.DispatchId}:sms"
            || string.IsNullOrWhiteSpace(command.OriginalCommandIdempotencyKey)
            || command.OriginalCommandIdempotencyKey.Length > 200
            || !command.OriginalCommandIdempotencyKey.StartsWith(
                $"{command.PortfolioId}:{command.WorkOrderId}:",
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200)
        {
            throw new ArgumentException(
                "Vendor-dispatch chronology recovery requires exact UTC timestamps, row identities, original command key, pending SMS key, active actor, and delivery key.");
        }
    }

    private static InvalidOperationException ExpectedStateMismatch() =>
        new("Vendor-dispatch chronology recovery expected-state check failed; no rows were changed.");

    private sealed record ChronologyRecoveryMutation(
        int RepairedAuditCount,
        int RepairedDispatchCount,
        int RepairedStatusEventCount,
        int RepairedWorkOrderCount,
        int RepairedOutboxCount,
        int RecoveryAuditCount,
        int DataUpdateOutboxCount,
        int WorkOrderId,
        int DispatchId,
        int StatusEventId,
        long OutboxId,
        DateTime DispatchedAtUtc,
        bool WorkOrderUpdatedAtRepaired);
}
