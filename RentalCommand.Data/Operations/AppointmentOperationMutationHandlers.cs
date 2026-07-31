using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Navigation;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class CreateAppointmentHandler
    : IAtomicCommandHandler<CreateAppointmentCommand, OperationMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public CreateAppointmentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<OperationMutationResult> HandleAsync(
        CreateAppointmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var capability = AppointmentOperationValidation.ManageCapability(command.Type);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                capability, _db, businessNow, securityNow, ct))
            return new(OperationMutationOutcome.NotFound, 0);
        if (!AppointmentOperationValidation.ValidRange(command.ScheduledStartUtc, command.ScheduledEndUtc))
            throw new DomainValidationException("The end time must be after the start time.");
        if (!await AppointmentOperationValidation.ReferencesMatchAsync(
                _db, command.PortfolioId, command.PropertyId, command.UnitId,
                command.LeaseManagementId, command.RentalApplicationId, command.TenantId, ct))
            return new(OperationMutationOutcome.NotFound, 0);
        if (!await AppointmentOperationValidation.WorkOrderMatchesAsync(
                _db, command.PortfolioId, command.Actor, command.WorkOrderId,
                command.PropertyId, command.UnitId, command.TenantId, businessNow, securityNow, ct))
            return new(OperationMutationOutcome.NotFound, 0);

        var entity = new Appointment
        {
            PortfolioId = command.PortfolioId, PropertyId = command.PropertyId, UnitId = command.UnitId,
            LeaseManagementId = command.LeaseManagementId, RentalApplicationId = command.RentalApplicationId,
            TenantId = command.TenantId, WorkOrderId = command.WorkOrderId,
            Title = command.Title.Trim(), ProspectName = Clean(command.ProspectName),
            ProspectEmail = Clean(command.ProspectEmail), Type = command.Type, Status = command.Status,
            ScheduledStart = command.ScheduledStartUtc, ScheduledEnd = command.ScheduledEndUtc,
            AssignedTo = Clean(command.AssignedTo), Notes = Clean(command.Notes),
            CreatedAt = businessNow, UpdatedAt = businessNow,
        };
        _db.Add(entity);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, Audit(command, entity, AuditLogOperation.Created, 0));
        await context.FlushBusinessAsync(ct);
        var snapshot = await AppointmentSnapshot.LoadAsync(_db, command.PortfolioId, entity.Id, ct);
        await AppointmentTenantNotifications.StageAsync(
            _db, context, entity, AppointmentTenantNotificationLifecycle.Scheduled, businessNow, ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(Appointment), entity.Id,
            $"appointment-create:{command.DeliveryIdempotencyKey}", businessNow));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(CreateAppointmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var capability = AppointmentOperationValidation.ManageCapability(command.Type);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(command.PortfolioId, command.Actor,
                command.PropertyId, capability, _db, command.BusinessNowUtc, securityNow, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
        if (!await AppointmentOperationValidation.WorkOrderMatchesAsync(
                _db, command.PortfolioId, command.Actor, command.WorkOrderId,
                command.PropertyId, command.UnitId, command.TenantId,
                command.BusinessNowUtc, securityNow, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order appointment.");
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    internal static AtomicSemanticAudit Audit(CreateAppointmentCommand command, Appointment entity,
        AuditLogOperation operation, int id) => new(command.PortfolioId, nameof(Appointment), id, operation,
        command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
        {
            entity.PropertyId, entity.UnitId, entity.Title, entity.Type, entity.Status,
            entity.ScheduledStart, entity.ScheduledEnd, entity.WorkOrderId,
        }), ChangeReason: operation == AuditLogOperation.Created ? "Created appointment." : "Updated appointment.");
}

public sealed class UpdateAppointmentHandler
    : IAtomicCommandHandler<UpdateAppointmentCommand, OperationMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public UpdateAppointmentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<OperationMutationResult> HandleAsync(
        UpdateAppointmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await StaffOperationAuthorization.AuthorizedAppointmentsByType(
                command.PortfolioId, command.Actor, _db,
                businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.AppointmentId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        var destinationProperty = command.PropertyId ?? entity.PropertyId;
        var destinationCapability = AppointmentOperationValidation.ManageCapability(command.Type ?? entity.Type);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(command.PortfolioId, command.Actor,
                destinationProperty, destinationCapability,
                _db, businessNow, securityNow, ct))
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        var effectiveUnit = command.UnitId ?? entity.UnitId;
        var effectiveManagement = command.LeaseManagementId ?? entity.LeaseManagementId;
        var effectiveApplication = command.RentalApplicationId ?? entity.RentalApplicationId;
        var effectiveTenant = command.TenantId ?? entity.TenantId;
        var effectiveWorkOrder = command.WorkOrderId ?? entity.WorkOrderId;
        if (!await AppointmentOperationValidation.ReferencesMatchAsync(_db, command.PortfolioId,
                destinationProperty, effectiveUnit, effectiveManagement, effectiveApplication, effectiveTenant, ct))
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        if (!await AppointmentOperationValidation.WorkOrderMatchesAsync(
                _db, command.PortfolioId, command.Actor, effectiveWorkOrder,
                destinationProperty, effectiveUnit, effectiveTenant, businessNow, securityNow, ct))
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);

        var hasChanges = HasChanges(command, entity);
        if (command.PropertyId.HasValue) entity.PropertyId = command.PropertyId;
        if (command.UnitId.HasValue) entity.UnitId = command.UnitId;
        if (command.LeaseManagementId.HasValue) entity.LeaseManagementId = command.LeaseManagementId;
        if (command.RentalApplicationId.HasValue) entity.RentalApplicationId = command.RentalApplicationId;
        if (command.TenantId.HasValue) entity.TenantId = command.TenantId;
        if (command.WorkOrderId.HasValue) entity.WorkOrderId = command.WorkOrderId;
        if (command.Title is not null) entity.Title = command.Title.Trim();
        if (command.ProspectName is not null) entity.ProspectName = CreateAppointmentHandler.Clean(command.ProspectName);
        if (command.ProspectEmail is not null) entity.ProspectEmail = CreateAppointmentHandler.Clean(command.ProspectEmail);
        if (command.Type.HasValue) entity.Type = command.Type.Value;
        if (command.Status.HasValue) entity.Status = command.Status.Value;
        if (command.ScheduledStartUtc.HasValue) entity.ScheduledStart = command.ScheduledStartUtc.Value;
        if (command.ScheduledEndUtc.HasValue) entity.ScheduledEnd = command.ScheduledEndUtc;
        if (command.AssignedTo is not null) entity.AssignedTo = CreateAppointmentHandler.Clean(command.AssignedTo);
        if (command.Notes is not null) entity.Notes = CreateAppointmentHandler.Clean(command.Notes);
        if (!AppointmentOperationValidation.ValidRange(entity.ScheduledStart, entity.ScheduledEnd))
            throw new DomainValidationException("The end time must be after the start time.");
        if (!hasChanges)
        {
            var currentSnapshot = await AppointmentSnapshot.LoadAsync(
                _db, command.PortfolioId, entity.Id, ct);
            return new(OperationMutationOutcome.Applied, entity.Id, currentSnapshot);
        }

        entity.UpdatedAt = businessNow;
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(command.PortfolioId, nameof(Appointment), entity.Id,
            AuditLogOperation.Updated, command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                entity.PropertyId, entity.UnitId, entity.Title, entity.Type, entity.Status,
                entity.ScheduledStart, entity.ScheduledEnd, entity.WorkOrderId,
            }), ChangeReason: "Updated appointment."));
        await context.FlushBusinessAsync(ct);
        var snapshot = await AppointmentSnapshot.LoadAsync(_db, command.PortfolioId, entity.Id, ct);
        await AppointmentTenantNotifications.StageAsync(
            _db,
            context,
            entity,
            entity.Status == AppointmentStatus.Cancelled
                ? AppointmentTenantNotificationLifecycle.Cancelled
                : AppointmentTenantNotificationLifecycle.Updated,
            businessNow,
            ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(Appointment), entity.Id,
            $"appointment-update:{command.DeliveryIdempotencyKey}", businessNow));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(UpdateAppointmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var current = await StaffOperationAuthorization.AuthorizedAppointmentsByType(
                command.PortfolioId, command.Actor, _db,
                command.BusinessNowUtc, securityNow, tracking: false)
            .Where(item => item.Id == command.AppointmentId)
            .Select(item => new { item.PropertyId, item.UnitId, item.TenantId, item.WorkOrderId, item.Type })
            .SingleOrDefaultAsync(ct);
        if (current is null)
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
        var destinationCapability = AppointmentOperationValidation.ManageCapability(command.Type ?? current.Type);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(command.PortfolioId, command.Actor,
                command.PropertyId ?? current.PropertyId, destinationCapability,
                _db, command.BusinessNowUtc, securityNow, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
        if (!await AppointmentOperationValidation.WorkOrderMatchesAsync(
                _db, command.PortfolioId, command.Actor, command.WorkOrderId ?? current.WorkOrderId,
                command.PropertyId ?? current.PropertyId, command.UnitId ?? current.UnitId,
                command.TenantId ?? current.TenantId, command.BusinessNowUtc, securityNow, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this work order appointment.");
    }

    private static bool HasChanges(UpdateAppointmentCommand command, Appointment entity)
    {
        return (command.PropertyId.HasValue && entity.PropertyId != command.PropertyId.Value)
            || (command.UnitId.HasValue && entity.UnitId != command.UnitId.Value)
            || (command.LeaseManagementId.HasValue && entity.LeaseManagementId != command.LeaseManagementId.Value)
            || (command.RentalApplicationId.HasValue && entity.RentalApplicationId != command.RentalApplicationId.Value)
            || (command.TenantId.HasValue && entity.TenantId != command.TenantId.Value)
            || (command.WorkOrderId.HasValue && entity.WorkOrderId != command.WorkOrderId.Value)
            || (command.Title is not null && entity.Title != command.Title.Trim())
            || (command.ProspectName is not null
                && entity.ProspectName != CreateAppointmentHandler.Clean(command.ProspectName))
            || (command.ProspectEmail is not null
                && entity.ProspectEmail != CreateAppointmentHandler.Clean(command.ProspectEmail))
            || (command.Type.HasValue && entity.Type != command.Type.Value)
            || (command.Status.HasValue && entity.Status != command.Status.Value)
            || (command.ScheduledStartUtc.HasValue && entity.ScheduledStart != command.ScheduledStartUtc.Value)
            || (command.ScheduledEndUtc.HasValue && entity.ScheduledEnd != command.ScheduledEndUtc.Value)
            || (command.AssignedTo is not null
                && entity.AssignedTo != CreateAppointmentHandler.Clean(command.AssignedTo))
            || (command.Notes is not null && entity.Notes != CreateAppointmentHandler.Clean(command.Notes));
    }
}

public sealed class DeleteAppointmentHandler
    : IAtomicCommandHandler<DeleteAppointmentCommand, OperationMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public DeleteAppointmentHandler(RentalCommandDbContext db) => _db = db;

    public async Task<OperationMutationResult> HandleAsync(DeleteAppointmentCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var securityNow = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNow = command.BusinessNowUtc;
        var entity = await StaffOperationAuthorization.AuthorizedAppointmentsByType(
                command.PortfolioId, command.Actor, _db,
                businessNow, securityNow, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.AppointmentId, ct);
        if (entity is null || entity.PropertyId != command.ExpectedPropertyId)
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        _db.Remove(entity);
        context.UseDatabaseWallClockForAudit(businessNow);
        context.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(Appointment), entity.Id, AuditLogOperation.Deleted,
            command.Actor.UserId, ChangeReason: "Deleted appointment."));
        await AppointmentTenantNotifications.StageAsync(
            _db, context, entity, AppointmentTenantNotificationLifecycle.Cancelled, businessNow, ct);
        context.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(Appointment), entity.Id,
            $"appointment-delete:{command.DeliveryIdempotencyKey}", businessNow, operation: "delete"));
        return new(OperationMutationOutcome.Applied, entity.Id);
    }

    public async Task AuthorizeReplayAsync(DeleteAppointmentCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var securityNow = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await StaffOperationAuthorization.AuthorizedAppointmentsByType(
                command.PortfolioId, command.Actor, _db,
                command.BusinessNowUtc, securityNow, tracking: false)
            .AnyAsync(item => item.Id == command.AppointmentId &&
                item.PropertyId == command.ExpectedPropertyId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
    }
}

internal enum AppointmentTenantNotificationLifecycle
{
    Scheduled,
    Updated,
    Cancelled,
}

internal static class AppointmentTenantNotifications
{
    internal static async Task StageAsync(
        RentalCommandDbContext db,
        IAtomicCommandContext commandContext,
        Appointment appointment,
        AppointmentTenantNotificationLifecycle lifecycle,
        DateTime now,
        CancellationToken ct)
    {
        if (appointment.TenantId is null)
        {
            return;
        }

        var notificationType = Type(lifecycle);
        var businessDate = DateOnly.FromDateTime(now);
        var recipients = await (
                from access in db.Set<TenantUserAccess>().AsNoTracking()
                join context in db.Set<WorkspaceAccessContext>().AsNoTracking()
                    on new { access.AccessContextId, access.PortfolioId }
                    equals new { AccessContextId = context.Id, context.PortfolioId }
                join portfolio in db.Set<Portfolio>().AsNoTracking()
                    on access.PortfolioId equals portfolio.Id
                join party in db.Set<LeaseManagementParty>().AsNoTracking()
                    on new { LeaseManagementPartyId = access.LeaseManagementPartyId, access.PortfolioId }
                    equals new { LeaseManagementPartyId = party.Id, party.PortfolioId }
                join relationship in db.Set<LeaseManagement>().AsNoTracking()
                    on new { party.LeaseManagementId, party.PortfolioId }
                    equals new { LeaseManagementId = relationship.Id, relationship.PortfolioId }
                where access.PortfolioId == appointment.PortfolioId
                    && access.RevokedAtUtc == null
                    && context.UserId == access.ApplicationUserId
                    && context.Status == WorkspaceAccessContextStatus.Active
                    && context.SuspendedAtUtc == null
                    && context.RevokedAtUtc == null
                    && party.TenantId == appointment.TenantId.Value
                    && party.EffectiveFrom <= businessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= businessDate)
                    && (!appointment.LeaseManagementId.HasValue ||
                        party.LeaseManagementId == appointment.LeaseManagementId.Value)
                    && (!appointment.PropertyId.HasValue || relationship.PropertyId == appointment.PropertyId.Value)
                    && (!appointment.UnitId.HasValue || relationship.UnitId == appointment.UnitId.Value)
                    && access.AccessContextId == (
                        from candidateAccess in db.Set<TenantUserAccess>().AsNoTracking()
                        join candidateContext in db.Set<WorkspaceAccessContext>().AsNoTracking()
                            on new { candidateAccess.AccessContextId, candidateAccess.PortfolioId }
                            equals new { AccessContextId = candidateContext.Id, candidateContext.PortfolioId }
                        join candidateParty in db.Set<LeaseManagementParty>().AsNoTracking()
                            on new
                            {
                                LeaseManagementPartyId = candidateAccess.LeaseManagementPartyId,
                                candidateAccess.PortfolioId,
                            }
                            equals new
                            {
                                LeaseManagementPartyId = candidateParty.Id,
                                candidateParty.PortfolioId,
                            }
                        join candidateRelationship in db.Set<LeaseManagement>().AsNoTracking()
                            on new { candidateParty.LeaseManagementId, candidateParty.PortfolioId }
                            equals new
                            {
                                LeaseManagementId = candidateRelationship.Id,
                                candidateRelationship.PortfolioId,
                            }
                        where candidateAccess.PortfolioId == access.PortfolioId
                            && candidateAccess.ApplicationUserId == access.ApplicationUserId
                            && candidateAccess.RevokedAtUtc == null
                            && candidateContext.UserId == candidateAccess.ApplicationUserId
                            && candidateContext.Status == WorkspaceAccessContextStatus.Active
                            && candidateContext.SuspendedAtUtc == null
                            && candidateContext.RevokedAtUtc == null
                            && candidateParty.TenantId == party.TenantId
                            && candidateParty.EffectiveFrom <= businessDate
                            && (candidateParty.EffectiveThrough == null ||
                                candidateParty.EffectiveThrough >= businessDate)
                            && (!appointment.LeaseManagementId.HasValue ||
                                candidateParty.LeaseManagementId == appointment.LeaseManagementId.Value)
                            && (!appointment.PropertyId.HasValue ||
                                candidateRelationship.PropertyId == appointment.PropertyId.Value)
                            && (!appointment.UnitId.HasValue ||
                                candidateRelationship.UnitId == appointment.UnitId.Value)
                        orderby candidateAccess.AccessContextId
                        select candidateAccess.AccessContextId).First()
                orderby context.UserId
                select new AppointmentTenantNotificationRecipient(
                    context.UserId,
                    access.AccessContextId,
                    context.AccessRevision,
                    portfolio.TimeZone))
            .TagWith("YS-187 appointment lifecycle tenant notification recipients")
            .ToListAsync(ct);

        if (recipients.Count == 0)
        {
            return;
        }

        var notifications = recipients.Select(recipient => new Notification
        {
            PortfolioId = appointment.PortfolioId,
            UserId = recipient.UserId,
            Type = notificationType,
            Title = Title(lifecycle),
            Message = Message(lifecycle, appointment, recipient.TimeZoneId),
            Severity = lifecycle == AppointmentTenantNotificationLifecycle.Cancelled ? "Warning" : "Info",
            NavigationExperience = NavigationExperience.Tenant,
            NavigationDestination = NavigationDestination.Home,
            NavigationAccessContextId = recipient.AccessContextId,
            NavigationAccessRevision = recipient.AccessRevision,
            NavigationAction = NavigationAction.Open,
            NavigationExpiresAtUtc = now.AddDays(7),
            NavigationFallbackDestination = NavigationDestination.Home,
            RelatedEntityType = nameof(Appointment),
            RelatedEntityId = appointment.Id,
            CreatedAt = now,
        }).ToList();

        db.AddRange(notifications);
        await commandContext.FlushBusinessAsync(ct);
    }

    private static string Type(AppointmentTenantNotificationLifecycle lifecycle) => lifecycle switch
    {
        AppointmentTenantNotificationLifecycle.Scheduled => "TenantAppointmentScheduled",
        AppointmentTenantNotificationLifecycle.Updated => "TenantAppointmentUpdated",
        AppointmentTenantNotificationLifecycle.Cancelled => "TenantAppointmentCancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown appointment notification lifecycle."),
    };

    private static string Title(AppointmentTenantNotificationLifecycle lifecycle) => lifecycle switch
    {
        AppointmentTenantNotificationLifecycle.Scheduled => "Appointment scheduled",
        AppointmentTenantNotificationLifecycle.Updated => "Appointment updated",
        AppointmentTenantNotificationLifecycle.Cancelled => "Appointment canceled",
        _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown appointment notification lifecycle."),
    };

    private static string Message(
        AppointmentTenantNotificationLifecycle lifecycle,
        Appointment appointment,
        string timeZoneId)
    {
        var scheduled = FormatScheduledStart(appointment.ScheduledStart, timeZoneId);
        return lifecycle switch
        {
            AppointmentTenantNotificationLifecycle.Scheduled =>
                $"Your appointment \"{appointment.Title}\" is scheduled for {scheduled}.",
            AppointmentTenantNotificationLifecycle.Updated =>
                $"Your appointment \"{appointment.Title}\" was updated. It is scheduled for {scheduled}.",
            AppointmentTenantNotificationLifecycle.Cancelled =>
                $"Your appointment \"{appointment.Title}\" was canceled.",
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycle), lifecycle, "Unknown appointment notification lifecycle."),
        };
    }

    private static string FormatScheduledStart(DateTime scheduledStartUtc, string timeZoneId)
    {
        var zone = ResolveTimeZone(timeZoneId);
        var utc = scheduledStartUtc.Kind == DateTimeKind.Utc
            ? scheduledStartUtc
            : DateTime.SpecifyKind(scheduledStartUtc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(utc, zone);
        return string.Format(
            CultureInfo.InvariantCulture,
            "{0:MMM d, yyyy 'at' h:mm tt} {1} ({2})",
            local,
            zone.Id,
            FormatOffset(zone.GetUtcOffset(new DateTimeOffset(utc))));
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
            return TimeZoneInfo.Utc;

        return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    private static string FormatOffset(TimeSpan offset) =>
        string.Format(
            CultureInfo.InvariantCulture,
            "UTC{0}{1:00}:{2:00}",
            offset < TimeSpan.Zero ? '-' : '+',
            Math.Abs(offset.Hours),
            Math.Abs(offset.Minutes));

    private sealed record AppointmentTenantNotificationRecipient(
        int UserId,
        int AccessContextId,
        long AccessRevision,
        string TimeZoneId);
}

internal static class AppointmentOperationValidation
{
    internal static void Validate(CreateAppointmentCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        ArgumentException.ThrowIfNullOrWhiteSpace(command.Title);
        if (command.ScheduledStartUtc == default)
            throw new ArgumentOutOfRangeException(nameof(command), "Scheduled start is required.");
    }

    internal static void Validate(UpdateAppointmentCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.AppointmentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Appointment identity is invalid.");
        if (command.Title is not null && string.IsNullOrWhiteSpace(command.Title))
            throw new ArgumentException("Appointment title cannot be blank.", nameof(command));
    }

    internal static void Validate(DeleteAppointmentCommand command)
    {
        ValidateCommon(command.PortfolioId, command.Actor, command.DeliveryIdempotencyKey);
        if (command.AppointmentId <= 0)
            throw new ArgumentOutOfRangeException(nameof(command), "Appointment identity is invalid.");
    }

    private static void ValidateCommon(int portfolioId, StaffOperationActor actor, string idempotencyKey)
    {
        if (portfolioId <= 0 || actor.UserId <= 0 || actor.AuthSessionId == Guid.Empty ||
            actor.AccessContextId <= 0 || actor.AccessRevision <= 0)
            throw new ArgumentOutOfRangeException(nameof(portfolioId), "Appointment command scope is invalid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(idempotencyKey);
    }

    internal static bool ValidRange(DateTime start, DateTime? end) => !end.HasValue || end > start;

    internal static string ManageCapability(AppointmentType type) => type switch
    {
        AppointmentType.MaintenanceVisit => CapabilityKeys.WorkManage,
        _ => CapabilityKeys.LeasingShowingsManage,
    };

    internal static async Task<bool> ReferencesMatchAsync(RentalCommandDbContext db,
        int portfolioId, int? propertyId, int? unitId, int? managementId, int? applicationId,
        int? tenantId, CancellationToken ct)
    {
        if (!propertyId.HasValue)
            return await db.Set<Portfolio>().AsNoTracking()
                .Where(portfolio => portfolio.Id == portfolioId)
                .Select(_ => !unitId.HasValue && !managementId.HasValue &&
                    (!applicationId.HasValue || db.Set<RentalApplication>().AsNoTracking().Any(
                        item => item.Id == applicationId.Value && item.PortfolioId == portfolioId)) &&
                    (!tenantId.HasValue || db.Set<Tenant>().AsNoTracking().Any(
                        item => item.Id == tenantId.Value && item.PortfolioId == portfolioId)))
                .SingleOrDefaultAsync(ct);
        return await db.Set<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId.Value && property.PortfolioId == portfolioId)
            .Select(property =>
                (!unitId.HasValue || db.Set<Unit>().AsNoTracking().Any(unit =>
                    unit.Id == unitId.Value && unit.PropertyId == property.Id && unit.PortfolioId == portfolioId)) &&
                (!managementId.HasValue || db.Set<LeaseManagement>().AsNoTracking().Any(management =>
                    management.Id == managementId.Value && management.PropertyId == property.Id &&
                    management.PortfolioId == portfolioId && (!unitId.HasValue || management.UnitId == unitId.Value))) &&
                (!applicationId.HasValue || db.Set<RentalApplication>().AsNoTracking().Any(application =>
                    application.Id == applicationId.Value && application.PortfolioId == portfolioId &&
                    (!application.PropertyId.HasValue || application.PropertyId == property.Id))) &&
                (!tenantId.HasValue || db.Set<Tenant>().AsNoTracking().Any(tenant =>
                    tenant.Id == tenantId.Value && tenant.PortfolioId == portfolioId) &&
                    (!managementId.HasValue || db.Set<LeaseManagementParty>().AsNoTracking().Any(party =>
                        party.TenantId == tenantId.Value && party.PortfolioId == portfolioId &&
                        party.LeaseManagementId == managementId.Value &&
                        party.LeaseManagement != null && party.LeaseManagement.PropertyId == property.Id &&
                        (!unitId.HasValue || party.LeaseManagement.UnitId == unitId.Value)))))
            .SingleOrDefaultAsync(ct);
    }

    internal static async Task<bool> WorkOrderMatchesAsync(
        RentalCommandDbContext db,
        int portfolioId,
        StaffOperationActor actor,
        int? workOrderId,
        int? propertyId,
        int? unitId,
        int? tenantId,
        DateTime businessNow,
        DateTime securityNow,
        CancellationToken ct)
    {
        if (!workOrderId.HasValue)
            return true;
        if (!propertyId.HasValue)
            return false;

        return await StaffOperationAuthorization.AuthorizedWorkOrders(
                portfolioId, actor, CapabilityKeys.WorkManage, db,
                businessNow, securityNow, tracking: false)
            .TagWith("YS-266 appointment work order authorized context match")
            .AnyAsync(workOrder =>
                workOrder.Id == workOrderId.Value &&
                workOrder.PropertyId == propertyId.Value &&
                (!unitId.HasValue ? workOrder.UnitId == null : workOrder.UnitId == unitId.Value) &&
                (!tenantId.HasValue ? workOrder.TenantId == null : workOrder.TenantId == tenantId.Value),
                ct);
    }
}

internal static class AppointmentSnapshot
{
    internal static async Task<string> LoadAsync(RentalCommandDbContext db,
        int portfolioId, int id, CancellationToken ct)
    {
        var row = await db.Set<Appointment>().AsNoTracking()
            .Where(item => item.Id == id && item.PortfolioId == portfolioId)
            .Select(item => new
            {
                item.Id, item.PortfolioId, item.PropertyId, item.UnitId, item.LeaseManagementId,
                item.RentalApplicationId, item.TenantId, item.WorkOrderId, item.Title, item.ProspectName,
                item.ProspectEmail, item.Type, item.Status, item.ScheduledStart, item.ScheduledEnd,
                item.AssignedTo, item.Notes, item.CreatedAt, item.UpdatedAt,
                PropertyName = item.Property != null ? item.Property.Name : null,
                UnitNumber = item.Unit != null ? item.Unit.UnitNumber : null,
                TenantName = item.Tenant != null ? (item.Tenant.FirstName + " " + item.Tenant.LastName).Trim() : null,
            }).SingleAsync(ct);
        return JsonSerializer.Serialize(row);
    }
}
