using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicRentalMutationDomain { Unit, Application }
public enum AtomicRentalMutationOperation { Create, Update, Approve, Decline, Delete }

public sealed record AtomicRentalMutationCommand(
    int PortfolioId,
    int ActorUserId,
    Guid AuthSessionId,
    int AccessContextId,
    long ExpectedAccessRevision,
    AtomicRentalMutationDomain Domain,
    AtomicRentalMutationOperation Operation,
    int EntityId,
    string RequestJson,
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicRentalMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    int? RelatedEntityId = null) : IAtomicResultData;

/// <summary>
/// Receipt-backed Unit and rental-application writes. Every authorization predicate is repeated
/// inside the owning transaction and again before a receipt replay is returned.
/// </summary>
public sealed class AtomicRentalMutationHandler
    : IAtomicCommandHandler<AtomicRentalMutationCommand, AtomicRentalMutationResult>,
      IAtomicReplayAuthorizer<AtomicRentalMutationCommand>
{
    private const string UnitEntityType = "Unit";
    private const string ApplicationEntityType = "RentalApplication";

    public async Task<AtomicRentalMutationResult> HandleAsync(
        AtomicRentalMutationCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.AuthSession, command.AuthSessionId, ct);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.WorkspaceAccessContext, command.AccessContextId, ct);
        await attempt.Locking.AcquireAsync(AtomicLockResource.Portfolio, command.PortfolioId, ct);
        if (command.EntityId > 0)
        {
            await attempt.Locking.AcquireAsync(
                command.Domain == AtomicRentalMutationDomain.Unit
                    ? AtomicLockResource.Unit
                    : AtomicLockResource.RentalApplication,
                command.EntityId,
                ct);
        }

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        return command.Domain switch
        {
            AtomicRentalMutationDomain.Unit =>
                await MutateUnitAsync(command, attempt, now, ct),
            AtomicRentalMutationDomain.Application =>
                await MutateApplicationAsync(command, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public async Task AuthorizeReplayAsync(
        AtomicRentalMutationCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        var authorized = command.Domain switch
        {
            AtomicRentalMutationDomain.Unit when command.Operation == AtomicRentalMutationOperation.Create =>
                await AuthorizeUnitCreateAsync(command, persistence, now, ct),
            AtomicRentalMutationDomain.Unit =>
                await AuthorizeUnitAsync(command, persistence, now, ct),
            AtomicRentalMutationDomain.Application when command.Operation == AtomicRentalMutationOperation.Create =>
                await AuthorizeApplicationCreateAsync(command, persistence, now, ct),
            AtomicRentalMutationDomain.Application =>
                await AuthorizeApplicationAsync(command, persistence, now, ct),
            _ => false,
        };
        if (!authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private static async Task<AtomicRentalMutationResult> MutateUnitAsync(
        AtomicRentalMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        if (command.Operation == AtomicRentalMutationOperation.Create)
        {
            var request = Read<CreateUnitRequest>(command);
            if (!await AuthorizeUnitCreateAsync(command, persistence, now, ct))
                throw Denied();
            if (await persistence.Query<Unit>().AnyAsync(unit =>
                    unit.PortfolioId == command.PortfolioId
                    && unit.PropertyId == request.PropertyId
                    && unit.UnitNumber == request.UnitNumber
                    && unit.DeletedAt == null, ct))
            {
                throw new DomainValidationException(
                    $"Unit number \"{request.UnitNumber}\" already exists on this property.", 409);
            }

            var entity = new Unit
            {
                PortfolioId = command.PortfolioId,
                PropertyId = request.PropertyId,
                UnitNumber = request.UnitNumber,
                FloorPlan = request.FloorPlan,
                Bedrooms = request.Bedrooms,
                Bathrooms = request.Bathrooms,
                SquareFeet = request.SquareFeet,
                MarketRent = request.MarketRent,
                Notes = request.Notes,
                CreatedAt = now,
                UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, UnitEntityType,
                AuditLogOperation.Created, $"Unit {entity.UnitNumber} created"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, UnitEntityType, entity.Id, now);
            return Applied(entity.Id);
        }

        var unit = await persistence.Query<Unit>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId
            && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (unit is null) return Missing();
        if (!await AuthorizePropertyAsync(command, persistence, now,
                unit.PropertyId, CapabilityKeys.RentalsManage, requireAllProperties: false, ct))
            throw Denied();

        if (command.Operation == AtomicRentalMutationOperation.Delete)
        {
            await EnsureUnitHasNoHistoryAsync(command.PortfolioId, unit.Id, persistence, ct);
            unit.DeletedAt = now;
            unit.UpdatedAt = now;
            var flush = await attempt.FlushBusinessAsync(ct);
            var mutation = flush.Mutations.Single(candidate => ReferenceEquals(candidate.EntityReference, unit));
            attempt.EnrichMutation(mutation, Audit(command, UnitEntityType,
                AuditLogOperation.Deleted, $"Unit {unit.UnitNumber} deleted"));
            StageDataUpdate(attempt, command, UnitEntityType, unit.Id, now, deleted: true);
            return Applied(unit.Id);
        }

        if (command.Operation != AtomicRentalMutationOperation.Update)
            throw new ArgumentException("Unsupported Unit mutation operation.");
        var update = Read<UpdateUnitRequest>(command);
        if (update.UnitNumber is not null
            && !string.Equals(update.UnitNumber, unit.UnitNumber, StringComparison.Ordinal)
            && await persistence.Query<Unit>().AnyAsync(candidate =>
                candidate.PortfolioId == command.PortfolioId
                && candidate.PropertyId == unit.PropertyId
                && candidate.UnitNumber == update.UnitNumber
                && candidate.Id != unit.Id
                && candidate.DeletedAt == null, ct))
        {
            throw new DomainValidationException(
                $"Unit number \"{update.UnitNumber}\" already exists on this property.", 409);
        }

        if (update.UnitNumber is not null) unit.UnitNumber = update.UnitNumber;
        if (update.FloorPlan is not null) unit.FloorPlan = update.FloorPlan;
        if (update.Bedrooms.HasValue) unit.Bedrooms = update.Bedrooms.Value;
        if (update.Bathrooms.HasValue) unit.Bathrooms = update.Bathrooms.Value;
        if (update.SquareFeet.HasValue) unit.SquareFeet = update.SquareFeet.Value;
        if (update.MarketRent.HasValue) unit.MarketRent = update.MarketRent.Value;
        if (update.Notes is not null) unit.Notes = update.Notes;
        unit.UpdatedAt = now;
        attempt.BindSemanticAudit(unit, Audit(command, UnitEntityType,
            AuditLogOperation.Updated, $"Unit {unit.UnitNumber} updated"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, UnitEntityType, unit.Id, now);
        return Applied(unit.Id);
    }

    private static async Task<AtomicRentalMutationResult> MutateApplicationAsync(
        AtomicRentalMutationCommand command,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        var persistence = attempt.Persistence;
        if (command.Operation == AtomicRentalMutationOperation.Create)
        {
            var request = Read<CreateApplicationRequest>(command);
            if (!await AuthorizeApplicationCreateAsync(command, persistence, now, ct))
                throw Denied();
            var references = await ResolveApplicationReferencesAsync(
                command.PortfolioId, request.PropertyId, request.UnitId, persistence, ct);
            var email = NormalizeEmail(request.Email);
            if (email is not null && await OpenApplicationExistsAsync(
                    command.PortfolioId, email, persistence, ct))
                throw new DomainValidationException(
                    $"An open application for {email} already exists. Review it before creating another.", 409);

            var entity = new RentalApplication
            {
                PortfolioId = command.PortfolioId,
                PropertyId = references.PropertyId,
                UnitId = references.UnitId,
                FirstName = request.FirstName.Trim(),
                LastName = request.LastName.Trim(),
                Email = Normalize(request.Email),
                Phone = request.Phone,
                DateOfBirth = Utc(request.DateOfBirth),
                CurrentAddress = Normalize(request.CurrentAddress),
                Employer = request.Employer,
                MonthlyIncome = request.MonthlyIncome,
                DesiredMoveInDate = Utc(request.DesiredMoveInDate),
                Notes = request.Notes,
                IdExtractedFields = request.IdExtractedFields,
                ConsentGiven = false,
                Status = ApplicationStatus.Submitted,
                SubmittedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            persistence.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
                AuditLogOperation.Created, "Created from scanned rental application"));
            await attempt.FlushBusinessAsync(ct);
            StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now);
            return Applied(entity.Id);
        }

        var application = await persistence.Query<RentalApplication>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId
            && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (application is null) return Missing();
        if (!await AuthorizeApplicationAsync(command, persistence, now, ct))
            throw Denied();

        return command.Operation switch
        {
            AtomicRentalMutationOperation.Update =>
                await UpdateApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Approve =>
                await ApproveApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Decline =>
                await DeclineApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Delete =>
                await DeleteApplicationAsync(command, application, attempt, now, ct),
            _ => throw new ArgumentException("Unsupported Application mutation operation."),
        };
    }

    private static async Task<AtomicRentalMutationResult> UpdateApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status is not (ApplicationStatus.Submitted or ApplicationStatus.UnderReview))
            throw new DomainValidationException(
                "Only submitted or under-review applications can be edited.", 409);
        var request = Read<UpdateApplicationRequest>(command);
        var persistence = attempt.Persistence;

        if (request.ClearProperty)
        {
            if (!await AuthorizePropertyAsync(command, persistence, now, null,
                    CapabilityKeys.LeasingApplicationsManage, requireAllProperties: true, ct))
                throw Denied();
            entity.PropertyId = null;
            entity.UnitId = null;
        }
        if (request.PropertyId is > 0)
        {
            if (!await AuthorizePropertyAsync(command, persistence, now, request.PropertyId,
                    CapabilityKeys.LeasingApplicationsManage, requireAllProperties: false, ct))
                throw Denied();
            entity.PropertyId = request.PropertyId;
            if (entity.UnitId is > 0 && !await persistence.Query<Unit>().AnyAsync(unit =>
                    unit.Id == entity.UnitId && unit.PortfolioId == command.PortfolioId
                    && unit.PropertyId == request.PropertyId && unit.DeletedAt == null, ct))
                entity.UnitId = null;
        }
        if (request.ClearUnit) entity.UnitId = null;
        if (request.UnitId is > 0)
        {
            var unit = await persistence.Query<Unit>().AsNoTracking()
                .Where(candidate => candidate.Id == request.UnitId
                    && candidate.PortfolioId == command.PortfolioId && candidate.DeletedAt == null)
                .Select(candidate => new { candidate.Id, candidate.PropertyId })
                .SingleOrDefaultAsync(ct)
                ?? throw new DomainValidationException("Selected unit was not found in this portfolio.");
            if (entity.PropertyId is > 0 && entity.PropertyId != unit.PropertyId)
                throw new DomainValidationException("Selected unit does not belong to the selected property.");
            if (!await AuthorizePropertyAsync(command, persistence, now, unit.PropertyId,
                    CapabilityKeys.LeasingApplicationsManage, requireAllProperties: false, ct))
                throw Denied();
            entity.PropertyId = unit.PropertyId;
            entity.UnitId = unit.Id;
        }

        if (request.FirstName is not null) entity.FirstName = RequireNonBlank(request.FirstName, "First name");
        if (request.LastName is not null) entity.LastName = RequireNonBlank(request.LastName, "Last name");
        if (request.Email is not null) entity.Email = Normalize(request.Email);
        if (request.Phone is not null) entity.Phone = Normalize(request.Phone);
        if (request.ClearDateOfBirth) entity.DateOfBirth = null;
        else if (request.DateOfBirth.HasValue) entity.DateOfBirth = Utc(request.DateOfBirth);

        var structuredAddressChanged = request.CurrentAddressLine1 is not null
            || request.CurrentAddressLine2 is not null || request.CurrentCity is not null
            || request.CurrentState is not null || request.CurrentPostalCode is not null;
        if (request.CurrentAddressLine1 is not null) entity.CurrentAddressLine1 = Normalize(request.CurrentAddressLine1);
        if (request.CurrentAddressLine2 is not null) entity.CurrentAddressLine2 = Normalize(request.CurrentAddressLine2);
        if (request.CurrentCity is not null) entity.CurrentCity = Normalize(request.CurrentCity);
        if (request.CurrentState is not null) entity.CurrentState = Normalize(request.CurrentState);
        if (request.CurrentPostalCode is not null) entity.CurrentPostalCode = Normalize(request.CurrentPostalCode);
        if (structuredAddressChanged)
            entity.CurrentAddress = AddressComposer.Compose(entity.CurrentAddressLine1,
                entity.CurrentAddressLine2, entity.CurrentCity, entity.CurrentState, entity.CurrentPostalCode);
        else if (request.CurrentAddress is not null)
        {
            entity.CurrentAddressLine1 = null;
            entity.CurrentAddressLine2 = null;
            entity.CurrentCity = null;
            entity.CurrentState = null;
            entity.CurrentPostalCode = null;
            entity.CurrentAddress = Normalize(request.CurrentAddress);
        }
        if (request.Employer is not null) entity.Employer = Normalize(request.Employer);
        if (request.ClearMonthlyIncome) entity.MonthlyIncome = null;
        else if (request.MonthlyIncome.HasValue) entity.MonthlyIncome = request.MonthlyIncome;
        if (request.ClearDesiredMoveInDate) entity.DesiredMoveInDate = null;
        else if (request.DesiredMoveInDate.HasValue) entity.DesiredMoveInDate = Utc(request.DesiredMoveInDate);
        if (request.Notes is not null) entity.Notes = Normalize(request.Notes);
        entity.UpdatedAt = now;

        attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
            AuditLogOperation.Updated, "Application corrected by landlord"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now);
        return Applied(entity.Id);
    }

    private static async Task<AtomicRentalMutationResult> ApproveApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status == ApplicationStatus.Approved)
            throw new InvalidOperationException("Application is already approved.");
        if (entity.Status is ApplicationStatus.Declined or ApplicationStatus.Withdrawn)
            throw new InvalidOperationException($"Application is {entity.Status.ToString().ToLowerInvariant()} and cannot be approved.");
        await RequireCompatibleScreeningDecisionAsync(
            command.PortfolioId, entity.Id, ScreeningDecision.Accept, ScreeningDecision.Conditional,
            attempt.Persistence, ct);

        var tenant = new Tenant
        {
            PortfolioId = command.PortfolioId,
            FirstName = entity.FirstName,
            LastName = entity.LastName,
            Email = entity.Email,
            Phone = entity.Phone,
            DateOfBirth = entity.DateOfBirth,
            Notes = BuildTenantNote(entity),
            CreatedAt = now,
            UpdatedAt = now,
        };
        attempt.Persistence.Add(tenant);
        attempt.BindSemanticAudit(tenant, Audit(command, nameof(Tenant),
            AuditLogOperation.Created, $"Created from approved rental application #{entity.Id}", entityId: 0));
        await attempt.FlushBusinessAsync(ct);

        entity.Status = ApplicationStatus.Approved;
        entity.ReviewedAtUtc = now;
        entity.ApprovedTenantId = tenant.Id;
        entity.UpdatedAt = now;
        attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
            AuditLogOperation.Updated, "Application approved"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, nameof(Tenant), tenant.Id, now, "tenant");
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now, "application");
        return Applied(entity.Id, tenant.Id);
    }

    private static async Task<AtomicRentalMutationResult> DeclineApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status == ApplicationStatus.Approved)
            throw new InvalidOperationException("An approved application cannot be declined.");
        await RequireCompatibleScreeningDecisionAsync(
            command.PortfolioId, entity.Id, ScreeningDecision.Decline, null, attempt.Persistence, ct);
        var reason = JsonSerializer.Deserialize<string?>(command.RequestJson);
        entity.Status = ApplicationStatus.Declined;
        entity.DecisionReason = Normalize(reason);
        entity.ReviewedAtUtc = now;
        entity.UpdatedAt = now;
        attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
            AuditLogOperation.Updated, "Application declined" +
            (entity.DecisionReason is null ? string.Empty : $": {entity.DecisionReason}")));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now);
        return Applied(entity.Id);
    }

    private static async Task<AtomicRentalMutationResult> DeleteApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicWriteAttempt attempt,
        DateTime now,
        CancellationToken ct)
    {
        entity.DeletedAt = now;
        entity.UpdatedAt = now;
        var flush = await attempt.FlushBusinessAsync(ct);
        var mutation = flush.Mutations.Single(candidate => ReferenceEquals(candidate.EntityReference, entity));
        attempt.EnrichMutation(mutation, Audit(command, ApplicationEntityType,
            AuditLogOperation.Deleted, $"Application #{entity.Id} deleted"));
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now, deleted: true);
        return Applied(entity.Id);
    }

    private static async Task<bool> AuthorizeUnitCreateAsync(
        AtomicRentalMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<CreateUnitRequest>(command);
        if (!await persistence.Query<Property>().AsNoTracking().AnyAsync(property =>
                property.Id == request.PropertyId
                && property.PortfolioId == command.PortfolioId
                && property.DeletedAt == null, ct))
            return false;
        return await AuthorizePropertyAsync(command, persistence, now, request.PropertyId,
            CapabilityKeys.RentalsManage, requireAllProperties: false, ct);
    }

    private static async Task<bool> AuthorizeUnitAsync(
        AtomicRentalMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var units = command.Operation == AtomicRentalMutationOperation.Delete
            ? persistence.Query<Unit>().IgnoreQueryFilters()
            : persistence.Query<Unit>();
        var propertyId = await units.AsNoTracking()
            .Where(unit => unit.Id == command.EntityId && unit.PortfolioId == command.PortfolioId
                && (command.Operation == AtomicRentalMutationOperation.Delete || unit.DeletedAt == null))
            .Select(unit => (int?)unit.PropertyId)
            .SingleOrDefaultAsync(ct);
        return propertyId.HasValue && await AuthorizePropertyAsync(command, persistence, now,
            propertyId, CapabilityKeys.RentalsManage, requireAllProperties: false, ct);
    }

    private static async Task<bool> AuthorizeApplicationCreateAsync(
        AtomicRentalMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<CreateApplicationRequest>(command);
        int? propertyId = request.PropertyId;
        if (request.UnitId is > 0)
            propertyId = await persistence.Query<Unit>().AsNoTracking()
                .Where(unit => unit.Id == request.UnitId && unit.PortfolioId == command.PortfolioId
                    && unit.DeletedAt == null)
                .Select(unit => (int?)unit.PropertyId)
                .SingleOrDefaultAsync(ct);
        return await AuthorizePropertyAsync(command, persistence, now, propertyId,
            CapabilityKeys.LeasingApplicationsManage, requireAllProperties: propertyId is null, ct);
    }

    private static async Task<bool> AuthorizeApplicationAsync(
        AtomicRentalMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        CancellationToken ct)
    {
        var applications = command.Operation == AtomicRentalMutationOperation.Delete
            ? persistence.Query<RentalApplication>().IgnoreQueryFilters()
            : persistence.Query<RentalApplication>();
        var property = await applications.AsNoTracking()
            .Where(application => application.Id == command.EntityId
                && application.PortfolioId == command.PortfolioId
                && (command.Operation == AtomicRentalMutationOperation.Delete || application.DeletedAt == null))
            .Select(application => new { Found = true, application.PropertyId })
            .SingleOrDefaultAsync(ct);
        return property is not null && await AuthorizePropertyAsync(command, persistence, now,
            property.PropertyId, CapabilityKeys.LeasingApplicationsManage,
            requireAllProperties: property.PropertyId is null, ct);
    }

    private static Task<bool> AuthorizePropertyAsync(
        AtomicRentalMutationCommand command,
        IAtomicPersistenceSession persistence,
        DateTime now,
        int? propertyId,
        string capability,
        bool requireAllProperties,
        CancellationToken ct)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().AsNoTracking();
        return persistence.Query<AuthSession>().AsNoTracking().AnyAsync(session =>
            session.Id == command.AuthSessionId
            && session.UserId == command.ActorUserId
            && session.ActiveAccessContextId == command.AccessContextId
            && session.Status == AuthSessionStatus.Active
            && session.RevokedAtUtc == null
            && session.ExpiresAtUtc > now
            && persistence.Query<WorkspaceAccessContext>().Any(context =>
                context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId
                && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null
                && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId
                && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null
                && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= now
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > now)
                && assignments.Any(assignment =>
                    assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && assignment.Status == MembershipRoleAssignmentStatus.Active
                    && assignment.SuspendedAtUtc == null
                    && assignment.RevokedAtUtc == null
                    && assignment.EffectiveFromUtc <= now
                    && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > now)
                    && assignment.RoleProfile!.Capabilities.Any(grant =>
                        grant.CapabilityDefinition!.Key == capability
                        && grant.CapabilityDefinition.AuthorizationTargetKind ==
                            CapabilityAuthorizationTargetKind.Property)
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (!requireAllProperties && propertyId != null
                            && assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(selected =>
                                selected.PortfolioId == command.PortfolioId
                                && selected.PropertyId == propertyId))))), ct);
    }

    private static async Task<(int? PropertyId, int? UnitId)> ResolveApplicationReferencesAsync(
        int portfolioId,
        int? requestedPropertyId,
        int? requestedUnitId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        int? propertyId = null;
        if (requestedPropertyId is > 0 && await persistence.Query<Property>().AnyAsync(property =>
                property.Id == requestedPropertyId && property.PortfolioId == portfolioId
                && property.DeletedAt == null, ct))
            propertyId = requestedPropertyId;
        int? unitId = null;
        if (requestedUnitId is > 0 && await persistence.Query<Unit>().AnyAsync(unit =>
                unit.Id == requestedUnitId && unit.PortfolioId == portfolioId
                && unit.DeletedAt == null && (propertyId == null || unit.PropertyId == propertyId), ct))
            unitId = requestedUnitId;
        return (propertyId, unitId);
    }

    private static Task<bool> OpenApplicationExistsAsync(
        int portfolioId,
        string normalizedEmail,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        persistence.Query<RentalApplication>().AsNoTracking().AnyAsync(application =>
            application.PortfolioId == portfolioId
            && application.DeletedAt == null
            && application.Email != null
            && application.Email.Trim().ToLower() == normalizedEmail
            && (application.Status == ApplicationStatus.Submitted
                || application.Status == ApplicationStatus.UnderReview
                || application.Status == ApplicationStatus.Approved), ct);

    private static async Task RequireCompatibleScreeningDecisionAsync(
        int portfolioId,
        int applicationId,
        ScreeningDecision allowed,
        ScreeningDecision? alsoAllowed,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var latest = await persistence.Query<ApplicantScreening>().AsNoTracking()
            .Where(screening => screening.PortfolioId == portfolioId
                && screening.ApplicationId == applicationId
                && screening.Status == ApplicantScreeningStatus.Completed)
            .OrderByDescending(screening => screening.CompletedAtUtc)
            .Select(screening => new { screening.Decision })
            .FirstOrDefaultAsync(ct);
        if (latest is null) return;
        if (latest.Decision is null)
            throw new InvalidOperationException(
                "Record the screening decision before approving or declining this application.");
        if (latest.Decision != allowed && latest.Decision != alsoAllowed)
            throw new InvalidOperationException(
                "Update the recorded screening decision so it matches this application outcome.");
    }

    private static async Task EnsureUnitHasNoHistoryAsync(
        int portfolioId,
        int unitId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var guard = await persistence.Query<Unit>().IgnoreQueryFilters().AsNoTracking()
            .Where(unit => unit.PortfolioId == portfolioId && unit.Id == unitId)
            .Select(unit => new
            {
                IsOccupied = persistence.Query<UnitOccupancyProjection>().Any(occupancy =>
                    occupancy.PortfolioId == portfolioId && occupancy.UnitId == unit.Id && occupancy.IsOccupied),
                HasCurrent = persistence.Query<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                    lifecycle.PortfolioId == portfolioId && lifecycle.UnitId == unit.Id
                    && lifecycle.Lifecycle != "Canceled" && lifecycle.Lifecycle != "Closed"
                    && lifecycle.Lifecycle != "AccountingCloseout"),
                HasLease = persistence.Query<LeaseManagement>().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasWork = persistence.Query<WorkOrder>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasAppointment = persistence.Query<Appointment>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasInspection = persistence.Query<Inspection>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasExpense = persistence.Query<Expense>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasApplication = persistence.Query<RentalApplication>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasRecurringExpense = persistence.Query<RecurringExpense>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.UnitId == unit.Id),
                HasDocument = persistence.Query<StoredFile>().IgnoreQueryFilters().Any(row => row.PortfolioId == portfolioId && row.EntityType == UnitEntityType && row.EntityId == unit.Id),
            })
            .SingleAsync(ct);
        if (guard.IsOccupied) throw Conflict("This unit is occupied. Return possession before deleting the unit.");
        if (guard.HasCurrent) throw Conflict("This unit has a planned or current rental relationship. Cancel or complete it before deleting the unit.");
        if (guard.HasLease) throw Conflict("This unit has rental relationship, legal, or financial history and cannot be deleted.");
        if (guard.HasWork) throw Conflict("This unit has work order history. Archive the work order history instead of deleting the unit.");
        if (guard.HasAppointment) throw Conflict("This unit has appointment history. Archive the appointment history instead of deleting the unit.");
        if (guard.HasInspection) throw Conflict("This unit has inspection history. Archive the inspection history instead of deleting the unit.");
        if (guard.HasExpense) throw Conflict("This unit has expense history. Archive the expense history instead of deleting the unit.");
        if (guard.HasApplication) throw Conflict("This unit has application history. Archive the applications instead of deleting the unit.");
        if (guard.HasRecurringExpense) throw Conflict("This unit has recurring expense history. Archive the recurring expense history instead of deleting the unit.");
        if (guard.HasDocument) throw Conflict("This unit has document history. Archive the documents instead of deleting the unit.");
    }

    private static void StageDataUpdate(
        IAtomicWriteAttempt attempt,
        AtomicRentalMutationCommand command,
        string entityType,
        int entityId,
        DateTime now,
        string suffix = "entity",
        bool deleted = false) =>
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType,
                entityId,
                operation = deleted ? "delete" : "update",
                data = new { },
            }),
            IdempotencyKey = $"{command.DeliveryIdempotencyKey}:{suffix}",
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });

    private static AtomicSemanticAudit Audit(
        AtomicRentalMutationCommand command,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int? entityId = null) => new(
            command.PortfolioId, entityType, entityId ?? command.EntityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private static T Read<T>(AtomicRentalMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("Rental mutation request payload is invalid.");

    private static void Validate(AtomicRentalMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200
            || (command.Operation != AtomicRentalMutationOperation.Create && command.EntityId <= 0))
            throw new ArgumentException("Portfolio, actor, access revision, operation, and delivery identifiers are required.");
    }

    private static string RequireNonBlank(string value, string field)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new DomainValidationException($"{field} is required.");
        return trimmed;
    }

    private static string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private static string? NormalizeEmail(string? value) => Normalize(value)?.ToLowerInvariant();
    private static DateTime? Utc(DateTime? value) => value is null ? null
        : value.Value.Kind == DateTimeKind.Utc ? value : value.Value.ToUniversalTime();
    private static AtomicRentalMutationResult Missing() => new(false, false, 0);
    private static AtomicRentalMutationResult Applied(int id, int? relatedId = null) => new(true, true, id, relatedId);
    private static UnauthorizedAccessException Denied() => new("The record is not authorized in the current workspace scope.");
    private static DomainValidationException Conflict(string message) => new(message, 409);

    private static string? BuildTenantNote(RentalApplication application)
    {
        var parts = new List<string> { $"Created from rental application #{application.Id}." };
        if (!string.IsNullOrWhiteSpace(application.Employer)) parts.Add($"Employer: {application.Employer}.");
        if (application.MonthlyIncome is > 0) parts.Add($"Stated monthly income: {application.MonthlyIncome:0.##}.");
        if (!string.IsNullOrWhiteSpace(application.CurrentAddress)) parts.Add($"Prior address: {application.CurrentAddress}.");
        if (!string.IsNullOrWhiteSpace(application.Notes)) parts.Add($"Applicant notes: {application.Notes}");
        var note = string.Join(" ", parts);
        return note.Length > 2000 ? note[..2000] : note;
    }
}

public static class AtomicRentalMutation
{
    public static readonly AtomicJsonResultCodec<AtomicRentalMutationResult> Codec =
        new("rental.scoped-mutation.v1");

    public static AtomicRentalMutationCommand Command<TRequest>(
        WorkspaceReadScope scope,
        AtomicRentalMutationDomain domain,
        AtomicRentalMutationOperation operation,
        int entityId,
        string operationKey,
        TRequest request) => new(
            scope.PortfolioId, scope.UserId, scope.SessionId, scope.AccessContextId,
            scope.AccessRevision, domain, operation, entityId,
            JsonSerializer.Serialize(request), operationKey);

    public static AtomicCommandIdentity Identity(AtomicRentalMutationCommand command) => new(
        $"rental.{command.Domain.ToString().ToLowerInvariant()}.{command.Operation.ToString().ToLowerInvariant()}",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
        $"{command.EntityId}:{command.DeliveryIdempotencyKey}");

}
