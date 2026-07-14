using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicRecurringMaintenanceOperation { Create, Update, SetActive, Delete }

public sealed record AtomicRecurringMaintenanceMutationCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AtomicRecurringMaintenanceOperation Operation,
    int EntityId,
    string RequestJson,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicRecurringMaintenanceMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null) : IAtomicResultData;

public sealed class AtomicRecurringMaintenanceMutationHandler
    : IAtomicCommandHandler<AtomicRecurringMaintenanceMutationCommand, AtomicRecurringMaintenanceMutationResult>,
      IAtomicReplayAuthorizer<AtomicRecurringMaintenanceMutationCommand>
{
    public async Task<AtomicRecurringMaintenanceMutationResult> HandleAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        attempt.UseDatabaseWallClockForAudit(now);
        await AuthorizeAsync(command, attempt.Persistence, now, replay: false, ct);

        return await MutateAsync(command, attempt, now, ct);
    }

    public async Task AuthorizeReplayAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeAsync(command, persistence, now, replay: true, ct);
    }

    private static async Task<AtomicRecurringMaintenanceMutationResult> MutateAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        RecurringMaintenanceTask? entity = null;
        if (command.Operation != AtomicRecurringMaintenanceOperation.Create)
        {
            entity = await persistence.Query<RecurringMaintenanceTask>()
                .SingleOrDefaultAsync(task => task.Id == command.EntityId
                    && task.PortfolioId == command.PortfolioId && task.DeletedAt == null, ct);
            if (entity is null)
                return Missing(command.EntityId);
        }

        if (command.Operation == AtomicRecurringMaintenanceOperation.Delete)
        {
            entity!.DeletedAt = now;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, entity.Id,
                AuditLogOperation.Deleted, $"Recurring maintenance task {entity.Id} deleted"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, entity.Id, now, deleted: true);
            return Applied(entity.Id);
        }

        if (command.Operation == AtomicRecurringMaintenanceOperation.Create)
        {
            var request = Read<CreateRecurringMaintenanceTaskRequest>(command);
            if (!await ReferencesExistAsync(command.PortfolioId, request.PropertyId,
                    request.UnitId, request.VendorId, persistence, ct))
                return Missing(0);

            entity = new RecurringMaintenanceTask
            {
                PortfolioId = command.PortfolioId,
                PropertyId = request.PropertyId,
                UnitId = request.UnitId,
                VendorId = request.VendorId,
                Title = request.Title,
                Description = request.Description,
                Category = request.Category,
                RecurrenceInterval = request.RecurrenceInterval,
                NextDueDate = NormalizeDate(request.NextDueDate),
                ScheduledTime = request.ScheduledTime,
                EstimatedCost = request.EstimatedCost,
                IsActive = request.IsActive,
                Priority = request.Priority,
                CreatedAt = now,
                UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, 0,
                AuditLogOperation.Created, $"Recurring maintenance task {entity.Title} created"));
        }
        else if (command.Operation == AtomicRecurringMaintenanceOperation.SetActive)
        {
            var request = Read<ToggleRecurringMaintenanceTaskActiveRequest>(command);
            entity!.IsActive = request.IsActive;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, entity.Id,
                AuditLogOperation.Updated,
                $"Recurring maintenance task {entity.Id} {(request.IsActive ? "activated" : "deactivated")}"));
        }
        else if (command.Operation == AtomicRecurringMaintenanceOperation.Update)
        {
            var request = Read<UpdateRecurringMaintenanceTaskRequest>(command);
            var effectiveUnitId = request.UnitId ?? entity!.UnitId;
            var effectiveVendorId = request.VendorId ?? entity.VendorId;
            if (!await ReferencesExistAsync(command.PortfolioId, entity.PropertyId,
                    effectiveUnitId, effectiveVendorId, persistence, ct))
                return Missing(entity.Id);

            if (request.UnitId.HasValue) entity.UnitId = request.UnitId;
            if (request.VendorId.HasValue) entity.VendorId = request.VendorId;
            if (request.Title is not null) entity.Title = request.Title;
            if (request.Description is not null) entity.Description = request.Description;
            if (request.Category is not null) entity.Category = request.Category;
            if (request.RecurrenceInterval.HasValue)
                entity.RecurrenceInterval = request.RecurrenceInterval.Value;
            if (request.NextDueDate.HasValue)
                entity.NextDueDate = NormalizeDate(request.NextDueDate.Value);
            entity.ScheduledTime = request.ScheduledTime;
            entity.EstimatedCost = request.EstimatedCost;
            if (request.IsActive.HasValue) entity.IsActive = request.IsActive.Value;
            if (request.Priority.HasValue) entity.Priority = request.Priority.Value;
            entity.UpdatedAt = now;
            attempt.BindSemanticAudit(entity, Audit(command, entity.Id,
                AuditLogOperation.Updated, $"Recurring maintenance task {entity.Id} updated"));
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(command.Operation));
        }

        await attempt.FlushBusinessAsync(ct);
        var responseJson = await SnapshotAsync(
            persistence, command.PortfolioId, entity!.Id, ct);
        StageDataUpdate(attempt, command, entity.Id, now, responseJson);
        return Applied(entity.Id, responseJson);
    }

    private static async Task AuthorizeAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        bool replay,
        CancellationToken ct)
    {
        int? propertyId;
        if (command.Operation == AtomicRecurringMaintenanceOperation.Create)
        {
            propertyId = Read<CreateRecurringMaintenanceTaskRequest>(command).PropertyId;
        }
        else
        {
            var tasks = persistence.Query<RecurringMaintenanceTask>().IgnoreQueryFilters();
            propertyId = await tasks.Where(task => task.Id == command.EntityId
                    && task.PortfolioId == command.PortfolioId)
                .Select(task => (int?)task.PropertyId)
                .SingleOrDefaultAsync(ct);
        }

        var authorized = propertyId.HasValue && await AuthorizedProperties(command, persistence, now)
            .AnyAsync(property => property.Id == propertyId.Value, ct);
        if (!authorized)
        {
            var reason = replay
                ? "Workspace access changed. Refresh and try again."
                : "The recurring maintenance task is outside the current Team role and property scope.";
            throw new UnauthorizedAccessException(reason);
        }
    }

    private static IQueryable<Property> AuthorizedProperties(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().AsNoTracking()
            .Where(assignment =>
                assignment.PortfolioId == command.PortfolioId
                && assignment.Status == MembershipRoleAssignmentStatus.Active
                && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
                && assignment.EffectiveFromUtc <= now
                && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                && assignment.WorkspaceMembership!.AccessContextId == command.AccessContextId
                && assignment.WorkspaceMembership.PortfolioId == command.PortfolioId
                && assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active
                && assignment.WorkspaceMembership.SuspendedAtUtc == null
                && assignment.WorkspaceMembership.RevokedAtUtc == null
                && assignment.WorkspaceMembership.EffectiveFromUtc <= now
                && (assignment.WorkspaceMembership.EffectiveToUtc == null
                    || assignment.WorkspaceMembership.EffectiveToUtc > now)
                && persistence.Query<WorkspaceAccessContext>().Any(context =>
                    context.Id == command.AccessContextId && context.UserId == command.ActorUserId
                    && context.PortfolioId == command.PortfolioId
                    && context.AccessRevision == command.ExpectedAccessRevision
                    && context.Status == WorkspaceAccessContextStatus.Active
                    && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
                && persistence.Query<AuthSession>().Any(session =>
                    session.Id == command.AuthSessionId && session.UserId == command.ActorUserId
                    && session.ActiveAccessContextId == command.AccessContextId
                    && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                    && session.ExpiresAtUtc > now)
                && assignment.RoleProfile!.Capabilities.Any(grant =>
                    grant.CapabilityDefinition!.Key == CapabilityKeys.WorkManage
                    && grant.CapabilityDefinition.AuthorizationTargetKind ==
                        CapabilityAuthorizationTargetKind.Property));

        return persistence.Query<Property>().AsNoTracking().Where(property =>
            property.PortfolioId == command.PortfolioId && property.DeletedAt == null
            && assignments.Any(assignment =>
                assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                    && assignment.SelectedProperties.Any(selected =>
                        selected.PortfolioId == command.PortfolioId
                        && selected.PropertyId == property.Id))));
    }

    private static Task<bool> ReferencesExistAsync(
        int portfolioId,
        int propertyId,
        int? unitId,
        int? vendorId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        persistence.Query<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId
                && property.DeletedAt == null)
            .AnyAsync(property =>
                (unitId == null || persistence.Query<Unit>().Any(unit =>
                    unit.Id == unitId && unit.PortfolioId == portfolioId
                    && unit.PropertyId == property.Id && unit.DeletedAt == null))
                && (vendorId == null || persistence.Query<Vendor>().Any(vendor =>
                    vendor.Id == vendorId && vendor.PortfolioId == portfolioId
                    && vendor.DeletedAt == null)), ct);

    private static async Task<string> SnapshotAsync(
        IAtomicPersistenceSession persistence,
        int portfolioId,
        int entityId,
        CancellationToken ct)
    {
        var response = await ProjectResponse(persistence.Query<RecurringMaintenanceTask>()
                .AsNoTracking().Where(task => task.Id == entityId && task.PortfolioId == portfolioId))
            .SingleAsync(ct);
        return JsonSerializer.Serialize(response);
    }

    internal static IQueryable<RecurringMaintenanceTaskResponse> ProjectResponse(
        IQueryable<RecurringMaintenanceTask> query) =>
        query.Select(task => new RecurringMaintenanceTaskResponse
        {
            Id = task.Id,
            PortfolioId = task.PortfolioId,
            PropertyId = task.PropertyId,
            UnitId = task.UnitId,
            VendorId = task.VendorId,
            PropertyName = task.Property == null ? null : task.Property.Name,
            UnitNumber = task.Unit == null ? null : task.Unit.UnitNumber,
            VendorName = task.Vendor == null ? null : task.Vendor.Name,
            Title = task.Title,
            Description = task.Description,
            Category = task.Category,
            RecurrenceInterval = task.RecurrenceInterval,
            NextDueDate = task.NextDueDate,
            ScheduledTime = task.ScheduledTime,
            EstimatedCost = task.EstimatedCost,
            MonthlyEstimatedCost = task.EstimatedCost == null
                ? null
                : task.RecurrenceInterval == RecurrenceInterval.Weekly
                    ? task.EstimatedCost.Value * 52m / 12m
                    : task.RecurrenceInterval == RecurrenceInterval.Monthly
                        ? task.EstimatedCost.Value
                        : task.RecurrenceInterval == RecurrenceInterval.Quarterly
                            ? task.EstimatedCost.Value / 3m
                            : task.RecurrenceInterval == RecurrenceInterval.SemiAnnually
                                ? task.EstimatedCost.Value / 6m
                                : task.RecurrenceInterval == RecurrenceInterval.Annually
                                    ? task.EstimatedCost.Value / 12m
                                    : task.EstimatedCost.Value,
            GeneratedWorkOrderCount = task.WorkOrders.Count,
            LastGeneratedWorkOrderId = task.WorkOrders
                .OrderByDescending(workOrder => workOrder.RequestedAt)
                .ThenByDescending(workOrder => workOrder.Id)
                .Select(workOrder => (int?)workOrder.Id)
                .FirstOrDefault(),
            LastGeneratedAtUtc = task.LastGeneratedAtUtc,
            IsActive = task.IsActive,
            Priority = task.Priority,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
        });

    private static void StageDataUpdate(
        IAtomicWriteAttempt attempt,
        AtomicRecurringMaintenanceMutationCommand command,
        int entityId,
        DateTime now,
        string? responseJson = null,
        bool deleted = false)
    {
        object data = responseJson is null
            ? new { }
            : JsonSerializer.Deserialize<JsonElement>(responseJson);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(RecurringMaintenanceTask),
                entityId,
                operation = deleted ? "delete" : "update",
                data,
            }),
            IdempotencyKey = $"recurring-maintenance:{command.PortfolioId}:{command.AccessContextId}:" +
                $"{command.Operation}:{entityId}:{command.DeliveryIdempotencyKey}:data-update",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    private static AtomicSemanticAudit Audit(
        AtomicRecurringMaintenanceMutationCommand command,
        int entityId,
        AuditLogOperation operation,
        string reason) => new(
            command.PortfolioId,
            nameof(RecurringMaintenanceTask),
            entityId,
            operation,
            UserId: command.ActorUserId,
            ChangeReason: reason);

    private static DateTime NormalizeDate(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static T Read<T>(AtomicRecurringMaintenanceMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("Recurring maintenance mutation request payload is invalid.");

    private static void Validate(AtomicRecurringMaintenanceMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0
            || (command.Operation != AtomicRecurringMaintenanceOperation.Create && command.EntityId <= 0)
            || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 128)
            throw new ArgumentException("Recurring maintenance atomic command is invalid.");
    }

    private static AtomicRecurringMaintenanceMutationResult Missing(int entityId) =>
        new(false, false, entityId);

    private static AtomicRecurringMaintenanceMutationResult Applied(
        int entityId, string? responseJson = null) =>
        new(true, true, entityId, responseJson);
}
