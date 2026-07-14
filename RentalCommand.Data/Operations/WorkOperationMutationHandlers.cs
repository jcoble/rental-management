using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Outbox;

namespace RentalCommand.Data.Operations;

public sealed class CreateWorkOrderHandler
    : IAtomicCommandHandler<CreateWorkOrderCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<CreateWorkOrderCommand>
{
    public async Task<OperationMutationResult> HandleAsync(
        CreateWorkOrderCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        WorkOperationValidation.Validate(command);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.CanManagePropertyAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                CapabilityKeys.WorkManage, attempt.Persistence, now, ct))
            return new(OperationMutationOutcome.NotFound, 0);

        if (!await WorkOperationValidation.ReferencesMatchAsync(
                attempt.Persistence, command.PortfolioId, command.PropertyId, command.UnitId,
                command.TenantId, command.LeaseManagementId, command.VendorId, ct))
            return new(OperationMutationOutcome.NotFound, 0);

        WorkOperationValidation.EnsureSchedule(command.ScheduledForUtc, command.ScheduledWindowEndUtc);
        var entity = new WorkOrder
        {
            PortfolioId = command.PortfolioId,
            PropertyId = command.PropertyId,
            UnitId = command.UnitId,
            TenantId = command.TenantId,
            LeaseManagementId = command.LeaseManagementId,
            VendorId = command.VendorId,
            Title = command.Title.Trim(),
            Description = command.Description.Trim(),
            TechnicianAccessInstructions = command.TechnicianAccessInstructions?.Trim(),
            Category = command.Category.Trim(),
            Priority = command.Priority,
            Status = command.Status,
            RequestedAt = command.RequestedAtUtc ?? now,
            ScheduledFor = command.ScheduledForUtc,
            ScheduledWindowEnd = command.ScheduledWindowEndUtc,
            CompletedAt = command.CompletedAtUtc,
            EstimatedCost = command.EstimatedCost,
            ActualCost = command.ActualCost,
            CreatedBy = command.CreatedBy,
            ExtractedData = command.ExtractedData,
            UpdatedAt = now,
        };
        entity.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            FromStatus = null,
            ToStatus = entity.Status,
            ChangedByUserId = command.Actor.UserId,
            ChangedByLabel = "Staff",
            CreatedAtUtc = now,
        });
        attempt.Persistence.Add(entity);
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), 0, AuditLogOperation.Created,
            command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                command.PropertyId, command.UnitId, command.TenantId, command.LeaseManagementId,
                command.VendorId, command.Title, command.Status,
            }), ChangeReason: "Created work order."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await WorkOrderSnapshot.LoadAsync(
            attempt.Persistence, command.PortfolioId, entity.Id, ct);
        attempt.StageOutbox(DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-create:{command.DeliveryIdempotencyKey}", now));
        await StageTenantScheduleSmsAsync(command, attempt, now, ct);
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        CreateWorkOrderCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.CanManagePropertyAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                CapabilityKeys.WorkManage, persistence, now, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this property.");
    }

    internal static OutboxMessage DataUpdate(
        int portfolioId, string entityType, int entityId, string key, DateTime now,
        string? operation = null) => new()
    {
        PortfolioId = portfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new { entityType, entityId, operation }),
        IdempotencyKey = key,
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
    };

    private static async Task StageTenantScheduleSmsAsync(
        CreateWorkOrderCommand command, IAtomicWriteAttempt attempt, DateTime now, CancellationToken ct)
    {
        if (!command.TenantId.HasValue || !command.ScheduledForUtc.HasValue ||
            !command.ScheduledWindowEndUtc.HasValue || !command.ScheduledForLocal.HasValue ||
            !command.ScheduledWindowEndLocal.HasValue)
            return;

        var phone = await attempt.Persistence.Query<Tenant>().AsNoTracking()
            .Where(tenant => tenant.Id == command.TenantId.Value && tenant.PortfolioId == command.PortfolioId)
            .Select(tenant => tenant.Phone)
            .SingleOrDefaultAsync(ct);
        var normalizedPhone = PhoneNumber.Normalize(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone)) return;

        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "sms",
            Payload = JsonSerializer.Serialize(new
            {
                to = normalizedPhone,
                message = BuildTenantScheduleSms(
                    command.Title.Trim(), command.ScheduledForLocal.Value,
                    command.ScheduledWindowEndLocal.Value),
            }),
            IdempotencyKey = OutboxIdempotency.Create(
                "work-order-schedule", command.DeliveryIdempotencyKey),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private static string BuildTenantScheduleSms(
        string title, DateTimeOffset start, DateTimeOffset end)
    {
        var text = new StringBuilder($"Maintenance scheduled for {title}: ")
            .Append(start.ToString("ddd MMM d, h:mm tt"))
            .Append(" – ")
            .Append(end.Date == start.Date
                ? end.ToString("h:mm tt")
                : end.ToString("ddd MMM d, h:mm tt"))
            .Append(". Please ensure access is available during this window.");
        return text.ToString();
    }
}

public sealed class UpdateWorkOrderHandler
    : IAtomicCommandHandler<UpdateWorkOrderCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<UpdateWorkOrderCommand>
{
    public async Task<OperationMutationResult> HandleAsync(
        UpdateWorkOrderCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var entity = await StaffOperationAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
                attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);

        var unitId = command.ClearUnit ? null : command.UnitId ?? entity.UnitId;
        var tenantId = command.ClearTenant ? null : command.TenantId ?? entity.TenantId;
        var managementId = command.ClearLeaseManagement
            ? null : command.LeaseManagementId ?? entity.LeaseManagementId;
        if (!await WorkOperationValidation.ReferencesMatchAsync(
                attempt.Persistence, command.PortfolioId, entity.PropertyId, unitId,
                tenantId, managementId, command.VendorId ?? entity.VendorId, ct))
            return new(OperationMutationOutcome.NotFound, command.WorkOrderId);

        var priorStatus = entity.Status;
        var statusChanged = command.Status.HasValue && command.Status.Value != priorStatus;
        if (statusChanged) WorkOperationValidation.EnsureStatusAssignable(command.Status!.Value);
        var scheduleChanged =
            (command.ScheduledForUtc.HasValue && command.ScheduledForUtc != entity.ScheduledFor) ||
            (command.ScheduledWindowEndUtc.HasValue &&
             command.ScheduledWindowEndUtc != entity.ScheduledWindowEnd);
        var detailsChanged =
            (command.ClearUnit && entity.UnitId.HasValue) ||
            (command.ClearTenant && entity.TenantId.HasValue) ||
            (command.ClearLeaseManagement && entity.LeaseManagementId.HasValue) ||
            (command.UnitId.HasValue && command.UnitId != entity.UnitId) ||
            (command.TenantId.HasValue && command.TenantId != entity.TenantId) ||
            (command.LeaseManagementId.HasValue && command.LeaseManagementId != entity.LeaseManagementId) ||
            (command.VendorId.HasValue && command.VendorId != entity.VendorId) ||
            (command.Title is not null && command.Title.Trim() != entity.Title) ||
            (command.Description is not null && command.Description.Trim() != entity.Description) ||
            (command.TechnicianAccessInstructions is not null &&
             command.TechnicianAccessInstructions.Trim() != entity.TechnicianAccessInstructions) ||
            (command.Category is not null && command.Category.Trim() != entity.Category) ||
            (command.Priority.HasValue && command.Priority.Value != entity.Priority) ||
            (command.EstimatedCost.HasValue && command.EstimatedCost != entity.EstimatedCost) ||
            (command.ActualCost.HasValue && command.ActualCost != entity.ActualCost);
        if (command.ClearUnit) entity.UnitId = null; else if (command.UnitId.HasValue) entity.UnitId = command.UnitId;
        if (command.ClearTenant) entity.TenantId = null; else if (command.TenantId.HasValue) entity.TenantId = command.TenantId;
        if (command.ClearLeaseManagement) entity.LeaseManagementId = null; else if (command.LeaseManagementId.HasValue) entity.LeaseManagementId = command.LeaseManagementId;
        if (command.VendorId.HasValue) entity.VendorId = command.VendorId;
        if (command.Title is not null) entity.Title = command.Title.Trim();
        if (command.Description is not null) entity.Description = command.Description.Trim();
        if (command.TechnicianAccessInstructions is not null)
            entity.TechnicianAccessInstructions = command.TechnicianAccessInstructions.Trim();
        if (command.Category is not null) entity.Category = command.Category.Trim();
        if (command.Priority.HasValue) entity.Priority = command.Priority.Value;
        if (command.Status.HasValue) entity.Status = command.Status.Value;
        if (command.RequestedAtUtc.HasValue) entity.RequestedAt = command.RequestedAtUtc.Value;
        if (command.ScheduledForUtc.HasValue) entity.ScheduledFor = command.ScheduledForUtc;
        if (command.ScheduledWindowEndUtc.HasValue) entity.ScheduledWindowEnd = command.ScheduledWindowEndUtc;
        if (command.CompletedAtUtc.HasValue) entity.CompletedAt = command.CompletedAtUtc;
        if (entity.Status == WorkOrderStatus.Completed && priorStatus != entity.Status) entity.CompletedAt ??= now;
        if (command.EstimatedCost.HasValue) entity.EstimatedCost = command.EstimatedCost;
        if (command.ActualCost.HasValue) entity.ActualCost = command.ActualCost;
        WorkOperationValidation.EnsureSchedule(entity.ScheduledFor, entity.ScheduledWindowEnd);
        WorkOperationValidation.EnsureCompletedAtInRange(
            command.RequestedAtUtc.HasValue, entity.RequestedAt,
            command.ScheduledForUtc.HasValue, entity.ScheduledFor,
            command.CompletedAtUtc.HasValue, entity.CompletedAt, now);
        entity.UpdatedAt = now;

        if (statusChanged)
            attempt.Persistence.Add(new WorkOrderStatusEvent
            {
                PortfolioId = command.PortfolioId, WorkOrderId = entity.Id,
                FromStatus = priorStatus, ToStatus = entity.Status,
                Note = string.IsNullOrWhiteSpace(command.StatusNote) ? null : command.StatusNote.Trim(),
                ChangedByUserId = command.Actor.UserId, ChangedByLabel = "Staff", CreatedAtUtc = now,
            });
        else if (WorkOperationValidation.EditTimelineNote(scheduleChanged, detailsChanged) is { } editNote)
            attempt.Persistence.Add(new WorkOrderStatusEvent
            {
                PortfolioId = command.PortfolioId, WorkOrderId = entity.Id,
                FromStatus = entity.Status, ToStatus = entity.Status, Note = editNote,
                ChangedByUserId = command.Actor.UserId, ChangedByLabel = "Staff", CreatedAtUtc = now,
            });
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Updated,
            command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                entity.UnitId, entity.TenantId, entity.LeaseManagementId, entity.VendorId,
                entity.Title, entity.Status, entity.ScheduledFor, entity.CompletedAt,
                entity.EstimatedCost, entity.ActualCost,
            }), ChangeReason: "Updated work order."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await WorkOrderSnapshot.LoadAsync(
            attempt.Persistence, command.PortfolioId, entity.Id, ct);
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-update:{command.DeliveryIdempotencyKey}", now));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        UpdateWorkOrderCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
                persistence, now, tracking: false)
            .AnyAsync(item => item.Id == command.WorkOrderId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order.");
    }
}

public sealed class DeleteWorkOrderHandler
    : IAtomicCommandHandler<DeleteWorkOrderCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<DeleteWorkOrderCommand>
{
    public async Task<OperationMutationResult> HandleAsync(
        DeleteWorkOrderCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var entity = await StaffOperationAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
                attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Deleted,
            command.Actor.UserId, ChangeReason: "Deleted work order."));
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-delete:{command.DeliveryIdempotencyKey}", now, operation: "delete"));
        return new(OperationMutationOutcome.Applied, entity.Id);
    }

    public async Task AuthorizeReplayAsync(
        DeleteWorkOrderCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.CanManagePropertyForDeletedWorkOrderAsync(
                command.PortfolioId, command.Actor, command.WorkOrderId,
                CapabilityKeys.WorkManage, persistence, now, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order.");
    }
}

public sealed class CreateTenantWorkOrderHandler
    : IAtomicCommandHandler<CreateTenantWorkOrderCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<CreateTenantWorkOrderCommand>
{
    public async Task<OperationMutationResult> HandleAsync(
        CreateTenantWorkOrderCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var relationship = await TenantWorkOrderAuthorization.CurrentRelationship(command, attempt.Persistence, now)
            .FirstOrDefaultAsync(ct);
        if (relationship is null) return new(OperationMutationOutcome.NotFound, 0);
        var entity = new WorkOrder
        {
            PortfolioId = command.PortfolioId, PropertyId = relationship.PropertyId,
            UnitId = relationship.UnitId, TenantId = relationship.TenantId,
            LeaseManagementId = relationship.LeaseManagementId,
            Title = command.Title.Trim(), Description = command.Description.Trim(),
            Category = string.IsNullOrWhiteSpace(command.Category) ? "Resident Request" : command.Category.Trim(),
            Priority = command.Priority, Status = WorkOrderStatus.New, RequestedAt = now,
            CreatedBy = "Tenant", UpdatedAt = now,
        };
        entity.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId, FromStatus = null, ToStatus = WorkOrderStatus.New,
            ChangedByUserId = command.TenantUserId, ChangedByLabel = "Tenant", CreatedAtUtc = now,
        });
        attempt.Persistence.Add(entity);
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), 0, AuditLogOperation.Created,
            command.TenantUserId, ActorLabel: "Tenant",
            NewValues: JsonSerializer.Serialize(new { relationship.LeaseManagementId, command.Title }),
            ChangeReason: "Tenant submitted maintenance request."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await WorkOrderSnapshot.LoadAsync(attempt.Persistence, command.PortfolioId, entity.Id, ct);
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"tenant-work-order-create:{command.DeliveryIdempotencyKey}", now));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        CreateTenantWorkOrderCommand command, IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await TenantWorkOrderAuthorization.CurrentRelationship(command, persistence, now).AnyAsync(ct))
            throw new UnauthorizedAccessException("The tenant relationship is no longer active.");
    }
}

internal static class StaffOperationAuthorization
{
    internal static async Task<bool> CanManageNullablePropertyAsync(
        int portfolioId, StaffOperationActor actor, int? propertyId, string capability,
        IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct)
    {
        if (propertyId.HasValue)
            return await CanManagePropertyAsync(portfolioId, actor, propertyId.Value, capability,
                persistence, now, ct);
        return await ActiveAssignments(portfolioId, actor, capability, persistence, now)
            .AnyAsync(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);
    }

    internal static Task<bool> CanManagePropertyAsync(
        int portfolioId, StaffOperationActor actor, int propertyId, string capability,
        IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct) =>
        AuthorizedProperties(portfolioId, actor, capability, persistence, now)
            .AnyAsync(property => property.Id == propertyId, ct);

    internal static IQueryable<Property> AuthorizedProperties(
        int portfolioId, StaffOperationActor actor, string capability,
        IAtomicPersistenceSession persistence, DateTime now)
    {
        return persistence.Query<Property>().Where(property =>
            property.PortfolioId == portfolioId &&
            persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == actor.AccessContextId && context.UserId == actor.UserId &&
                context.PortfolioId == portfolioId && context.AccessRevision == actor.AccessRevision &&
                context.Status == WorkspaceAccessContextStatus.Active && context.SuspendedAtUtc == null &&
                context.RevokedAtUtc == null &&
                persistence.Query<AuthSession>().Any(session =>
                    session.Id == actor.AuthSessionId && session.UserId == actor.UserId &&
                    session.ActiveAccessContextId == context.Id && session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null && session.ExpiresAtUtc > now) &&
                context.Membership != null && context.Membership.Status == WorkspaceMembershipStatus.Active &&
                context.Membership.EffectiveFromUtc <= now &&
                (context.Membership.EffectiveToUtc == null || context.Membership.EffectiveToUtc > now) &&
                context.Membership.RoleAssignments.Any(assignment =>
                    assignment.Status == MembershipRoleAssignmentStatus.Active &&
                    assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null &&
                    assignment.EffectiveFromUtc <= now &&
                    (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
                    assignment.RoleProfile!.Capabilities.Any(item =>
                        item.CapabilityDefinition!.Key == capability &&
                        item.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property) &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(selected =>
                          selected.PropertyId == property.Id && selected.PortfolioId == portfolioId))))));
    }

    internal static IQueryable<MembershipRoleAssignment> ActiveAssignments(
        int portfolioId, StaffOperationActor actor, string capability,
        IAtomicPersistenceSession persistence, DateTime now)
    {
        return persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId && assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.Id == actor.AccessContextId &&
            assignment.WorkspaceMembership.AccessContext.UserId == actor.UserId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == actor.AccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= now &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null || assignment.WorkspaceMembership.EffectiveToUtc > now) &&
            assignment.Status == MembershipRoleAssignmentStatus.Active && assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null && assignment.EffectiveFromUtc <= now &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now) &&
            assignment.RoleProfile!.Capabilities.Any(item =>
                item.CapabilityDefinition!.Key == capability &&
                item.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property) &&
            persistence.Query<AuthSession>().Any(session =>
                session.Id == actor.AuthSessionId && session.UserId == actor.UserId &&
                session.ActiveAccessContextId == actor.AccessContextId &&
                session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > now));
    }

    internal static IQueryable<WorkOrder> AuthorizedWorkOrders(
        int portfolioId, StaffOperationActor actor, string capability,
        IAtomicPersistenceSession persistence, DateTime now, bool tracking)
    {
        var properties = AuthorizedProperties(portfolioId, actor, capability, persistence, now);
        var query = persistence.Query<WorkOrder>().Where(item =>
            item.PortfolioId == portfolioId && properties.Any(property => property.Id == item.PropertyId));
        return tracking ? query : query.AsNoTracking();
    }

    internal static IQueryable<Appointment> AuthorizedAppointments(
        int portfolioId, StaffOperationActor actor, string capability,
        IAtomicPersistenceSession persistence, DateTime now, bool tracking)
    {
        var properties = AuthorizedProperties(portfolioId, actor, capability, persistence, now);
        var allProperties = ActiveAssignments(portfolioId, actor, capability, persistence, now)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        var query = persistence.Query<Appointment>().Where(item =>
            item.PortfolioId == portfolioId &&
            ((item.PropertyId == null && allProperties.Any()) ||
             (item.PropertyId != null && properties.Any(property => property.Id == item.PropertyId))));
        return tracking ? query : query.AsNoTracking();
    }

    internal static async Task<bool> CanManagePropertyForDeletedWorkOrderAsync(
        int portfolioId, StaffOperationActor actor, int workOrderId, string capability,
        IAtomicPersistenceSession persistence, DateTime now, CancellationToken ct)
    {
        var propertyId = await persistence.Query<WorkOrder>().IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.Id == workOrderId && item.PortfolioId == portfolioId)
            .Select(item => (int?)item.PropertyId).SingleOrDefaultAsync(ct);
        return propertyId.HasValue && await CanManagePropertyAsync(
            portfolioId, actor, propertyId.Value, capability, persistence, now, ct);
    }
}

internal static class WorkOperationValidation
{
    private static readonly IReadOnlySet<WorkOrderStatus> UserAssignableStatuses =
        new HashSet<WorkOrderStatus>
        {
            WorkOrderStatus.New,
            WorkOrderStatus.Scheduled,
            WorkOrderStatus.InProgress,
            WorkOrderStatus.WaitingParts,
            WorkOrderStatus.Completed,
            WorkOrderStatus.Cancelled,
        };
    private static readonly TimeSpan MaxCompletedAtFutureSkew = TimeSpan.FromDays(1);

    internal static void Validate(CreateWorkOrderCommand command)
    {
        if (command.PortfolioId <= 0 || command.PropertyId <= 0 || command.Actor.UserId <= 0 ||
            command.Actor.AuthSessionId == Guid.Empty || command.Actor.AccessContextId <= 0 ||
            command.Actor.AccessRevision <= 0 || string.IsNullOrWhiteSpace(command.Title) ||
            string.IsNullOrWhiteSpace(command.Description) || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new DomainValidationException("A complete work order is required.");
    }

    internal static void EnsureSchedule(DateTime? start, DateTime? end)
    {
        if (start.HasValue && end.HasValue && end <= start)
            throw new DomainValidationException("The arrival window end must be after its start.");
    }

    internal static void EnsureStatusAssignable(WorkOrderStatus status)
    {
        if (!UserAssignableStatuses.Contains(status))
            throw new DomainValidationException($"A work order cannot be moved to {status}.");
    }

    internal static void EnsureCompletedAtInRange(
        bool requestedProvided, DateTime effectiveRequestedAt,
        bool scheduledProvided, DateTime? effectiveScheduledFor,
        bool completedProvided, DateTime? effectiveCompletedAt, DateTime nowUtc)
    {
        if (effectiveCompletedAt is not { } completed) return;
        if (completedProvided && completed > nowUtc + MaxCompletedAtFutureSkew)
            throw new DomainValidationException("The completion date can't be in the future.");
        if (requestedProvided && completedProvided && completed < effectiveRequestedAt)
            throw new DomainValidationException(
                "The completion date can't be before the work order was requested.");
        if ((scheduledProvided || completedProvided) && effectiveScheduledFor is { } scheduled &&
            completed < scheduled)
            throw new DomainValidationException(
                "The completion date can't be before the scheduled visit.");
    }

    internal static string? EditTimelineNote(bool scheduleChanged, bool detailsChanged) =>
        (scheduleChanged, detailsChanged) switch
        {
            (true, true) => "Schedule and details updated.",
            (true, false) => "Schedule updated.",
            (false, true) => "Details updated.",
            _ => null,
        };

    internal static Task<bool> ReferencesMatchAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int propertyId, int? unitId,
        int? tenantId, int? managementId, int? vendorId, CancellationToken ct) =>
        persistence.Query<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId)
            .Select(property =>
                (!unitId.HasValue || persistence.Query<Unit>().AsNoTracking().Any(unit =>
                    unit.Id == unitId.Value && unit.PortfolioId == portfolioId && unit.PropertyId == propertyId)) &&
                (!vendorId.HasValue || persistence.Query<Vendor>().AsNoTracking().Any(vendor =>
                    vendor.Id == vendorId.Value && vendor.PortfolioId == portfolioId)) &&
                (!managementId.HasValue || persistence.Query<LeaseManagement>().AsNoTracking().Any(management =>
                    management.Id == managementId.Value && management.PortfolioId == portfolioId &&
                    management.PropertyId == propertyId && (!unitId.HasValue || management.UnitId == unitId.Value))) &&
                (!tenantId.HasValue || persistence.Query<LeaseManagementParty>().AsNoTracking().Any(party =>
                    party.TenantId == tenantId.Value && party.PortfolioId == portfolioId &&
                    party.LeaseManagement != null && party.LeaseManagement.PropertyId == propertyId &&
                    (!unitId.HasValue || party.LeaseManagement.UnitId == unitId.Value) &&
                    (!managementId.HasValue || party.LeaseManagementId == managementId.Value) &&
                    party.LeaseManagement.CanceledAtUtc == null &&
                    party.LeaseManagement.PossessionReturnedAtUtc == null)))
            .SingleOrDefaultAsync(ct);
}

internal static class WorkOrderSnapshot
{
    internal static async Task<string> LoadAsync(
        IAtomicPersistenceSession persistence, int portfolioId, int id, CancellationToken ct)
    {
        var row = await persistence.Query<WorkOrder>().AsNoTracking()
            .Where(item => item.Id == id && item.PortfolioId == portfolioId)
            .Select(item => new
            {
                item.Id, item.PortfolioId, item.PropertyId, item.UnitId, item.TenantId,
                item.LeaseManagementId, item.VendorId, item.RecurringMaintenanceTaskId,
                item.Title, item.Description, item.Category, item.Priority, item.Status,
                item.RequestedAt, item.ScheduledFor, item.ScheduledWindowEnd, item.CompletedAt,
                item.EstimatedCost, item.ActualCost, item.CreatedBy, item.UpdatedAt,
                PropertyName = item.Property != null ? item.Property.Name : null,
                UnitNumber = item.Unit != null ? item.Unit.UnitNumber : null,
                VendorName = item.Vendor != null ? item.Vendor.Name : null,
                TenantName = item.Tenant != null ? (item.Tenant.FirstName + " " + item.Tenant.LastName).Trim() : null,
            }).SingleAsync(ct);
        return JsonSerializer.Serialize(row);
    }
}

internal static class TenantWorkOrderAuthorization
{
    internal sealed record Relationship(int TenantId, int LeaseManagementId, int PropertyId, int UnitId);

    internal static IQueryable<Relationship> CurrentRelationship(
        CreateTenantWorkOrderCommand command, IAtomicPersistenceSession persistence, DateTime now)
    {
        var today = DateOnly.FromDateTime(now);
        return persistence.Query<TenantUserAccess>().AsNoTracking()
            .Where(access =>
                access.PortfolioId == command.PortfolioId &&
                access.AccessContextId == command.TenantAccessContextId &&
                access.ApplicationUserId == command.TenantUserId && access.RevokedAtUtc == null &&
                access.AccessContext != null &&
                access.AccessContext.UserId == command.TenantUserId &&
                access.AccessContext.AccessRevision == command.TenantAccessRevision &&
                access.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                access.AccessContext.SuspendedAtUtc == null && access.AccessContext.RevokedAtUtc == null &&
                persistence.Query<AuthSession>().AsNoTracking().Any(session =>
                    session.Id == command.TenantAuthSessionId && session.UserId == command.TenantUserId &&
                    session.ActiveAccessContextId == command.TenantAccessContextId &&
                    session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > now) &&
                access.LeaseManagementParty != null && access.LeaseManagementParty.LeaseManagement != null &&
                access.LeaseManagementParty.EffectiveFrom <= today &&
                (access.LeaseManagementParty.EffectiveThrough == null ||
                 access.LeaseManagementParty.EffectiveThrough >= today) &&
                access.LeaseManagementParty.LeaseManagement.CanceledAtUtc == null &&
                access.LeaseManagementParty.LeaseManagement.PossessionReturnedAtUtc == null)
            .OrderByDescending(access => access.LeaseManagementParty!.EffectiveFrom)
            .ThenByDescending(access => access.Id)
            .Select(access => new Relationship(
                access.LeaseManagementParty!.TenantId,
                access.LeaseManagementParty.LeaseManagementId,
                access.LeaseManagementParty.LeaseManagement!.PropertyId,
                access.LeaseManagementParty.LeaseManagement.UnitId));
    }
}
