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
    : IAtomicCommandHandler<CreateWorkOrderCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public CreateWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        CreateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        if (!await StaffOperationAuthorization.CanManagePropertyAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                CapabilityKeys.WorkManage, _db, businessNow, securityNow, ct))
            return new(OperationMutationOutcome.NotFound, 0);

        if (!await WorkOperationValidation.ReferencesMatchAsync(
                _db, command.PortfolioId, command.PropertyId, command.UnitId,
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
            SubmittedByLabel = WorkOperationValidation.Clean(command.SubmittedByLabel),
            RequesterName = WorkOperationValidation.Clean(command.RequesterName),
            RequesterPhone = WorkOperationValidation.Clean(command.RequesterPhone),
            RequesterEmail = WorkOperationValidation.Clean(command.RequesterEmail),
            ResidentMustBePresent = command.ResidentMustBePresent,
            CallBeforeEntry = command.CallBeforeEntry,
            CallIfNotHome = command.CallIfNotHome,
            PermissionToEnter = command.PermissionToEnter,
            EntryNotes = WorkOperationValidation.Clean(command.EntryNotes),
            PetWarnings = WorkOperationValidation.Clean(command.PetWarnings),
            AccessWarnings = WorkOperationValidation.Clean(command.AccessWarnings),
            Category = command.Category.Trim(),
            Priority = command.Priority,
            Status = command.Status,
            RequestedAt = command.RequestedAtUtc ?? businessNow,
            ScheduledFor = command.ScheduledForUtc,
            ScheduledWindowEnd = command.ScheduledWindowEndUtc,
            CompletedAt = command.CompletedAtUtc,
            EstimatedCost = command.EstimatedCost,
            ActualCost = command.ActualCost,
            CreatedBy = command.CreatedBy,
            ExtractedData = command.ExtractedData,
            UpdatedAt = businessNow,
        };
        var eventNow = WorkOperationValidation.EventTimestamp(entity.RequestedAt, businessNow);
        entity.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            FromStatus = null,
            ToStatus = entity.Status,
            Kind = "Status",
            Visibility = "Public",
            ChangedByUserId = command.Actor.UserId,
            ChangedByLabel = "Staff",
            CreatedAtUtc = eventNow,
        });
        _db.Add(entity);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), 0, AuditLogOperation.Created,
            command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                command.PropertyId, command.UnitId, command.TenantId, command.LeaseManagementId,
                command.VendorId, command.Title, command.Status,
            }), ChangeReason: "Created work order."));
        await context.FlushBusinessAsync(ct);
        var appointmentSync = await WorkOrderAppointmentSync.SyncLinkedMaintenanceAppointmentAsync(
            _db, entity, command.Actor.UserId, command.DeliveryIdempotencyKey, context, businessNow, ct);
        await context.FlushBusinessAsync(ct);
        if (appointmentSync is not null)
            await WorkOrderAppointmentSync.StageLinkedAppointmentSideEffectsAsync(
                _db, appointmentSync, context, businessNow, ct);
        var snapshot = await WorkOrderSnapshot.LoadAsync(
            _db, command.PortfolioId, entity.Id, ct);
        context.StageOutbox(DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-create:{command.DeliveryIdempotencyKey}", businessNow));
        await StageTenantScheduleSmsAsync(command, context, businessNow, ct);
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        CreateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await StaffOperationAuthorization.CanManagePropertyAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                CapabilityKeys.WorkManage, _db, command.BusinessNowUtc, securityNow, ct))
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

    private async Task StageTenantScheduleSmsAsync(
        CreateWorkOrderCommand command, IAtomicCommandContext context, DateTime now, CancellationToken ct)
    {
        if (!command.TenantId.HasValue || !command.ScheduledForUtc.HasValue ||
            !command.ScheduledWindowEndUtc.HasValue || !command.ScheduledForLocal.HasValue ||
            !command.ScheduledWindowEndLocal.HasValue)
            return;

        var phone = await _db.Set<Tenant>().AsNoTracking()
            .Where(tenant => tenant.Id == command.TenantId.Value && tenant.PortfolioId == command.PortfolioId)
            .Select(tenant => tenant.Phone)
            .SingleOrDefaultAsync(ct);
        var normalizedPhone = PhoneNumber.Normalize(phone);
        if (string.IsNullOrWhiteSpace(normalizedPhone)) return;

        context.StageOutbox(new OutboxMessage
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
    : IAtomicCommandHandler<UpdateWorkOrderCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public UpdateWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        UpdateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.Validate(command);
        await WorkOrderAppointmentSync.AcquireLinkedAppointmentLocksAsync(
            _db, command.PortfolioId, command.WorkOrderId, context, ct);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await StaffOperationAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
                _db, businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        if (entity.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived)
            return new(OperationMutationOutcome.NotFound, command.WorkOrderId);

        var unitId = command.ClearUnit ? null : command.UnitId ?? entity.UnitId;
        var tenantId = command.ClearTenant ? null : command.TenantId ?? entity.TenantId;
        var managementId = command.ClearLeaseManagement
            ? null : command.LeaseManagementId ?? entity.LeaseManagementId;
        if (!await WorkOperationValidation.ReferencesMatchAsync(
                _db, command.PortfolioId, entity.PropertyId, unitId,
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
            (command.SubmittedByLabel is not null && command.SubmittedByLabel.Trim() != entity.SubmittedByLabel) ||
            (command.RequesterName is not null && command.RequesterName.Trim() != entity.RequesterName) ||
            (command.RequesterPhone is not null && command.RequesterPhone.Trim() != entity.RequesterPhone) ||
            (command.RequesterEmail is not null && command.RequesterEmail.Trim() != entity.RequesterEmail) ||
            (command.ResidentMustBePresent.HasValue && command.ResidentMustBePresent != entity.ResidentMustBePresent) ||
            (command.CallBeforeEntry.HasValue && command.CallBeforeEntry != entity.CallBeforeEntry) ||
            (command.CallIfNotHome.HasValue && command.CallIfNotHome != entity.CallIfNotHome) ||
            (command.PermissionToEnter.HasValue && command.PermissionToEnter != entity.PermissionToEnter) ||
            (command.EntryNotes is not null && command.EntryNotes.Trim() != entity.EntryNotes) ||
            (command.PetWarnings is not null && command.PetWarnings.Trim() != entity.PetWarnings) ||
            (command.AccessWarnings is not null && command.AccessWarnings.Trim() != entity.AccessWarnings) ||
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
        if (command.SubmittedByLabel is not null) entity.SubmittedByLabel = WorkOperationValidation.Clean(command.SubmittedByLabel);
        if (command.RequesterName is not null) entity.RequesterName = WorkOperationValidation.Clean(command.RequesterName);
        if (command.RequesterPhone is not null) entity.RequesterPhone = WorkOperationValidation.Clean(command.RequesterPhone);
        if (command.RequesterEmail is not null) entity.RequesterEmail = WorkOperationValidation.Clean(command.RequesterEmail);
        if (command.ResidentMustBePresent.HasValue) entity.ResidentMustBePresent = command.ResidentMustBePresent;
        if (command.CallBeforeEntry.HasValue) entity.CallBeforeEntry = command.CallBeforeEntry;
        if (command.CallIfNotHome.HasValue) entity.CallIfNotHome = command.CallIfNotHome;
        if (command.PermissionToEnter.HasValue) entity.PermissionToEnter = command.PermissionToEnter;
        if (command.EntryNotes is not null) entity.EntryNotes = WorkOperationValidation.Clean(command.EntryNotes);
        if (command.PetWarnings is not null) entity.PetWarnings = WorkOperationValidation.Clean(command.PetWarnings);
        if (command.AccessWarnings is not null) entity.AccessWarnings = WorkOperationValidation.Clean(command.AccessWarnings);
        if (command.Category is not null) entity.Category = command.Category.Trim();
        if (command.Priority.HasValue) entity.Priority = command.Priority.Value;
        if (command.Status.HasValue) entity.Status = command.Status.Value;
        if (command.RequestedAtUtc.HasValue) entity.RequestedAt = command.RequestedAtUtc.Value;
        if (command.ScheduledForUtc.HasValue) entity.ScheduledFor = command.ScheduledForUtc;
        if (command.ScheduledWindowEndUtc.HasValue) entity.ScheduledWindowEnd = command.ScheduledWindowEndUtc;
        if (command.CompletedAtUtc.HasValue) entity.CompletedAt = command.CompletedAtUtc;
        if (entity.Status == WorkOrderStatus.Completed && priorStatus != entity.Status) entity.CompletedAt ??= businessNow;
        if (command.EstimatedCost.HasValue) entity.EstimatedCost = command.EstimatedCost;
        if (command.ActualCost.HasValue) entity.ActualCost = command.ActualCost;
        WorkOperationValidation.EnsureSchedule(entity.ScheduledFor, entity.ScheduledWindowEnd);
        WorkOperationValidation.EnsureCompletedAtInRange(
            command.RequestedAtUtc.HasValue, entity.RequestedAt,
            command.ScheduledForUtc.HasValue, entity.ScheduledFor,
            command.CompletedAtUtc.HasValue, entity.CompletedAt, businessNow);
        entity.UpdatedAt = businessNow;
        var eventNow = WorkOperationValidation.EventTimestamp(entity.RequestedAt, businessNow);

        if (statusChanged)
            _db.Add(new WorkOrderStatusEvent
            {
                PortfolioId = command.PortfolioId, WorkOrderId = entity.Id,
                FromStatus = priorStatus, ToStatus = entity.Status,
                Kind = "Status", Visibility = "Public",
                Note = string.IsNullOrWhiteSpace(command.StatusNote) ? null : command.StatusNote.Trim(),
                ChangedByUserId = command.Actor.UserId, ChangedByLabel = "Staff", CreatedAtUtc = eventNow,
            });
        else if (WorkOperationValidation.EditTimelineNote(scheduleChanged, detailsChanged) is { } editNote)
            _db.Add(new WorkOrderStatusEvent
            {
                PortfolioId = command.PortfolioId, WorkOrderId = entity.Id,
                FromStatus = entity.Status, ToStatus = entity.Status,
                Kind = "Edit", Visibility = "Public", Note = editNote,
                ChangedByUserId = command.Actor.UserId, ChangedByLabel = "Staff", CreatedAtUtc = eventNow,
            });
        var appointmentSync = await WorkOrderAppointmentSync.SyncLinkedMaintenanceAppointmentAsync(
            _db, entity, command.Actor.UserId, command.DeliveryIdempotencyKey, context, businessNow, ct);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Updated,
            command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                entity.UnitId, entity.TenantId, entity.LeaseManagementId, entity.VendorId,
                entity.Title, entity.Status, entity.ScheduledFor, entity.CompletedAt,
                entity.EstimatedCost, entity.ActualCost,
            }), ChangeReason: "Updated work order."));
        await context.FlushBusinessAsync(ct);
        if (appointmentSync is not null)
            await WorkOrderAppointmentSync.StageLinkedAppointmentSideEffectsAsync(
                _db, appointmentSync, context, businessNow, ct);
        var snapshot = await WorkOrderSnapshot.LoadAsync(
            _db, command.PortfolioId, entity.Id, ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-update:{command.DeliveryIdempotencyKey}", businessNow));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        UpdateWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await StaffOperationAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
                _db, command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.WorkOrderId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order.");
    }
}

internal static class WorkOrderAppointmentSync
{
    internal static async Task AcquireLinkedAppointmentLocksAsync(
        RentalCommandDbContext db,
        int portfolioId,
        int workOrderId,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var initialAppointmentIds = await db.Set<Appointment>()
            .AsNoTracking()
            .Where(item => item.PortfolioId == portfolioId && item.WorkOrderId == workOrderId)
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync(ct);
        await WorkOrderProgressionLock.AcquireAppointmentsAsync(
            context, ct, initialAppointmentIds.Select(id => (int?)id).ToArray());
        var currentAppointmentIds = await db.Set<Appointment>()
            .AsNoTracking()
            .Where(item => item.PortfolioId == portfolioId && item.WorkOrderId == workOrderId)
            .OrderBy(item => item.Id)
            .Select(item => item.Id)
            .ToArrayAsync(ct);
        if (!initialAppointmentIds.SequenceEqual(currentAppointmentIds))
            throw new DomainValidationException("The linked appointment changed; refresh and retry.", 409);
    }

    internal static async Task<WorkOrderAppointmentSyncResult?> SyncLinkedMaintenanceAppointmentAsync(
        RentalCommandDbContext db,
        WorkOrder workOrder,
        int actorUserId,
        string deliveryIdempotencyKey,
        IAtomicCommandContext context,
        DateTime businessNow,
        CancellationToken ct)
    {
        var appointment = await db.Set<Appointment>()
            .SingleOrDefaultAsync(item =>
                item.PortfolioId == workOrder.PortfolioId &&
                item.WorkOrderId == workOrder.Id, ct);
        if (!QualifiesForTenantMaintenanceAppointment(workOrder))
        {
            if (appointment is not null &&
                appointment.Status is AppointmentStatus.Scheduled or AppointmentStatus.Confirmed)
            {
                appointment.Status = AppointmentStatus.Cancelled;
                appointment.UpdatedAt = businessNow;
                BindAppointmentAudit(
                    appointment, AuditLogOperation.Updated, actorUserId,
                    "Cancelled linked maintenance appointment.", context);
                return new(
                    appointment,
                    AppointmentTenantNotificationLifecycle.Cancelled,
                    deliveryIdempotencyKey,
                    "cancelled",
                    NotifyTenant: true);
            }

            return null;
        }

        var assignedTo = workOrder.VendorId.HasValue
            ? await db.Set<Vendor>().AsNoTracking()
                .Where(vendor => vendor.PortfolioId == workOrder.PortfolioId &&
                    vendor.Id == workOrder.VendorId.Value)
                .Select(vendor => vendor.Name)
                .SingleOrDefaultAsync(ct)
            : null;
        var status = workOrder.Status == WorkOrderStatus.Scheduled
            ? AppointmentStatus.Confirmed
            : AppointmentStatus.Scheduled;
        var notes = string.IsNullOrWhiteSpace(workOrder.TechnicianAccessInstructions)
            ? workOrder.Description
            : workOrder.TechnicianAccessInstructions;

        if (appointment is null)
        {
            appointment = new Appointment
            {
                PortfolioId = workOrder.PortfolioId,
                PropertyId = workOrder.PropertyId,
                UnitId = workOrder.UnitId,
                LeaseManagementId = workOrder.LeaseManagementId,
                TenantId = workOrder.TenantId,
                WorkOrderId = workOrder.Id,
                Title = workOrder.Title,
                Type = AppointmentType.MaintenanceVisit,
                Status = status,
                ScheduledStart = workOrder.ScheduledFor!.Value,
                ScheduledEnd = workOrder.ScheduledWindowEnd,
                AssignedTo = assignedTo,
                Notes = notes,
                CreatedAt = businessNow,
                UpdatedAt = businessNow,
            };
            db.Add(appointment);
            BindAppointmentAudit(
                appointment, AuditLogOperation.Created, actorUserId,
                "Created linked maintenance appointment.", context);
            return new(
                appointment,
                AppointmentTenantNotificationLifecycle.Scheduled,
                deliveryIdempotencyKey,
                "created",
                NotifyTenant: true);
        }

        var tenantVisibleChanged =
            appointment.PropertyId != workOrder.PropertyId ||
            appointment.UnitId != workOrder.UnitId ||
            appointment.LeaseManagementId != workOrder.LeaseManagementId ||
            appointment.TenantId != workOrder.TenantId ||
            appointment.Title != workOrder.Title ||
            appointment.Type != AppointmentType.MaintenanceVisit ||
            appointment.ScheduledStart != workOrder.ScheduledFor!.Value ||
            appointment.ScheduledEnd != workOrder.ScheduledWindowEnd ||
            appointment.AssignedTo != assignedTo ||
            appointment.Notes != notes;
        var statusChanged = appointment.Status != status;
        var changed = tenantVisibleChanged || statusChanged;
        if (!changed) return null;
        var notifyTenant = tenantVisibleChanged ||
            !IsInternalOnlyStatusPromotion(appointment.Status, status);

        appointment.PropertyId = workOrder.PropertyId;
        appointment.UnitId = workOrder.UnitId;
        appointment.LeaseManagementId = workOrder.LeaseManagementId;
        appointment.TenantId = workOrder.TenantId;
        appointment.Title = workOrder.Title;
        appointment.Type = AppointmentType.MaintenanceVisit;
        appointment.Status = status;
        appointment.ScheduledStart = workOrder.ScheduledFor!.Value;
        appointment.ScheduledEnd = workOrder.ScheduledWindowEnd;
        appointment.AssignedTo = assignedTo;
        appointment.Notes = notes;
        appointment.UpdatedAt = businessNow;
        BindAppointmentAudit(
            appointment, AuditLogOperation.Updated, actorUserId,
            "Updated linked maintenance appointment.", context);
        return new(
            appointment,
            appointment.Status == AppointmentStatus.Cancelled
                ? AppointmentTenantNotificationLifecycle.Cancelled
                : AppointmentTenantNotificationLifecycle.Updated,
            deliveryIdempotencyKey,
            "updated",
            notifyTenant);
    }

    internal static async Task StageLinkedAppointmentSideEffectsAsync(
        RentalCommandDbContext db,
        WorkOrderAppointmentSyncResult sync,
        IAtomicCommandContext context,
        DateTime businessNow,
        CancellationToken ct)
    {
        if (sync.NotifyTenant)
        {
            await AppointmentTenantNotifications.StageAsync(
                db, context, sync.Appointment, sync.Lifecycle, businessNow, ct);
        }

        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(
            sync.Appointment.PortfolioId, nameof(Appointment), sync.Appointment.Id,
            $"appointment-work-order-sync:{sync.DeliveryIdempotencyKey}",
            businessNow, operation: sync.Operation));
    }

    private static bool QualifiesForTenantMaintenanceAppointment(WorkOrder workOrder) =>
        workOrder.TenantId.HasValue &&
        workOrder.LeaseManagementId.HasValue &&
        workOrder.ScheduledFor.HasValue &&
        workOrder.Status is not WorkOrderStatus.Completed and
            not WorkOrderStatus.Cancelled and
            not WorkOrderStatus.Archived;

    private static bool IsInternalOnlyStatusPromotion(
        AppointmentStatus current,
        AppointmentStatus next) =>
        current is AppointmentStatus.Scheduled or AppointmentStatus.Confirmed &&
        next is AppointmentStatus.Scheduled or AppointmentStatus.Confirmed;

    private static void BindAppointmentAudit(
        Appointment appointment,
        AuditLogOperation operation,
        int actorUserId,
        string reason,
        IAtomicCommandContext context) =>
        context.BindSemanticAudit(appointment, new AtomicSemanticAudit(
            appointment.PortfolioId, nameof(Appointment), appointment.Id, operation,
            actorUserId, NewValues: JsonSerializer.Serialize(new
            {
                appointment.PropertyId, appointment.UnitId, appointment.TenantId,
                appointment.LeaseManagementId, appointment.WorkOrderId, appointment.Title,
                appointment.Type, appointment.Status, appointment.ScheduledStart,
                appointment.ScheduledEnd,
            }), ChangeReason: reason));
}

internal sealed record WorkOrderAppointmentSyncResult(
    Appointment Appointment,
    AppointmentTenantNotificationLifecycle Lifecycle,
    string DeliveryIdempotencyKey,
    string Operation,
    bool NotifyTenant);

public sealed class DeleteWorkOrderHandler
    : IAtomicCommandHandler<DeleteWorkOrderCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public DeleteWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        DeleteWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await StaffOperationAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.Actor, CapabilityKeys.WorkManage,
                _db, businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        entity.DeletedAt = businessNow;
        entity.UpdatedAt = businessNow;
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Deleted,
            command.Actor.UserId, ChangeReason: "Deleted work order."));
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-delete:{command.DeliveryIdempotencyKey}", businessNow, operation: "delete"));
        return new(OperationMutationOutcome.Applied, entity.Id);
    }

    public async Task AuthorizeReplayAsync(
        DeleteWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await StaffOperationAuthorization.CanManagePropertyForDeletedWorkOrderAsync(
                command.PortfolioId, command.Actor, command.WorkOrderId,
                CapabilityKeys.WorkManage, _db, command.BusinessNowUtc, securityNow, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order.");
    }
}

public sealed class CreateTenantWorkOrderHandler
    : IAtomicCommandHandler<CreateTenantWorkOrderCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public CreateTenantWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        CreateTenantWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.Validate(command);
        var businessNow = command.RequestedAtUtc;
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var relationship = await TenantWorkOrderAuthorization.CurrentRelationship(
                command, _db, businessNow, securityNow)
            .FirstOrDefaultAsync(ct);
        if (relationship is null) return new(OperationMutationOutcome.NotFound, 0);
        var entity = new WorkOrder
        {
            PortfolioId = command.PortfolioId, PropertyId = relationship.PropertyId,
            UnitId = relationship.UnitId, TenantId = relationship.TenantId,
            LeaseManagementId = relationship.LeaseManagementId,
            Title = command.Title.Trim(), Description = command.Description.Trim(),
            Category = string.IsNullOrWhiteSpace(command.Category) ? "Resident Request" : command.Category.Trim(),
            Priority = command.Priority, Status = WorkOrderStatus.New, RequestedAt = businessNow,
            CreatedBy = "Tenant", UpdatedAt = businessNow,
            SubmittedByLabel = "Resident",
            RequesterName = relationship.TenantName,
            RequesterPhone = WorkOperationValidation.Clean(command.ContactPhone) ?? relationship.TenantPhone,
            RequesterEmail = WorkOperationValidation.Clean(command.ContactEmail) ?? relationship.TenantEmail,
            ResidentMustBePresent = command.ResidentMustBePresent,
            CallBeforeEntry = command.CallBeforeEntry,
            CallIfNotHome = command.CallIfNotHome,
            PermissionToEnter = command.PermissionToEnter,
            EntryNotes = WorkOperationValidation.Clean(command.EntryNotes),
            PetWarnings = WorkOperationValidation.Clean(command.PetWarnings),
            AccessWarnings = WorkOperationValidation.Clean(command.AccessWarnings),
        };
        entity.StatusEvents.Add(new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId, FromStatus = null, ToStatus = WorkOrderStatus.New,
            Kind = "Status", Visibility = "Public",
            ChangedByUserId = command.TenantUserId, ChangedByLabel = "Tenant", CreatedAtUtc = businessNow,
        });
        _db.Add(entity);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), 0, AuditLogOperation.Created,
            command.TenantUserId, ActorLabel: "Tenant",
            NewValues: JsonSerializer.Serialize(new { relationship.LeaseManagementId, command.Title }),
            ChangeReason: "Tenant submitted maintenance request."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await WorkOrderSnapshot.LoadAsync(_db, command.PortfolioId, entity.Id, ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"tenant-work-order-create:{command.DeliveryIdempotencyKey}", businessNow));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(
        CreateTenantWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var businessNow = command.RequestedAtUtc;
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await TenantWorkOrderAuthorization.CurrentRelationship(
                command, _db, businessNow, securityNow).AnyAsync(ct))
            throw new UnauthorizedAccessException("The tenant relationship is no longer active.");
    }
}

public sealed class AddStaffWorkOrderCommentHandler
    : IAtomicCommandHandler<AddStaffWorkOrderCommentCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AddStaffWorkOrderCommentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        AddStaffWorkOrderCommentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.EnsureComment(command.Body);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var entity = await StaffOperationAuthorization.AuthorizedWorkOrdersForComment(
                command.PortfolioId, command.Actor, command.IsPrivate,
                _db, command.BusinessNowUtc, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        if (entity.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived)
            return new(OperationMutationOutcome.NotFound, command.WorkOrderId);

        var activity = AddActivity(_db, context, entity, command.PortfolioId, command.Actor.UserId, "Staff",
            "Comment", command.IsPrivate ? "Private" : "Public", command.Body, command.BusinessNowUtc);
        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Updated,
            command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                comment = true,
                visibility = command.IsPrivate ? "Private" : "Public",
            }), ChangeReason: "Added work order comment."));
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"work-order-comment:{command.DeliveryIdempotencyKey}", command.BusinessNowUtc));
        return new(
            OperationMutationOutcome.Applied,
            entity.Id,
            Receipt: WorkOperationValidation.ActivityReceipt(entity.Id, activity.Id, command.BusinessNowUtc));
    }

    public async Task AuthorizeReplayAsync(
        AddStaffWorkOrderCommentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var now = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await StaffOperationAuthorization.AuthorizedWorkOrdersForComment(
                command.PortfolioId, command.Actor, command.IsPrivate,
                _db, command.BusinessNowUtc, now, tracking: false)
            .AnyAsync(item => item.Id == command.WorkOrderId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order.");
    }

    internal static WorkOrderStatusEvent AddActivity(
        RentalCommandDbContext db,
        IAtomicCommandContext context,
        WorkOrder entity,
        int portfolioId,
        int actorUserId,
        string actorLabel,
        string kind,
        string visibility,
        string? note,
        DateTime businessNow)
    {
        entity.UpdatedAt = businessNow > entity.UpdatedAt
            ? businessNow
            : entity.UpdatedAt.AddTicks(1);
        var eventNow = WorkOperationValidation.EventTimestamp(entity.RequestedAt, businessNow);
        var activity = new WorkOrderStatusEvent
        {
            PortfolioId = portfolioId,
            WorkOrderId = entity.Id,
            FromStatus = entity.Status,
            ToStatus = entity.Status,
            Kind = kind,
            Visibility = visibility,
            Note = WorkOperationValidation.Clean(note),
            ChangedByUserId = actorUserId,
            ChangedByLabel = actorLabel,
            CreatedAtUtc = eventNow,
        };
        db.Add(activity);
        return activity;
    }
}

public sealed class AddTenantWorkOrderCommentHandler
    : IAtomicCommandHandler<AddTenantWorkOrderCommentCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AddTenantWorkOrderCommentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        AddTenantWorkOrderCommentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.EnsureComment(command.Body);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var entity = await TenantWorkOrderAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.TenantUserId, command.TenantAuthSessionId,
                command.TenantAccessContextId, command.TenantAccessRevision,
                _db, command.BusinessNowUtc, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        if (entity.Status is WorkOrderStatus.Cancelled or WorkOrderStatus.Archived)
            return new(OperationMutationOutcome.NotFound, command.WorkOrderId);

        var activity = AddStaffWorkOrderCommentHandler.AddActivity(_db, context, entity, command.PortfolioId,
            command.TenantUserId, "Tenant", "Comment", "Public", command.Body, command.BusinessNowUtc);
        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Updated,
            command.TenantUserId, ActorLabel: "Tenant",
            NewValues: JsonSerializer.Serialize(new { comment = true, visibility = "Public" }),
            ChangeReason: "Tenant added work order comment."));
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"tenant-work-order-comment:{command.DeliveryIdempotencyKey}", command.BusinessNowUtc));
        return new(
            OperationMutationOutcome.Applied,
            entity.Id,
            Receipt: WorkOperationValidation.ActivityReceipt(entity.Id, activity.Id, command.BusinessNowUtc));
    }

    public async Task AuthorizeReplayAsync(
        AddTenantWorkOrderCommentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await TenantWorkOrderAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.TenantUserId, command.TenantAuthSessionId,
                command.TenantAccessContextId, command.TenantAccessRevision,
                _db, command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.WorkOrderId, ct))
            throw new UnauthorizedAccessException("The tenant relationship is no longer active.");
    }
}

public sealed class UpdateTenantWorkOrderHandler
    : IAtomicCommandHandler<UpdateTenantWorkOrderCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public UpdateTenantWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        UpdateTenantWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.Validate(command);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var entity = await TenantWorkOrderAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.TenantUserId, command.TenantAuthSessionId,
                command.TenantAccessContextId, command.TenantAccessRevision,
                _db, command.BusinessNowUtc, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        WorkOperationValidation.EnsureTenantEditable(entity.Status);

        var changed = false;
        if (command.Title is not null)
        {
            var title = WorkOperationValidation.CleanRequired(command.Title, "A title is required.");
            changed |= title != entity.Title;
            entity.Title = title;
        }
        if (command.Description is not null)
        {
            var description = WorkOperationValidation.CleanRequired(command.Description, "A description is required.");
            changed |= description != entity.Description;
            entity.Description = description;
        }
        if (command.RequesterName is not null) changed |= ApplyClean(command.RequesterName, value => entity.RequesterName = value, entity.RequesterName);
        if (command.RequesterPhone is not null) changed |= ApplyClean(command.RequesterPhone, value => entity.RequesterPhone = value, entity.RequesterPhone);
        if (command.RequesterEmail is not null) changed |= ApplyClean(command.RequesterEmail, value => entity.RequesterEmail = value, entity.RequesterEmail);
        if (command.ResidentMustBePresent.HasValue) changed |= ApplyValue(command.ResidentMustBePresent, value => entity.ResidentMustBePresent = value, entity.ResidentMustBePresent);
        if (command.CallBeforeEntry.HasValue) changed |= ApplyValue(command.CallBeforeEntry, value => entity.CallBeforeEntry = value, entity.CallBeforeEntry);
        if (command.CallIfNotHome.HasValue) changed |= ApplyValue(command.CallIfNotHome, value => entity.CallIfNotHome = value, entity.CallIfNotHome);
        if (command.PermissionToEnter.HasValue) changed |= ApplyValue(command.PermissionToEnter, value => entity.PermissionToEnter = value, entity.PermissionToEnter);
        if (command.EntryNotes is not null) changed |= ApplyClean(command.EntryNotes, value => entity.EntryNotes = value, entity.EntryNotes);
        if (command.PetWarnings is not null) changed |= ApplyClean(command.PetWarnings, value => entity.PetWarnings = value, entity.PetWarnings);
        if (command.AccessWarnings is not null) changed |= ApplyClean(command.AccessWarnings, value => entity.AccessWarnings = value, entity.AccessWarnings);
        if (!changed)
            throw new DomainValidationException("At least one request field must change.");

        var activity = AddStaffWorkOrderCommentHandler.AddActivity(_db, context, entity, command.PortfolioId,
            command.TenantUserId, "Tenant", "Edit", "Public", "Resident updated request details.",
            command.BusinessNowUtc);
        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Updated,
            command.TenantUserId, ActorLabel: "Tenant",
            NewValues: JsonSerializer.Serialize(new { entity.Title, entity.Description }),
            ChangeReason: "Tenant updated work order request fields."));
        await context.FlushBusinessAsync(ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"tenant-work-order-update:{command.DeliveryIdempotencyKey}", command.BusinessNowUtc));
        return new(
            OperationMutationOutcome.Applied,
            entity.Id,
            Receipt: WorkOperationValidation.ActivityReceipt(entity.Id, activity.Id, command.BusinessNowUtc));
    }

    public async Task AuthorizeReplayAsync(
        UpdateTenantWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await TenantWorkOrderAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.TenantUserId, command.TenantAuthSessionId,
                command.TenantAccessContextId, command.TenantAccessRevision,
                _db, command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.WorkOrderId, ct))
            throw new UnauthorizedAccessException("The tenant relationship is no longer active.");
    }

    private static bool ApplyClean(string input, Action<string?> apply, string? current)
    {
        var value = WorkOperationValidation.Clean(input);
        if (value == current) return false;
        apply(value);
        return true;
    }

    private static bool ApplyValue<T>(T? input, Action<T?> apply, T? current)
        where T : struct
    {
        if (EqualityComparer<T?>.Default.Equals(input, current)) return false;
        apply(input);
        return true;
    }
}

public sealed class CancelTenantWorkOrderHandler
    : IAtomicCommandHandler<CancelTenantWorkOrderCommand, WorkOrderMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public CancelTenantWorkOrderHandler(RentalCommandDbContext db) => _db = db;

    public async Task<WorkOrderMutationResult> HandleAsync(
        CancelTenantWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        WorkOperationValidation.Validate(command);
        await WorkOrderAppointmentSync.AcquireLinkedAppointmentLocksAsync(
            _db, command.PortfolioId, command.WorkOrderId, context, ct);
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var entity = await TenantWorkOrderAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.TenantUserId, command.TenantAuthSessionId,
                command.TenantAccessContextId, command.TenantAccessRevision,
                _db, command.BusinessNowUtc, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.WorkOrderId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.WorkOrderId);
        WorkOperationValidation.EnsureTenantCancellable(entity.Status);

        var priorStatus = entity.Status;
        entity.Status = WorkOrderStatus.Cancelled;
        entity.UpdatedAt = command.BusinessNowUtc;
        var activity = new WorkOrderStatusEvent
        {
            PortfolioId = command.PortfolioId,
            WorkOrderId = entity.Id,
            FromStatus = priorStatus,
            ToStatus = entity.Status,
            Kind = "Status",
            Visibility = "Public",
            Note = WorkOperationValidation.Clean(command.Note) ?? "Resident cancelled request.",
            ChangedByUserId = command.TenantUserId,
            ChangedByLabel = "Tenant",
            CreatedAtUtc = WorkOperationValidation.EventTimestamp(entity.RequestedAt, command.BusinessNowUtc),
        };
        _db.Add(activity);
        context.UseDatabaseWallClockForAudit(command.BusinessNowUtc);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(WorkOrder), entity.Id, AuditLogOperation.Updated,
            command.TenantUserId, ActorLabel: "Tenant",
            NewValues: JsonSerializer.Serialize(new { entity.Status }),
            ChangeReason: "Tenant cancelled work order."));
        var appointmentSync = await WorkOrderAppointmentSync.SyncLinkedMaintenanceAppointmentAsync(
            _db, entity, command.TenantUserId, command.DeliveryIdempotencyKey, context, command.BusinessNowUtc, ct);
        await context.FlushBusinessAsync(ct);
        if (appointmentSync is not null)
            await WorkOrderAppointmentSync.StageLinkedAppointmentSideEffectsAsync(
                _db, appointmentSync, context, command.BusinessNowUtc, ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(WorkOrder), entity.Id,
            $"tenant-work-order-cancel:{command.DeliveryIdempotencyKey}", command.BusinessNowUtc));
        return new(
            OperationMutationOutcome.Applied,
            entity.Id,
            Receipt: WorkOperationValidation.ActivityReceipt(entity.Id, activity.Id, command.BusinessNowUtc));
    }

    public async Task AuthorizeReplayAsync(
        CancelTenantWorkOrderCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        await WorkOrderProgressionLock.AcquireAsync(context, ct, command.WorkOrderId);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await TenantWorkOrderAuthorization.AuthorizedWorkOrders(
                command.PortfolioId, command.TenantUserId, command.TenantAuthSessionId,
                command.TenantAccessContextId, command.TenantAccessRevision,
                _db, command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.WorkOrderId, ct))
            throw new UnauthorizedAccessException("The tenant relationship is no longer active.");
    }
}

internal static class StaffOperationAuthorization
{
    internal static async Task<bool> CanManageNullablePropertyAsync(
        int portfolioId, StaffOperationActor actor, int? propertyId, string capability,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow,
        CancellationToken ct)
    {
        if (propertyId.HasValue)
            return await CanManagePropertyAsync(portfolioId, actor, propertyId.Value, capability,
                db, businessNow, securityNow, ct);
        return await ActiveAssignments(
                portfolioId, actor, capability, db, businessNow, securityNow)
            .AnyAsync(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct);
    }

    internal static Task<bool> CanManagePropertyAsync(
        int portfolioId, StaffOperationActor actor, int propertyId, string capability,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow,
        CancellationToken ct) =>
        CanManagePropertyAsync(
            portfolioId, actor, propertyId, [capability], db, businessNow, securityNow, ct);

    internal static Task<bool> CanManagePropertyAsync(
        int portfolioId, StaffOperationActor actor, int propertyId, IReadOnlyCollection<string> capabilities,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow,
        CancellationToken ct) =>
        AuthorizedProperties(portfolioId, actor, capabilities, db, businessNow, securityNow)
            .AnyAsync(property => property.Id == propertyId, ct);

    internal static IQueryable<Property> AuthorizedProperties(
        int portfolioId, StaffOperationActor actor, string capability,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow) =>
        AuthorizedProperties(portfolioId, actor, [capability], db, businessNow, securityNow);

    internal static IQueryable<Property> AuthorizedProperties(
        int portfolioId, StaffOperationActor actor, IReadOnlyCollection<string> capabilities,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow)
    {
        return db.Set<Property>().Where(property =>
            property.PortfolioId == portfolioId &&
            db.Set<WorkspaceAccessContext>().Any(context =>
                context.Id == actor.AccessContextId && context.UserId == actor.UserId &&
                context.PortfolioId == portfolioId && context.AccessRevision == actor.AccessRevision &&
                context.Status == WorkspaceAccessContextStatus.Active && context.SuspendedAtUtc == null &&
                context.RevokedAtUtc == null &&
                db.Set<AuthSession>().AsNoTracking().Any(session =>
                    session.Id == actor.AuthSessionId && session.UserId == actor.UserId &&
                    session.ActiveAccessContextId == context.Id && session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null && session.ExpiresAtUtc > securityNow) &&
                context.Membership != null && context.Membership.Status == WorkspaceMembershipStatus.Active &&
                context.Membership.EffectiveFromUtc <= businessNow &&
                (context.Membership.EffectiveToUtc == null || context.Membership.EffectiveToUtc > businessNow) &&
                context.Membership.RoleAssignments.Any(assignment =>
                    assignment.Status == MembershipRoleAssignmentStatus.Active &&
                    assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null &&
                    assignment.EffectiveFromUtc <= businessNow &&
                    (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNow) &&
                    assignment.RoleProfile!.Capabilities.Any(item =>
                        capabilities.Contains(item.CapabilityDefinition!.Key) &&
                        item.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property) &&
                    (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties ||
                     (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties &&
                      assignment.SelectedProperties.Any(selected =>
                          selected.PropertyId == property.Id && selected.PortfolioId == portfolioId))))));
    }

    internal static IQueryable<MembershipRoleAssignment> ActiveAssignments(
        int portfolioId, StaffOperationActor actor, string capability,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow) =>
        ActiveAssignments(portfolioId, actor, [capability], db, businessNow, securityNow);

    internal static IQueryable<MembershipRoleAssignment> ActiveAssignments(
        int portfolioId, StaffOperationActor actor, IReadOnlyCollection<string> capabilities,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow)
    {
        return db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId && assignment.WorkspaceMembership != null &&
            assignment.WorkspaceMembership.AccessContext != null &&
            assignment.WorkspaceMembership.AccessContext.Id == actor.AccessContextId &&
            assignment.WorkspaceMembership.AccessContext.UserId == actor.UserId &&
            assignment.WorkspaceMembership.AccessContext.AccessRevision == actor.AccessRevision &&
            assignment.WorkspaceMembership.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
            assignment.WorkspaceMembership.AccessContext.SuspendedAtUtc == null &&
            assignment.WorkspaceMembership.AccessContext.RevokedAtUtc == null &&
            assignment.WorkspaceMembership.Status == WorkspaceMembershipStatus.Active &&
            assignment.WorkspaceMembership.EffectiveFromUtc <= businessNow &&
            (assignment.WorkspaceMembership.EffectiveToUtc == null ||
             assignment.WorkspaceMembership.EffectiveToUtc > businessNow) &&
            assignment.Status == MembershipRoleAssignmentStatus.Active && assignment.SuspendedAtUtc == null &&
            assignment.RevokedAtUtc == null && assignment.EffectiveFromUtc <= businessNow &&
            (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNow) &&
            assignment.RoleProfile!.Capabilities.Any(item =>
                capabilities.Contains(item.CapabilityDefinition!.Key) &&
                item.CapabilityDefinition.AuthorizationTargetKind == CapabilityAuthorizationTargetKind.Property) &&
            db.Set<AuthSession>().Any(session =>
                session.Id == actor.AuthSessionId && session.UserId == actor.UserId &&
                session.ActiveAccessContextId == actor.AccessContextId &&
                session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                session.ExpiresAtUtc > securityNow));
    }

    internal static IQueryable<WorkOrder> AuthorizedWorkOrders(
        int portfolioId, StaffOperationActor actor, string capability,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow, bool tracking)
    {
        var properties = AuthorizedProperties(
            portfolioId, actor, capability, db, businessNow, securityNow);
        var query = db.Set<WorkOrder>().Where(item =>
            item.PortfolioId == portfolioId && properties.Any(property => property.Id == item.PropertyId));
        return tracking ? query.AsTracking() : query.AsNoTracking();
    }

    internal static IQueryable<WorkOrder> AuthorizedWorkOrdersForComment(
        int portfolioId, StaffOperationActor actor, bool isPrivate,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow, bool tracking)
    {
        var managedProperties = AuthorizedProperties(
            portfolioId, actor, CapabilityKeys.WorkManage, db, businessNow, securityNow);
        var query = db.Set<WorkOrder>().Where(item =>
            item.PortfolioId == portfolioId &&
            (managedProperties.Any(property => property.Id == item.PropertyId) ||
             (!isPrivate &&
              db.Set<WorkspaceAccessContext>().Any(context =>
                  context.Id == actor.AccessContextId &&
                  context.UserId == actor.UserId &&
                  context.PortfolioId == item.PortfolioId &&
                  context.AccessRevision == actor.AccessRevision &&
                  context.Status == WorkspaceAccessContextStatus.Active &&
                  context.SuspendedAtUtc == null &&
                  context.RevokedAtUtc == null &&
                  context.Membership != null &&
                  context.Membership.Status == WorkspaceMembershipStatus.Active &&
                  context.Membership.SuspendedAtUtc == null &&
                  context.Membership.RevokedAtUtc == null &&
                  context.Membership.EffectiveFromUtc <= businessNow &&
                  (context.Membership.EffectiveToUtc == null ||
                   context.Membership.EffectiveToUtc > businessNow) &&
                  db.Set<AuthSession>().Any(session =>
                      session.Id == actor.AuthSessionId &&
                      session.UserId == actor.UserId &&
                      session.ActiveAccessContextId == actor.AccessContextId &&
                      session.Status == AuthSessionStatus.Active &&
                      session.RevokedAtUtc == null &&
                      session.ExpiresAtUtc > securityNow) &&
                  context.Membership.RoleAssignments.Any(assignment =>
                      assignment.Status == MembershipRoleAssignmentStatus.Active &&
                      assignment.SuspendedAtUtc == null &&
                      assignment.RevokedAtUtc == null &&
                      assignment.EffectiveFromUtc <= businessNow &&
                      (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > businessNow) &&
                      assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AssignedWorkOrders &&
                      assignment.RoleProfile!.Capabilities.Any(capability =>
                          capability.CapabilityDefinition!.Key == CapabilityKeys.AssignedWorkUpdate &&
                          capability.CapabilityDefinition.AuthorizationTargetKind ==
                              CapabilityAuthorizationTargetKind.WorkOrder) &&
                      db.Set<WorkOrderResponsibility>().Any(responsibility =>
                          responsibility.WorkOrderId == item.Id &&
                          responsibility.PortfolioId == item.PortfolioId &&
                          responsibility.WorkspaceMembershipId == context.Membership.Id &&
                          responsibility.MembershipRoleAssignmentId == assignment.Id &&
                          responsibility.EffectiveFromUtc <= businessNow &&
                          (responsibility.EffectiveToUtc == null ||
                           responsibility.EffectiveToUtc > businessNow)))))));
        return tracking ? query.AsTracking() : query.AsNoTracking();
    }

    internal static IQueryable<Appointment> AuthorizedAppointmentsByType(
        int portfolioId, StaffOperationActor actor,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow, bool tracking)
    {
        var workProperties = AuthorizedProperties(
            portfolioId, actor, CapabilityKeys.WorkManage, db, businessNow, securityNow);
        var workAllProperties = ActiveAssignments(
                portfolioId, actor, CapabilityKeys.WorkManage, db, businessNow, securityNow)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);
        var leasingProperties = AuthorizedProperties(
            portfolioId, actor, CapabilityKeys.LeasingShowingsManage, db, businessNow, securityNow);
        var leasingAllProperties = ActiveAssignments(
                portfolioId, actor, CapabilityKeys.LeasingShowingsManage,
                db, businessNow, securityNow)
            .Where(assignment => assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties);

        var query = db.Set<Appointment>().Where(item =>
            item.PortfolioId == portfolioId &&
            (item.Type == AppointmentType.MaintenanceVisit
                ? ((item.PropertyId == null && workAllProperties.Any()) ||
                   (item.PropertyId != null && workProperties.Any(property => property.Id == item.PropertyId)))
                : ((item.PropertyId == null && leasingAllProperties.Any()) ||
                   (item.PropertyId != null && leasingProperties.Any(property => property.Id == item.PropertyId)))));
        return tracking ? query.AsTracking() : query.AsNoTracking();
    }

    internal static async Task<bool> CanManagePropertyForDeletedWorkOrderAsync(
        int portfolioId, StaffOperationActor actor, int workOrderId, string capability,
        RentalCommandDbContext db, DateTime businessNow, DateTime securityNow,
        CancellationToken ct)
    {
        var propertyId = await db.Set<WorkOrder>().IgnoreQueryFilters().AsNoTracking()
            .Where(item => item.Id == workOrderId && item.PortfolioId == portfolioId)
            .Select(item => (int?)item.PropertyId).SingleOrDefaultAsync(ct);
        return propertyId.HasValue && await CanManagePropertyAsync(
            portfolioId, actor, propertyId.Value, capability, db, businessNow, securityNow, ct);
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
        ValidateTextShape(command.Title, 200, "A title is required and cannot exceed 200 characters.", required: true);
        ValidateTextShape(command.Description, 4000, "A description is required and cannot exceed 4000 characters.", required: true);
        ValidateTextShape(command.Category, 120, "Category cannot exceed 120 characters.", required: true);
        ValidateTextShape(command.TechnicianAccessInstructions, 2000, "Technician access instructions cannot exceed 2000 characters.");
        ValidateTextShape(command.SubmittedByLabel, 120, "Submitted-by label cannot exceed 120 characters.");
        ValidateContact(command.RequesterName, command.RequesterPhone, command.RequesterEmail);
        ValidateTextShape(command.EntryNotes, 2000, "Entry notes cannot exceed 2000 characters.");
        ValidateTextShape(command.PetWarnings, 2000, "Pet warnings cannot exceed 2000 characters.");
        ValidateTextShape(command.AccessWarnings, 2000, "Access warnings cannot exceed 2000 characters.");
    }

    internal static void Validate(UpdateWorkOrderCommand command)
    {
        ValidateTextShape(command.Title, 200, "A title is required and cannot exceed 200 characters.", requiredWhenProvided: true);
        ValidateTextShape(command.Description, 4000, "A description is required and cannot exceed 4000 characters.", requiredWhenProvided: true);
        ValidateTextShape(command.Category, 120, "Category cannot exceed 120 characters.", requiredWhenProvided: true);
        ValidateTextShape(command.TechnicianAccessInstructions, 2000, "Technician access instructions cannot exceed 2000 characters.");
        ValidateTextShape(command.SubmittedByLabel, 120, "Submitted-by label cannot exceed 120 characters.");
        ValidateContact(command.RequesterName, command.RequesterPhone, command.RequesterEmail);
        ValidateTextShape(command.EntryNotes, 2000, "Entry notes cannot exceed 2000 characters.");
        ValidateTextShape(command.PetWarnings, 2000, "Pet warnings cannot exceed 2000 characters.");
        ValidateTextShape(command.AccessWarnings, 2000, "Access warnings cannot exceed 2000 characters.");
    }

    internal static void Validate(CreateTenantWorkOrderCommand command)
    {
        ValidateTextShape(command.Title, 200, "A title is required and cannot exceed 200 characters.", required: true);
        ValidateTextShape(command.Description, 4000, "A description is required and cannot exceed 4000 characters.", required: true);
        ValidateTextShape(command.Category, 120, "Category cannot exceed 120 characters.");
        ValidateContact(null, command.ContactPhone, command.ContactEmail);
        ValidateTextShape(command.EntryNotes, 2000, "Entry notes cannot exceed 2000 characters.");
        ValidateTextShape(command.PetWarnings, 2000, "Pet warnings cannot exceed 2000 characters.");
        ValidateTextShape(command.AccessWarnings, 2000, "Access warnings cannot exceed 2000 characters.");
    }

    internal static void Validate(UpdateTenantWorkOrderCommand command)
    {
        ValidateTextShape(command.Title, 200, "A title is required and cannot exceed 200 characters.", requiredWhenProvided: true);
        ValidateTextShape(command.Description, 4000, "A description is required and cannot exceed 4000 characters.", requiredWhenProvided: true);
        ValidateContact(command.RequesterName, command.RequesterPhone, command.RequesterEmail);
        ValidateTextShape(command.EntryNotes, 2000, "Entry notes cannot exceed 2000 characters.");
        ValidateTextShape(command.PetWarnings, 2000, "Pet warnings cannot exceed 2000 characters.");
        ValidateTextShape(command.AccessWarnings, 2000, "Access warnings cannot exceed 2000 characters.");
    }

    internal static void Validate(CancelTenantWorkOrderCommand command) =>
        ValidateTextShape(command.Note, 2000, "Cancellation note cannot exceed 2000 characters.");

    private static void ValidateContact(string? name, string? phone, string? email)
    {
        ValidateTextShape(name, 200, "Requester name cannot exceed 200 characters.");
        ValidateTextShape(phone, 64, "Requester phone cannot exceed 64 characters.");
        ValidateTextShape(email, 320, "Requester email cannot exceed 320 characters.");
        var cleanedPhone = Clean(phone);
        if (cleanedPhone is not null && PhoneNumber.Normalize(cleanedPhone) is null)
            throw new DomainValidationException("Requester phone must include at least one digit.");
        var cleanedEmail = Clean(email);
        if (cleanedEmail is not null &&
            (!cleanedEmail.Contains('@', StringComparison.Ordinal) ||
             cleanedEmail.StartsWith("@", StringComparison.Ordinal) ||
             cleanedEmail.EndsWith("@", StringComparison.Ordinal)))
            throw new DomainValidationException("Requester email must be a valid email address.");
    }

    private static void ValidateTextShape(
        string? value,
        int maxLength,
        string message,
        bool required = false,
        bool requiredWhenProvided = false)
    {
        if (value is null)
        {
            if (required) throw new DomainValidationException(message);
            return;
        }
        var trimmed = value.Trim();
        if ((required || requiredWhenProvided) && trimmed.Length == 0)
            throw new DomainValidationException(message);
        if (trimmed.Length > maxLength)
            throw new DomainValidationException(message);
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

    internal static void EnsureComment(string body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Trim().Length > 2000)
            throw new DomainValidationException("A comment is required and cannot exceed 2000 characters.");
    }

    internal static void EnsureTenantEditable(WorkOrderStatus status)
    {
        if (status is not WorkOrderStatus.New and not WorkOrderStatus.Scheduled)
            throw new DomainValidationException("This request can only be edited before work starts.");
    }

    internal static void EnsureTenantCancellable(WorkOrderStatus status)
    {
        if (status is not WorkOrderStatus.New and not WorkOrderStatus.Scheduled)
            throw new DomainValidationException("This request can no longer be cancelled from the portal.");
    }

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static string CleanRequired(string value, string message)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new DomainValidationException(message);
        return value.Trim();
    }

    internal static DateTime EventTimestamp(DateTime requestedAtUtc, DateTime businessNowUtc)
    {
        if (businessNowUtc < requestedAtUtc)
            throw new DomainValidationException("Work-order activity time cannot precede the request time.");
        return businessNowUtc;
    }

    internal static WorkOrderMutationActivityReceipt ActivityReceipt(
        int entityId,
        int? activityId,
        DateTime committedAtUtc) =>
        new(entityId, OperationMutationOutcome.Applied, activityId, committedAtUtc);

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
        RentalCommandDbContext db, int portfolioId, int propertyId, int? unitId,
        int? tenantId, int? managementId, int? vendorId, CancellationToken ct) =>
        db.Set<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId && property.PortfolioId == portfolioId)
            .Select(property =>
                (!unitId.HasValue || db.Set<Unit>().AsNoTracking().Any(unit =>
                    unit.Id == unitId.Value && unit.PortfolioId == portfolioId && unit.PropertyId == propertyId)) &&
                (!vendorId.HasValue || db.Set<Vendor>().AsNoTracking().Any(vendor =>
                    vendor.Id == vendorId.Value && vendor.PortfolioId == portfolioId)) &&
                (!managementId.HasValue || db.Set<LeaseManagement>().AsNoTracking().Any(management =>
                    management.Id == managementId.Value && management.PortfolioId == portfolioId &&
                    management.PropertyId == propertyId && (!unitId.HasValue || management.UnitId == unitId.Value))) &&
                (!tenantId.HasValue || db.Set<LeaseManagementParty>().AsNoTracking().Any(party =>
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
    internal static async Task<WorkOrderMutationSnapshot> LoadAsync(
        RentalCommandDbContext db, int portfolioId, int id, CancellationToken ct)
    {
        return await db.Set<WorkOrder>().AsNoTracking()
            .Where(item => item.Id == id && item.PortfolioId == portfolioId)
            .Select(item => new WorkOrderMutationSnapshot(
                item.Id,
                item.PortfolioId,
                item.PropertyId,
                item.UnitId,
                item.TenantId,
                item.LeaseManagementId,
                item.VendorId,
                item.RecurringMaintenanceTaskId,
                item.Title,
                item.Description,
                item.TechnicianAccessInstructions,
                item.SubmittedByLabel,
                item.RequesterName,
                item.RequesterPhone,
                item.RequesterEmail,
                item.ResidentMustBePresent,
                item.CallBeforeEntry,
                item.CallIfNotHome,
                item.PermissionToEnter,
                item.EntryNotes,
                item.PetWarnings,
                item.AccessWarnings,
                item.Category,
                item.Priority,
                item.Status,
                item.RequestedAt,
                item.ScheduledFor,
                item.ScheduledWindowEnd,
                item.CompletedAt,
                item.EstimatedCost,
                item.ActualCost,
                item.CreatedBy,
                item.UpdatedAt,
                item.Property != null ? item.Property.Name : null,
                item.Unit != null ? item.Unit.UnitNumber : null,
                item.Vendor != null ? item.Vendor.Name : null,
                item.Tenant != null ? (item.Tenant.FirstName + " " + item.Tenant.LastName).Trim() : null))
            .SingleAsync(ct);
    }
}

internal static class TenantWorkOrderAuthorization
{
    internal sealed record Relationship(
        int TenantId,
        int LeaseManagementId,
        int PropertyId,
        int UnitId,
        string TenantName,
        string? TenantPhone,
        string? TenantEmail);

    internal static IQueryable<Relationship> CurrentRelationship(
        CreateTenantWorkOrderCommand command,
        RentalCommandDbContext db,
        DateTime businessNow,
        DateTime securityNow)
    {
        var today = DateOnly.FromDateTime(businessNow);
        return db.Set<TenantUserAccess>().AsNoTracking()
            .Where(access =>
                access.PortfolioId == command.PortfolioId &&
                access.AccessContextId == command.TenantAccessContextId &&
                access.ApplicationUserId == command.TenantUserId && access.RevokedAtUtc == null &&
                access.AccessContext != null &&
                access.AccessContext.UserId == command.TenantUserId &&
                access.AccessContext.AccessRevision == command.TenantAccessRevision &&
                access.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                access.AccessContext.SuspendedAtUtc == null && access.AccessContext.RevokedAtUtc == null &&
                db.Set<AuthSession>().AsNoTracking().Any(session =>
                    session.Id == command.TenantAuthSessionId && session.UserId == command.TenantUserId &&
                    session.ActiveAccessContextId == command.TenantAccessContextId &&
                    session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > securityNow) &&
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
                access.LeaseManagementParty.LeaseManagement.UnitId,
                (access.LeaseManagementParty.Tenant!.FirstName + " " +
                    access.LeaseManagementParty.Tenant.LastName).Trim(),
                access.LeaseManagementParty.Tenant.Phone,
                access.LeaseManagementParty.Tenant.Email));
    }

    internal static IQueryable<WorkOrder> AuthorizedWorkOrders(
        int portfolioId,
        int tenantUserId,
        Guid tenantAuthSessionId,
        int tenantAccessContextId,
        long tenantAccessRevision,
        RentalCommandDbContext db,
        DateTime businessNow,
        DateTime securityNow,
        bool tracking)
    {
        var today = DateOnly.FromDateTime(businessNow);
        var query = db.Set<WorkOrder>().Where(workOrder =>
            workOrder.PortfolioId == portfolioId &&
            workOrder.TenantId != null &&
            workOrder.LeaseManagementId != null &&
            db.Set<TenantUserAccess>().Any(access =>
                access.PortfolioId == portfolioId &&
                access.AccessContextId == tenantAccessContextId &&
                access.ApplicationUserId == tenantUserId &&
                access.RevokedAtUtc == null &&
                access.AccessContext != null &&
                access.AccessContext.UserId == tenantUserId &&
                access.AccessContext.AccessRevision == tenantAccessRevision &&
                access.AccessContext.Status == WorkspaceAccessContextStatus.Active &&
                access.AccessContext.SuspendedAtUtc == null &&
                access.AccessContext.RevokedAtUtc == null &&
                db.Set<AuthSession>().Any(session =>
                    session.Id == tenantAuthSessionId &&
                    session.UserId == tenantUserId &&
                    session.ActiveAccessContextId == tenantAccessContextId &&
                    session.Status == AuthSessionStatus.Active &&
                    session.RevokedAtUtc == null &&
                    session.ExpiresAtUtc > securityNow) &&
                access.LeaseManagementParty != null &&
                access.LeaseManagementParty.TenantId == workOrder.TenantId &&
                access.LeaseManagementParty.LeaseManagementId == workOrder.LeaseManagementId &&
                access.LeaseManagementParty.EffectiveFrom <= today &&
                (access.LeaseManagementParty.EffectiveThrough == null ||
                 access.LeaseManagementParty.EffectiveThrough >= today) &&
                access.LeaseManagementParty.LeaseManagement != null &&
                access.LeaseManagementParty.LeaseManagement.CanceledAtUtc == null &&
                access.LeaseManagementParty.LeaseManagement.PossessionReturnedAtUtc == null));
        return tracking ? query.AsTracking() : query.AsNoTracking();
    }
}
