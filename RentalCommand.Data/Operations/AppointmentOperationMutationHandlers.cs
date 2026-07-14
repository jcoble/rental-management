using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Operations;

namespace RentalCommand.Data.Operations;

public sealed class CreateAppointmentHandler
    : IAtomicCommandHandler<CreateAppointmentCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<CreateAppointmentCommand>
{
    public async Task<OperationMutationResult> HandleAsync(
        CreateAppointmentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(
                command.PortfolioId, command.Actor, command.PropertyId,
                CapabilityKeys.LeasingShowingsManage, attempt.Persistence, now, ct))
            return new(OperationMutationOutcome.NotFound, 0);
        if (!AppointmentOperationValidation.ValidRange(command.ScheduledStartUtc, command.ScheduledEndUtc))
            throw new DomainValidationException("The end time must be after the start time.");
        if (!await AppointmentOperationValidation.ReferencesMatchAsync(
                attempt.Persistence, command.PortfolioId, command.PropertyId, command.UnitId,
                command.LeaseManagementId, command.RentalApplicationId, command.TenantId, ct))
            return new(OperationMutationOutcome.NotFound, 0);

        var entity = new Appointment
        {
            PortfolioId = command.PortfolioId, PropertyId = command.PropertyId, UnitId = command.UnitId,
            LeaseManagementId = command.LeaseManagementId, RentalApplicationId = command.RentalApplicationId,
            TenantId = command.TenantId, Title = command.Title.Trim(), ProspectName = Clean(command.ProspectName),
            ProspectEmail = Clean(command.ProspectEmail), Type = command.Type, Status = command.Status,
            ScheduledStart = command.ScheduledStartUtc, ScheduledEnd = command.ScheduledEndUtc,
            AssignedTo = Clean(command.AssignedTo), Notes = Clean(command.Notes), CreatedAt = now, UpdatedAt = now,
        };
        attempt.Persistence.Add(entity);
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, Audit(command, entity, AuditLogOperation.Created, 0));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await AppointmentSnapshot.LoadAsync(attempt.Persistence, command.PortfolioId, entity.Id, ct);
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(Appointment), entity.Id,
            $"appointment-create:{command.DeliveryIdempotencyKey}", now));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(CreateAppointmentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(command.PortfolioId, command.Actor,
                command.PropertyId, CapabilityKeys.LeasingShowingsManage, persistence, now, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
    }

    internal static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    internal static AtomicSemanticAudit Audit(CreateAppointmentCommand command, Appointment entity,
        AuditLogOperation operation, int id) => new(command.PortfolioId, nameof(Appointment), id, operation,
        command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
        {
            entity.PropertyId, entity.UnitId, entity.Title, entity.Type, entity.Status,
            entity.ScheduledStart, entity.ScheduledEnd,
        }), ChangeReason: operation == AuditLogOperation.Created ? "Created appointment." : "Updated appointment.");
}

public sealed class UpdateAppointmentHandler
    : IAtomicCommandHandler<UpdateAppointmentCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<UpdateAppointmentCommand>
{
    public async Task<OperationMutationResult> HandleAsync(
        UpdateAppointmentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var entity = await StaffOperationAuthorization.AuthorizedAppointments(
                command.PortfolioId, command.Actor, CapabilityKeys.LeasingShowingsManage,
                attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.AppointmentId, ct);
        if (entity is null) return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        var destinationProperty = command.PropertyId ?? entity.PropertyId;
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(command.PortfolioId, command.Actor,
                destinationProperty, CapabilityKeys.LeasingShowingsManage, attempt.Persistence, now, ct))
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        var effectiveUnit = command.UnitId ?? entity.UnitId;
        var effectiveManagement = command.LeaseManagementId ?? entity.LeaseManagementId;
        var effectiveApplication = command.RentalApplicationId ?? entity.RentalApplicationId;
        var effectiveTenant = command.TenantId ?? entity.TenantId;
        if (!await AppointmentOperationValidation.ReferencesMatchAsync(attempt.Persistence, command.PortfolioId,
                destinationProperty, effectiveUnit, effectiveManagement, effectiveApplication, effectiveTenant, ct))
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);

        if (command.PropertyId.HasValue) entity.PropertyId = command.PropertyId;
        if (command.UnitId.HasValue) entity.UnitId = command.UnitId;
        if (command.LeaseManagementId.HasValue) entity.LeaseManagementId = command.LeaseManagementId;
        if (command.RentalApplicationId.HasValue) entity.RentalApplicationId = command.RentalApplicationId;
        if (command.TenantId.HasValue) entity.TenantId = command.TenantId;
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
        entity.UpdatedAt = now;
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(command.PortfolioId, nameof(Appointment), entity.Id,
            AuditLogOperation.Updated, command.Actor.UserId, NewValues: JsonSerializer.Serialize(new
            {
                entity.PropertyId, entity.UnitId, entity.Title, entity.Type, entity.Status,
                entity.ScheduledStart, entity.ScheduledEnd,
            }), ChangeReason: "Updated appointment."));
        await attempt.FlushBusinessAsync(ct);
        var snapshot = await AppointmentSnapshot.LoadAsync(attempt.Persistence, command.PortfolioId, entity.Id, ct);
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(Appointment), entity.Id,
            $"appointment-update:{command.DeliveryIdempotencyKey}", now));
        return new(OperationMutationOutcome.Applied, entity.Id, snapshot);
    }

    public async Task AuthorizeReplayAsync(UpdateAppointmentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.AuthorizedAppointments(
                command.PortfolioId, command.Actor, CapabilityKeys.LeasingShowingsManage,
                persistence, now, tracking: false)
            .AnyAsync(item => item.Id == command.AppointmentId, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
    }
}

public sealed class DeleteAppointmentHandler
    : IAtomicCommandHandler<DeleteAppointmentCommand, OperationMutationResult>,
      IAtomicReplayAuthorizer<DeleteAppointmentCommand>
{
    public async Task<OperationMutationResult> HandleAsync(DeleteAppointmentCommand command,
        IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var entity = await StaffOperationAuthorization.AuthorizedAppointments(
                command.PortfolioId, command.Actor, CapabilityKeys.LeasingShowingsManage,
                attempt.Persistence, now, tracking: true)
            .SingleOrDefaultAsync(item => item.Id == command.AppointmentId, ct);
        if (entity is null || entity.PropertyId != command.ExpectedPropertyId)
            return new(OperationMutationOutcome.NotFound, command.AppointmentId);
        attempt.Persistence.Remove(entity);
        attempt.UseDatabaseWallClockForAudit(now);
        attempt.BindSemanticAudit(entity, new AtomicSemanticAudit(
            command.PortfolioId, nameof(Appointment), entity.Id, AuditLogOperation.Deleted,
            command.Actor.UserId, ChangeReason: "Deleted appointment."));
        attempt.StageOutbox(CreateWorkOrderHandler.DataUpdate(command.PortfolioId, nameof(Appointment), entity.Id,
            $"appointment-delete:{command.DeliveryIdempotencyKey}", now, operation: "delete"));
        return new(OperationMutationOutcome.Applied, entity.Id);
    }

    public async Task AuthorizeReplayAsync(DeleteAppointmentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        AppointmentOperationValidation.Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await StaffOperationAuthorization.CanManageNullablePropertyAsync(
                command.PortfolioId, command.Actor, command.ExpectedPropertyId,
                CapabilityKeys.LeasingShowingsManage, persistence, now, ct))
            throw new UnauthorizedAccessException("The active assignment cannot manage this appointment.");
    }
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

    internal static async Task<bool> ReferencesMatchAsync(IAtomicPersistenceSession persistence,
        int portfolioId, int? propertyId, int? unitId, int? managementId, int? applicationId,
        int? tenantId, CancellationToken ct)
    {
        if (!propertyId.HasValue)
            return await persistence.Query<Portfolio>().AsNoTracking()
                .Where(portfolio => portfolio.Id == portfolioId)
                .Select(_ => !unitId.HasValue && !managementId.HasValue &&
                    (!applicationId.HasValue || persistence.Query<RentalApplication>().AsNoTracking().Any(
                        item => item.Id == applicationId.Value && item.PortfolioId == portfolioId)) &&
                    (!tenantId.HasValue || persistence.Query<Tenant>().AsNoTracking().Any(
                        item => item.Id == tenantId.Value && item.PortfolioId == portfolioId)))
                .SingleOrDefaultAsync(ct);
        return await persistence.Query<Property>().AsNoTracking()
            .Where(property => property.Id == propertyId.Value && property.PortfolioId == portfolioId)
            .Select(property =>
                (!unitId.HasValue || persistence.Query<Unit>().AsNoTracking().Any(unit =>
                    unit.Id == unitId.Value && unit.PropertyId == property.Id && unit.PortfolioId == portfolioId)) &&
                (!managementId.HasValue || persistence.Query<LeaseManagement>().AsNoTracking().Any(management =>
                    management.Id == managementId.Value && management.PropertyId == property.Id &&
                    management.PortfolioId == portfolioId && (!unitId.HasValue || management.UnitId == unitId.Value))) &&
                (!applicationId.HasValue || persistence.Query<RentalApplication>().AsNoTracking().Any(application =>
                    application.Id == applicationId.Value && application.PortfolioId == portfolioId &&
                    (!application.PropertyId.HasValue || application.PropertyId == property.Id))) &&
                (!tenantId.HasValue || persistence.Query<Tenant>().AsNoTracking().Any(tenant =>
                    tenant.Id == tenantId.Value && tenant.PortfolioId == portfolioId)))
            .SingleOrDefaultAsync(ct);
    }
}

internal static class AppointmentSnapshot
{
    internal static async Task<string> LoadAsync(IAtomicPersistenceSession persistence,
        int portfolioId, int id, CancellationToken ct)
    {
        var row = await persistence.Query<Appointment>().AsNoTracking()
            .Where(item => item.Id == id && item.PortfolioId == portfolioId)
            .Select(item => new
            {
                item.Id, item.PortfolioId, item.PropertyId, item.UnitId, item.LeaseManagementId,
                item.RentalApplicationId, item.TenantId, item.Title, item.ProspectName,
                item.ProspectEmail, item.Type, item.Status, item.ScheduledStart, item.ScheduledEnd,
                item.AssignedTo, item.Notes, item.CreatedAt, item.UpdatedAt,
                PropertyName = item.Property != null ? item.Property.Name : null,
                UnitNumber = item.Unit != null ? item.Unit.UnitNumber : null,
                TenantName = item.Tenant != null ? (item.Tenant.FirstName + " " + item.Tenant.LastName).Trim() : null,
            }).SingleAsync(ct);
        return JsonSerializer.Serialize(row);
    }
}
