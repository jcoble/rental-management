using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class RecordTechnicianWorkEntryRule
{
    public const string ResultContract = "technician-work-entry.v1";

    private readonly RentalCommandDbContext _db;

    public RecordTechnicianWorkEntryRule(RentalCommandDbContext db) => _db = db;

    public static TransactionalWrite<RecordTechnicianWorkEntryCommand, RecordTechnicianWorkEntryResult> Write(
        RecordTechnicianWorkEntryCommand command,
        RentalCommandDbContext db)
    {
        var handler = new RecordTechnicianWorkEntryRule(db);
        return new TransactionalWrite<RecordTechnicianWorkEntryCommand, RecordTechnicianWorkEntryResult>(
            "technician-work-entry.record", WriteIdempotencyPolicy.Required, command, ResultContract,
            WorkOrderLock(command.WorkOrderId), handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<RecordTechnicianWorkEntryResult> ExecuteAsync(
        RecordTechnicianWorkEntryCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (!await _db.Set<WorkOrder>().AsNoTracking().AnyAsync(workOrder =>
                workOrder.Id == command.WorkOrderId &&
                workOrder.PortfolioId == command.PortfolioId &&
                workOrder.Status != WorkOrderStatus.Cancelled &&
                workOrder.Status != WorkOrderStatus.Archived, ct))
            throw new DomainValidationException(
                "Cancelled or archived work orders cannot receive technician entries.", 409);
        var responsibility = await AssignedTechnicianCommandAuthorization.RequireResponsibilityAsync(
            _db, command.PortfolioId, command.ActorUserId, command.ActorSessionId,
            command.ActorAccessContextId, command.ActorAccessRevision, command.WorkOrderId,
            CapabilityKeys.AssignedWorkTimeMaterialsManage, now, ct);

        if (command.Kind == TechnicianWorkEntryKind.Photo)
        {
            var fileExists = await _db.Set<StoredFile>().AsNoTracking().AnyAsync(file =>
                file.Id == command.StoredFileId && file.PortfolioId == command.PortfolioId &&
                file.EntityType == nameof(WorkOrder) && file.EntityId == command.WorkOrderId &&
                file.DeletedAt == null && file.ContentType.StartsWith("image/"), ct);
            if (!fileExists) throw new DomainValidationException("The photo is not attached to this assignment.");
        }

        var entry = new TechnicianWorkEntry
        {
            PortfolioId = command.PortfolioId,
            WorkOrderId = command.WorkOrderId,
            WorkOrderResponsibilityId = responsibility.Id,
            WorkspaceMembershipId = responsibility.WorkspaceMembershipId,
            MembershipRoleAssignmentId = responsibility.MembershipRoleAssignmentId,
            CreatedByUserId = command.ActorUserId,
            Kind = command.Kind,
            Note = command.Note?.Trim(),
            Quantity = command.Quantity,
            Unit = command.Unit?.Trim(),
            StoredFileId = command.StoredFileId,
            OccurredAtUtc = command.OccurredAtUtc == default ? now : command.OccurredAtUtc,
            CreatedAtUtc = now,
        };
        _db.Add(entry);
        await context.FlushBusinessAsync(ct);
        context.UseDatabaseWallClockForAudit(now);
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(TechnicianWorkEntry), entry.Id, AuditLogOperation.Created, command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                entry.WorkOrderId,
                entry.Kind,
                entry.Quantity,
                entry.Unit,
                entry.StoredFileId,
            }), ChangeReason: "Assigned technician recorded an operational field entry."), now);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(WorkOrder), entityId = command.WorkOrderId }),
            IdempotencyKey = $"technician-work-entry:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(entry.Id, entry.WorkOrderId, entry.Kind, now);
    }

    public Task AuthorizeReplayAsync(RecordTechnicianWorkEntryCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RetiredPath();

    public async Task AuthorizeAsync(RecordTechnicianWorkEntryCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await AssignedTechnicianCommandAuthorization.RequireResponsibilityAsync(
            _db, command.PortfolioId, command.ActorUserId, command.ActorSessionId,
            command.ActorAccessContextId, command.ActorAccessRevision, command.WorkOrderId,
            CapabilityKeys.AssignedWorkTimeMaterialsManage, now, ct);
    }

    internal static WriteLockPlan WorkOrderLock(int workOrderId) => new(
        WriteLockProtocol.WorkOrder, workOrderId);

    internal static InvalidOperationException RetiredPath() => new(
        "Technician experience mutations must use the shared write executor.");

    private static void Validate(RecordTechnicianWorkEntryCommand command)
    {
        var note = command.Note?.Trim();
        var unit = command.Unit?.Trim();
        var valid = command.PortfolioId > 0 && command.ActorUserId > 0 &&
            command.ActorSessionId != Guid.Empty && command.ActorAccessContextId > 0 &&
            command.ActorAccessRevision > 0 && command.WorkOrderId > 0 &&
            !string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey) &&
            (command.Kind switch
            {
                TechnicianWorkEntryKind.Note => !string.IsNullOrWhiteSpace(note) && note.Length <= 2000 &&
                    command.Quantity is null && command.StoredFileId is null,
                TechnicianWorkEntryKind.Time or TechnicianWorkEntryKind.Material =>
                    command.Quantity is > 0 && !string.IsNullOrWhiteSpace(unit) && unit.Length <= 40 &&
                    command.StoredFileId is null,
                TechnicianWorkEntryKind.Photo => command.StoredFileId is > 0 && command.Quantity is null,
                _ => false,
            });
        if (!valid) throw new DomainValidationException("A complete, valid technician entry is required.");
    }
}

public sealed class SendTechnicianAssignmentMessageRule
{
    public const string ResultContract = "technician-assignment-message.v1";

    private readonly RentalCommandDbContext _db;

    public SendTechnicianAssignmentMessageRule(RentalCommandDbContext db) => _db = db;

    public static TransactionalWrite<SendTechnicianAssignmentMessageCommand, SendTechnicianAssignmentMessageResult> Write(
        SendTechnicianAssignmentMessageCommand command,
        RentalCommandDbContext db)
    {
        var handler = new SendTechnicianAssignmentMessageRule(db);
        return new TransactionalWrite<SendTechnicianAssignmentMessageCommand, SendTechnicianAssignmentMessageResult>(
            "technician-assignment-message.send", WriteIdempotencyPolicy.Required, command,
            ResultContract, RecordTechnicianWorkEntryRule.WorkOrderLock(command.WorkOrderId),
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<SendTechnicianAssignmentMessageResult> ExecuteAsync(
        SendTechnicianAssignmentMessageCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0 || command.ActorSessionId == Guid.Empty ||
            command.ActorAccessContextId <= 0 || command.ActorAccessRevision <= 0 || command.WorkOrderId <= 0 ||
            string.IsNullOrWhiteSpace(command.Body) || command.Body.Trim().Length > 4000 ||
            string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A message is required.");

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var responsibility = await AssignedTechnicianCommandAuthorization.RequireResponsibilityAsync(
            _db, command.PortfolioId, command.ActorUserId, command.ActorSessionId,
            command.ActorAccessContextId, command.ActorAccessRevision, command.WorkOrderId,
            CapabilityKeys.AssignedWorkConverse, now, ct);
        var work = await _db.Set<WorkOrder>()
            .Where(item => item.Id == command.WorkOrderId && item.PortfolioId == command.PortfolioId)
            .Select(item => new { item.TenantId, item.PropertyId, item.Title, item.Status })
            .SingleAsync(ct);
        if (work.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived)
            throw new DomainValidationException(
                "Cancelled or archived work orders cannot receive technician messages.", 409);
        if (work.TenantId is null)
            throw new DomainValidationException("This assignment has no permitted tenant contact.");

        var conversation = await _db.Set<Conversation>()
            .SingleOrDefaultAsync(item => item.PortfolioId == command.PortfolioId &&
                item.WorkOrderId == command.WorkOrderId, ct);
        if (conversation is null)
        {
            conversation = new Conversation
            {
                PortfolioId = command.PortfolioId,
                TenantId = work.TenantId.Value,
                PropertyId = work.PropertyId,
                WorkOrderId = command.WorkOrderId,
                Subject = $"Maintenance: {work.Title}",
                StartedByLandlord = false,
                CreatedAt = now,
                LastMessageAt = now,
                LastMessagePreview = Preview(command.Body),
                LandlordUnreadCount = 1,
                TenantUnreadCount = 1,
            };
            _db.Add(conversation);
        }
        else
        {
            conversation.LastMessageAt = now;
            conversation.LastMessagePreview = Preview(command.Body);
            conversation.LandlordUnreadCount += 1;
            conversation.TenantUnreadCount += 1;
        }

        var message = new ConversationMessage
        {
            Conversation = conversation,
            SenderRole = ConversationSenderRole.Technician,
            Body = command.Body.Trim(),
            CreatedAt = now,
        };
        _db.Add(message);
        await context.FlushBusinessAsync(ct);
        context.UseDatabaseWallClockForAudit(now);
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(ConversationMessage), message.Id, AuditLogOperation.Created, command.ActorUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                message.ConversationId,
                WorkOrderId = command.WorkOrderId,
                Sender = ConversationSenderRole.Technician,
                ResponsibilityId = responsibility.Id,
            }), ChangeReason: "Assigned technician added an assignment conversation message."), now);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(Conversation), entityId = conversation.Id }),
            IdempotencyKey = $"technician-assignment-message:{command.DeliveryIdempotencyKey}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
        return new(conversation.Id, message.Id, now);
    }

    public Task AuthorizeReplayAsync(SendTechnicianAssignmentMessageCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RecordTechnicianWorkEntryRule.RetiredPath();

    public async Task AuthorizeAsync(SendTechnicianAssignmentMessageCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await AssignedTechnicianCommandAuthorization.RequireResponsibilityAsync(
            _db, command.PortfolioId, command.ActorUserId, command.ActorSessionId,
            command.ActorAccessContextId, command.ActorAccessRevision, command.WorkOrderId,
            CapabilityKeys.AssignedWorkConverse, now, ct);
    }

    private static string Preview(string body) => body.Trim().Length <= 280
        ? body.Trim()
        : body.Trim()[..277] + "...";
}

public sealed class MarkTechnicianAssignmentConversationReadRule
{
    public const string ResultContract = "technician-assignment-conversation-read.v1";

    private readonly RentalCommandDbContext _db;

    public MarkTechnicianAssignmentConversationReadRule(RentalCommandDbContext db) => _db = db;

    public static TransactionalWrite<MarkTechnicianAssignmentConversationReadCommand, MarkTechnicianAssignmentConversationReadResult> Write(
        MarkTechnicianAssignmentConversationReadCommand command,
        RentalCommandDbContext db)
    {
        var handler = new MarkTechnicianAssignmentConversationReadRule(db);
        return new TransactionalWrite<MarkTechnicianAssignmentConversationReadCommand, MarkTechnicianAssignmentConversationReadResult>(
            "technician-assignment-conversation.read", WriteIdempotencyPolicy.Required, command,
            ResultContract, RecordTechnicianWorkEntryRule.WorkOrderLock(command.WorkOrderId),
            handler.ExecuteAsync, handler.AuthorizeAsync);
    }

    public async Task<MarkTechnicianAssignmentConversationReadResult> ExecuteAsync(
        MarkTechnicianAssignmentConversationReadCommand command, IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (command.WorkOrderId <= 0 || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A valid assignment is required.");
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        _ = await AssignedTechnicianCommandAuthorization.RequireResponsibilityAsync(
            _db, command.PortfolioId, command.ActorUserId, command.ActorSessionId,
            command.ActorAccessContextId, command.ActorAccessRevision, command.WorkOrderId,
            CapabilityKeys.AssignedWorkConverse, now, ct);
        var conversation = await _db.Set<Conversation>().SingleOrDefaultAsync(item =>
            item.PortfolioId == command.PortfolioId && item.WorkOrderId == command.WorkOrderId, ct);
        if (conversation is null) return new(null, false);
        conversation.TechnicianUnreadCount = 0;
        return new(conversation.Id, true);
    }

    public Task AuthorizeReplayAsync(MarkTechnicianAssignmentConversationReadCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        throw RecordTechnicianWorkEntryRule.RetiredPath();

    public async Task AuthorizeAsync(MarkTechnicianAssignmentConversationReadCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        _ = await AssignedTechnicianCommandAuthorization.RequireResponsibilityAsync(
            _db, command.PortfolioId, command.ActorUserId, command.ActorSessionId,
            command.ActorAccessContextId, command.ActorAccessRevision, command.WorkOrderId,
            CapabilityKeys.AssignedWorkConverse, now, ct);
    }
}

internal static class AssignedTechnicianCommandAuthorization
{
    internal static async Task<WorkOrderResponsibility> RequireResponsibilityAsync(
        RentalCommandDbContext db, int portfolioId, int userId, Guid sessionId,
        int accessContextId, long accessRevision, int workOrderId, string capabilityKey,
        DateTime now, CancellationToken ct)
    {
        var responsibility = await db.Set<WorkOrderResponsibility>()
            .Where(item => item.PortfolioId == portfolioId && item.WorkOrderId == workOrderId &&
                item.EffectiveFromUtc <= now && (item.EffectiveToUtc == null || item.EffectiveToUtc > now) &&
                item.WorkspaceMembership != null &&
                item.WorkspaceMembership.AccessContextId == accessContextId &&
                item.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
                item.WorkspaceMembership.SuspendedAtUtc == null &&
                item.WorkspaceMembership.RevokedAtUtc == null &&
                item.WorkspaceMembership.AccessContext != null &&
                item.WorkspaceMembership.AccessContext.UserId == userId &&
                item.WorkspaceMembership.AccessContext.AccessRevision == accessRevision &&
                item.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                item.MembershipRoleAssignment != null &&
                item.MembershipRoleAssignment.Status == MembershipRoleAssignmentStatus.Active &&
                item.MembershipRoleAssignment.SuspendedAtUtc == null &&
                item.MembershipRoleAssignment.RevokedAtUtc == null &&
                item.MembershipRoleAssignment.EffectiveFromUtc <= now &&
                (item.MembershipRoleAssignment.EffectiveToUtc == null ||
                 item.MembershipRoleAssignment.EffectiveToUtc > now) &&
                item.MembershipRoleAssignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                item.MembershipRoleAssignment.RoleProfile!.Capabilities.Any(profileCapability =>
                    profileCapability.CapabilityDefinition!.Key == capabilityKey &&
                    profileCapability.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.WorkOrder) &&
                db.Set<AuthSession>().Any(session => session.Id == sessionId &&
                    session.UserId == userId && session.ActiveAccessContextId == accessContextId &&
                    session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > now))
            .SingleOrDefaultAsync(ct);
        return responsibility ?? throw new UnauthorizedAccessException(
            "The technician is not currently assigned to this work order.");
    }
}
