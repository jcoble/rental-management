using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Api.Services;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicRecurringMaintenanceOperation { Create, Update, SetActive, Delete }

public sealed record AtomicRecurringMaintenanceMutationCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AtomicRecurringMaintenanceOperation Operation,
    int EntityId,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicRecurringMaintenanceMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    string? ResponseJson = null);

public sealed class AtomicRecurringMaintenanceMutationHandler
    : IAtomicCommandHandler<AtomicRecurringMaintenanceMutationCommand, AtomicRecurringMaintenanceMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicRecurringMaintenanceMutationHandler(RentalCommandDbContext db) => _db = db;

    public async Task<AtomicRecurringMaintenanceMutationResult> HandleAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.AcquireLockAsync("AuthSession", command.AuthSessionId, ct);
        await attempt.AcquireLockAsync(
            "WorkspaceAccessContext", command.AccessContextId, ct);
        await attempt.AcquireLockAsync("Portfolio", command.PortfolioId, ct);

        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        attempt.UseDatabaseWallClockForAudit(now);
        await AuthorizeAsync(command, _db, now, replay: false, ct);

        return await MutateAsync(command, attempt, now, ct);
    }

    public async Task AuthorizeReplayAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        await AuthorizeAsync(command, _db, now, replay: true, ct);
    }

    private async Task<AtomicRecurringMaintenanceMutationResult> MutateAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var db = _db;
        RecurringMaintenanceTask? entity = null;
        if (command.Operation != AtomicRecurringMaintenanceOperation.Create)
        {
            entity = await db.Set<RecurringMaintenanceTask>()
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
                    request.UnitId, request.VendorId, db, ct))
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
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, 0,
                AuditLogOperation.Created, $"Recurring maintenance task {entity.Title} created"));
        }
        else if (command.Operation == AtomicRecurringMaintenanceOperation.SetActive)
        {
            var request = Read<ToggleRecurringMaintenanceTaskActiveRequest>(command);
            entity!.IsActive = request.IsActive;
            ClearAutomationFailureState(entity);
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
                    effectiveUnitId, effectiveVendorId, db, ct))
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
            ClearAutomationFailureState(entity);
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
            db, command.PortfolioId, entity!.Id, ct);
        StageDataUpdate(attempt, command, entity.Id, now, responseJson);
        return Applied(entity.Id, responseJson);
    }

    private async Task AuthorizeAsync(
        AtomicRecurringMaintenanceMutationCommand command,
        RentalCommandDbContext db,
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
            var tasks = db.Set<RecurringMaintenanceTask>().IgnoreQueryFilters();
            propertyId = await tasks.Where(task => task.Id == command.EntityId
                    && task.PortfolioId == command.PortfolioId)
                .Select(task => (int?)task.PropertyId)
                .SingleOrDefaultAsync(ct);
        }

        var authorized = propertyId.HasValue && await AuthorizedProperties(command, db, now)
            .AnyAsync(property => property.Id == propertyId.Value, ct);
        if (!authorized)
        {
            var reason = replay
                ? "Workspace access changed. Refresh and try again."
                : "The recurring maintenance task is outside the current Team role and property scope.";
            throw new UnauthorizedAccessException(reason);
        }
    }

    private IQueryable<Property> AuthorizedProperties(
        AtomicRecurringMaintenanceMutationCommand command,
        RentalCommandDbContext db,
        DateTime now) =>
        db.Set<Property>().AsNoTracking().WhereAuthorizedForScope(
            db,
            new WorkspaceReadScope(
                command.PortfolioId,
                command.ActorUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision),
            CapabilityKeys.WorkManage,
            now);

    private Task<bool> ReferencesExistAsync(
        int portfolioId,
        int propertyId,
        int? unitId,
        int? vendorId,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        db.Set<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId
                && property.DeletedAt == null)
            .AnyAsync(property =>
                (unitId == null || db.Set<Unit>().Any(unit =>
                    unit.Id == unitId && unit.PortfolioId == portfolioId
                    && unit.PropertyId == property.Id && unit.DeletedAt == null))
                && (vendorId == null || db.Set<Vendor>().Any(vendor =>
                    vendor.Id == vendorId && vendor.PortfolioId == portfolioId
                    && vendor.DeletedAt == null)), ct);

    private async Task<string> SnapshotAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int entityId,
        CancellationToken ct)
    {
        var response = await ProjectResponse(db.Set<RecurringMaintenanceTask>()
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
            AutomationFailureAttemptCount = task.WorkerClaimAttemptCount,
            AutomationFailureReason = task.WorkerClaimLastFailureReason,
            AutomationFailureAtUtc = task.WorkerClaimLastFailureAtUtc,
            AutomationQuarantinedAtUtc = task.WorkerClaimQuarantinedAtUtc,
            CreatedAt = task.CreatedAt,
            UpdatedAt = task.UpdatedAt,
        });

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
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

    private AtomicSemanticAudit Audit(
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

    private DateTime NormalizeDate(DateTime value) =>
        DateTime.SpecifyKind(value.Date, DateTimeKind.Utc);

    private static void ClearAutomationFailureState(RecurringMaintenanceTask entity)
    {
        entity.WorkerClaimAttemptCount = 0;
        entity.WorkerClaimLastFailureReason = null;
        entity.WorkerClaimLastFailureAtUtc = null;
        entity.WorkerClaimQuarantinedAtUtc = null;
    }

    private T Read<T>(AtomicRecurringMaintenanceMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("The recurring maintenance update is invalid.");

    private void Validate(AtomicRecurringMaintenanceMutationCommand command)
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

    private AtomicRecurringMaintenanceMutationResult Missing(int entityId) =>
        new(false, false, entityId);

    private AtomicRecurringMaintenanceMutationResult Applied(
        int entityId, string? responseJson = null) =>
        new(true, true, entityId, responseJson);
}
