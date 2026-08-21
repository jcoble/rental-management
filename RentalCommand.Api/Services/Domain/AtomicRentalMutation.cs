using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using RentalCommand.Api.DTOs;
using RentalCommand.Core;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Policies;
using RentalCommand.Data;
using RentalCommand.Data.Authorization;
using RentalCommand.Data.Policies;
using RentalCommand.Api.Services;

namespace RentalCommand.Api.Services.Domain;

public enum AtomicRentalMutationDomain { Unit, Application }
public enum AtomicRentalMutationOperation { Create, Update, Approve, Decline, Withdraw, Delete }

public sealed record AtomicRentalMutationCommand(
    int PortfolioId,
    int ActorUserId,
    [property: AtomicFingerprintIgnore]
    Guid AuthSessionId,
    [property: AtomicFingerprintIgnore]
    int AccessContextId,
    [property: AtomicFingerprintIgnore]
    long ExpectedAccessRevision,
    AtomicRentalMutationDomain Domain,
    AtomicRentalMutationOperation Operation,
    int EntityId,
    string RequestJson,
    [property: AtomicFingerprintIgnore]
    string DeliveryIdempotencyKey) : IAtomicCommandData;

public sealed record AtomicRentalMutationResult(
    bool Found,
    bool Applied,
    int EntityId,
    int? RelatedEntityId = null,
    string? ResponseJson = null);

/// <summary>
/// Receipt-backed rental-application writes.
/// Every application authorization predicate is repeated inside the owning transaction and again
/// before a receipt replay is returned.
/// </summary>
public sealed class AtomicRentalMutationHandler
    : IAtomicCommandHandler<AtomicRentalMutationCommand, AtomicRentalMutationResult>
{
    private readonly RentalCommandDbContext _db;

    public AtomicRentalMutationHandler(RentalCommandDbContext db) => _db = db;

    private const string ApplicationEntityType = "RentalApplication";

    public Task<AtomicRentalMutationResult> HandleAsync(
        AtomicRentalMutationCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct) => throw RetiredPath();

    public async Task<AtomicRentalMutationResult> ExecuteAsync(
        AtomicRentalMutationCommand command,
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        Validate(command);
        var now = await AtomicCommandDbClock.ReadDatabaseClockUtcAsync(_db, ct);
        return command.Domain switch
        {
            AtomicRentalMutationDomain.Application =>
                await MutateApplicationAsync(command, attempt, now, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(command.Domain)),
        };
    }

    public Task AuthorizeReplayAsync(
        AtomicRentalMutationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct) => throw RetiredPath();

    public async Task AuthorizeAsync(
        AtomicRentalMutationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var authorized = command.Domain switch
        {
            AtomicRentalMutationDomain.Application when command.Operation == AtomicRentalMutationOperation.Create =>
                await AuthorizeApplicationCreateAsync(command, _db, now, ct),
            AtomicRentalMutationDomain.Application =>
                await AuthorizeApplicationAsync(command, _db, now, ct),
            _ => false,
        };
        if (!authorized)
            throw new UnauthorizedAccessException("Workspace access changed. Refresh and try again.");
    }

    private async Task<AtomicRentalMutationResult> MutateApplicationAsync(
        AtomicRentalMutationCommand command,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var db = _db;
        if (command.Operation == AtomicRentalMutationOperation.Create)
        {
            var request = Read<CreateApplicationRequest>(command);
            var references = await ResolveApplicationReferencesAsync(
                command.PortfolioId, request.PropertyId, request.UnitId, db, ct);
            if (!await AuthorizePropertyAsync(command, db, now, references.PropertyId,
                    CapabilityKeys.LeasingApplicationsManage,
                    requireAllProperties: references.PropertyId is null, ct))
                throw Denied();
            var email = NormalizeEmail(request.Email);
            if (email is not null && await OpenApplicationExistsAsync(
                    command.PortfolioId, email, db, ct))
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
                DateOfBirth = UtcDate(request.DateOfBirth),
                CurrentAddress = Normalize(request.CurrentAddress),
                Employer = request.Employer,
                MonthlyIncome = request.MonthlyIncome,
                DesiredMoveInDate = UtcDate(request.DesiredMoveInDate),
                Notes = request.Notes,
                IdExtractedFields = request.IdExtractedFields,
                ConsentGiven = false,
                Status = ApplicationStatus.Submitted,
                SubmittedAtUtc = now,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.Add(entity);
            attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
                AuditLogOperation.Created, "Created from scanned rental application"));
            await FlushOpenApplicationAsync(attempt, ct);
            StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now);
            return Applied(entity.Id,
                responseJson: JsonSerializer.Serialize(ApplicationResponse.FromEntity(entity)));
        }

        var application = await db.Set<RentalApplication>().SingleOrDefaultAsync(entity =>
            entity.Id == command.EntityId
            && entity.PortfolioId == command.PortfolioId
            && entity.DeletedAt == null, ct);
        if (application is null) return Missing();
        if (!await AuthorizeApplicationAsync(command, db, now, ct))
            throw Denied();

        return command.Operation switch
        {
            AtomicRentalMutationOperation.Update =>
                await UpdateApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Approve =>
                await ApproveApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Decline =>
                await DeclineApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Withdraw =>
                await WithdrawApplicationAsync(command, application, attempt, now, ct),
            AtomicRentalMutationOperation.Delete =>
                await DeleteApplicationAsync(command, application, attempt, now, ct),
            _ => throw new ArgumentException("That application action is not supported."),
        };
    }

    private async Task<AtomicRentalMutationResult> UpdateApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status is not (ApplicationStatus.Submitted or ApplicationStatus.UnderReview))
            throw new DomainValidationException(
                "Only submitted or under-review applications can be edited.", 409);
        var request = Read<UpdateApplicationRequest>(command);
        var db = _db;

        if (request.ClearProperty)
        {
            if (!await AuthorizePropertyAsync(command, db, now, null,
                    CapabilityKeys.LeasingApplicationsManage, requireAllProperties: true, ct))
                throw Denied();
            entity.PropertyId = null;
            entity.UnitId = null;
        }
        if (request.PropertyId is > 0)
        {
            if (!await db.Set<Property>().AnyAsync(property =>
                    property.Id == request.PropertyId
                    && property.PortfolioId == command.PortfolioId
                    && property.DeletedAt == null, ct))
                throw new DomainValidationException("Selected property was not found in this portfolio.");
            if (!await AuthorizePropertyAsync(command, db, now, request.PropertyId,
                    CapabilityKeys.LeasingApplicationsManage, requireAllProperties: false, ct))
                throw Denied();
            entity.PropertyId = request.PropertyId;
            if (entity.UnitId is > 0)
            {
                var currentUnit = await db.ResolveUnit(
                        command.PortfolioId, entity.UnitId.Value)
                    .SingleOrDefaultAsync(ct);
                if (currentUnit is null || currentUnit.DoesNotBelongTo(request.PropertyId))
                    entity.UnitId = null;
            }
        }
        if (request.ClearUnit) entity.UnitId = null;
        if (request.UnitId is > 0)
        {
            var resolution = await db.ResolveUnit(command.PortfolioId, request.UnitId.Value)
                .SingleOrDefaultAsync(ct)
                ?? throw new DomainValidationException("Selected unit was not found in this portfolio.");
            if (resolution.DoesNotBelongTo(entity.PropertyId))
                throw new DomainValidationException("Selected unit does not belong to the selected property.");
            if (!await AuthorizePropertyAsync(command, db, now, resolution.PropertyId,
                    CapabilityKeys.LeasingApplicationsManage, requireAllProperties: false, ct))
                throw Denied();
            entity.PropertyId = resolution.PropertyId;
            entity.UnitId = resolution.UnitId;
        }

        if (request.FirstName is not null) entity.FirstName = RequireNonBlank(request.FirstName, "First name");
        if (request.LastName is not null) entity.LastName = RequireNonBlank(request.LastName, "Last name");
        if (request.Email is not null) entity.Email = Normalize(request.Email);
        if (request.Phone is not null) entity.Phone = Normalize(request.Phone);
        if (request.ClearDateOfBirth) entity.DateOfBirth = null;
        else if (request.DateOfBirth.HasValue) entity.DateOfBirth = UtcDate(request.DateOfBirth);

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
        else if (request.DesiredMoveInDate.HasValue) entity.DesiredMoveInDate = UtcDate(request.DesiredMoveInDate);
        if (request.Notes is not null) entity.Notes = Normalize(request.Notes);
        entity.UpdatedAt = now;

        attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
            AuditLogOperation.Updated, "Application corrected by landlord"));
        await FlushOpenApplicationAsync(attempt, ct);
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now);
        return Applied(entity.Id);
    }

    private async Task<AtomicRentalMutationResult> ApproveApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status == ApplicationStatus.Approved)
        {
            if (entity.ApprovedTenantId is null)
                throw new InvalidOperationException("Application is approved without a linked tenant.");
            await EnsureApprovalArtifactsAsync(command, entity, entity.ApprovedTenantId.Value, attempt, now, ct);
            StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now, "application");
            return Applied(entity.Id, entity.ApprovedTenantId);
        }
        if (entity.Status is ApplicationStatus.Declined or ApplicationStatus.Withdrawn)
            throw new InvalidOperationException("This application cannot be approved in its current status.");
        await RequireCompatibleScreeningDecisionAsync(
            command.PortfolioId, entity.Id, ScreeningDecision.Accept, ScreeningDecision.Conditional,
            _db, ct);

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
        _db.Add(tenant);
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
        await EnsureApprovalArtifactsAsync(command, entity, tenant.Id, attempt, now, ct);
        StageDataUpdate(attempt, command, nameof(Tenant), tenant.Id, now, "tenant");
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now, "application");
        return Applied(entity.Id, tenant.Id);
    }

    private async Task EnsureApprovalArtifactsAsync(
        AtomicRentalMutationCommand command,
        RentalApplication application,
        int tenantId,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        var screeningExists = await _db.Set<ApplicantScreening>().AsNoTracking()
            .AnyAsync(screening => screening.PortfolioId == command.PortfolioId
                && screening.ApplicationId == application.Id, ct);
        if (!screeningExists)
        {
            var screening = new ApplicantScreening
            {
                PortfolioId = command.PortfolioId,
                ApplicationId = application.Id,
                Mode = ScreeningMode.External,
                Status = ApplicantScreeningStatus.Completed,
                ProviderDisplayName = "Synthetic test screening",
                ProviderReference = $"application-{application.Id}",
                OperationKey = ApprovalScreeningOperationKey(application.Id),
                ConsentConfirmed = application.ConsentGiven,
                ConsentAtUtc = application.ConsentAtUtc,
                ApplicantSubmittedAtUtc = now,
                CompletedAtUtc = now,
                LastStatusAtUtc = now,
                Decision = ScreeningDecision.Accept,
                DecisionReason = "Approved through the scan-first application workflow.",
                DecisionRecordedByUserId = command.ActorUserId,
                DecisionRecordedAtUtc = now,
                ConsumerReportUsedForDecision = false,
                CreatedByUserId = command.ActorUserId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Add(screening);
            await attempt.FlushBusinessAsync(ct);
            attempt.StageSemanticEvent(Audit(command, nameof(ApplicantScreening),
                AuditLogOperation.Created, "Synthetic test screening recorded for approved application.",
                screening.Id), now);

            var milestone = new ApplicantScreeningMilestone
            {
                PortfolioId = command.PortfolioId,
                ApplicantScreeningId = screening.Id,
                Source = "application-approval",
                DeliveryId = ApprovalScreeningDeliveryId(application.Id),
                EventType = "synthetic.completed",
                Status = ApplicantScreeningStatus.Completed,
                OccurredAtUtc = now,
                RecordedAtUtc = now,
            };
            _db.Add(milestone);
            await attempt.FlushBusinessAsync(ct);
            attempt.StageSemanticEvent(Audit(command, nameof(ApplicantScreeningMilestone),
                AuditLogOperation.Created, "Synthetic test screening milestone recorded.",
                milestone.Id), now);
            StageDataUpdate(attempt, command, nameof(ApplicantScreening), screening.Id, now, "screening");
        }

        var conversationExists = await _db.Set<Conversation>().AsNoTracking()
            .AnyAsync(conversation => conversation.PortfolioId == command.PortfolioId
                && conversation.TenantId == tenantId
                && conversation.PropertyId == application.PropertyId
                && conversation.WorkOrderId == null
                && conversation.Subject == ApprovalConversationSubject(application.Id), ct);
        if (conversationExists)
            return;

        var body = $"Your rental application for {ApprovalApplicationHome(application)} has been approved. We will continue your move-in steps from this thread.";
        var conversation = new Conversation
        {
            PortfolioId = command.PortfolioId,
            TenantId = tenantId,
            PropertyId = application.PropertyId,
            Subject = ApprovalConversationSubject(application.Id),
            StartedByLandlord = true,
            CreatedAt = now,
            LastMessageAt = now,
            LastMessagePreview = body,
            TenantUnreadCount = 1,
        };
        var message = new ConversationMessage
        {
            Conversation = conversation,
            SenderRole = ConversationSenderRole.Landlord,
            Body = body,
            Channels = "Portal",
            CreatedAt = now,
        };
        _db.Add(message);
        await attempt.FlushBusinessAsync(ct);
        attempt.StageSemanticEvent(Audit(command, nameof(Conversation),
            AuditLogOperation.Created, "Approved application opened applicant communication.",
            conversation.Id), now);
        attempt.StageSemanticEvent(Audit(command, nameof(ConversationMessage),
            AuditLogOperation.Created, "Approved application sent applicant communication.",
            message.Id), now);
        StageDataUpdate(attempt, command, nameof(Conversation), conversation.Id, now, "conversation");
    }

    private async Task<AtomicRentalMutationResult> DeclineApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status == ApplicationStatus.Approved)
            throw new InvalidOperationException("An approved application cannot be declined.");
        await RequireCompatibleScreeningDecisionAsync(
            command.PortfolioId, entity.Id, ScreeningDecision.Decline, null, _db, ct);
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

    private async Task<AtomicRentalMutationResult> WithdrawApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicCommandContext attempt,
        DateTime now,
        CancellationToken ct)
    {
        if (entity.Status == ApplicationStatus.Approved)
            throw new InvalidOperationException("An approved application cannot be withdrawn.");
        entity.Status = ApplicationStatus.Withdrawn;
        entity.ReviewedAtUtc = now;
        entity.UpdatedAt = now;
        attempt.BindSemanticAudit(entity, Audit(command, ApplicationEntityType,
            AuditLogOperation.Updated, "Application withdrawn"));
        await attempt.FlushBusinessAsync(ct);
        StageDataUpdate(attempt, command, ApplicationEntityType, entity.Id, now);
        return Applied(entity.Id);
    }

    private async Task<AtomicRentalMutationResult> DeleteApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalApplication entity,
        IAtomicCommandContext attempt,
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

    private async Task<bool> AuthorizeApplicationCreateAsync(
        AtomicRentalMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var request = Read<CreateApplicationRequest>(command);
        var references = await ResolveApplicationReferencesAsync(
            command.PortfolioId, request.PropertyId, request.UnitId, db, ct);
        return await AuthorizePropertyAsync(command, db, now, references.PropertyId,
            CapabilityKeys.LeasingApplicationsManage,
            requireAllProperties: references.PropertyId is null, ct);
    }

    private async Task<bool> AuthorizeApplicationAsync(
        AtomicRentalMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        CancellationToken ct)
    {
        var applications = command.Operation == AtomicRentalMutationOperation.Delete
            ? db.Set<RentalApplication>().IgnoreQueryFilters()
            : db.Set<RentalApplication>();
        var property = await applications.AsNoTracking()
            .Where(application => application.Id == command.EntityId
                && application.PortfolioId == command.PortfolioId
                && (command.Operation == AtomicRentalMutationOperation.Delete || application.DeletedAt == null))
            .Select(application => new { Found = true, application.PropertyId })
            .SingleOrDefaultAsync(ct);
        return property is not null && await AuthorizePropertyAsync(command, db, now,
            property.PropertyId, CapabilityKeys.LeasingApplicationsManage,
            requireAllProperties: property.PropertyId is null, ct);
    }

    private Task<bool> AuthorizePropertyAsync(
        AtomicRentalMutationCommand command,
        RentalCommandDbContext db,
        DateTime now,
        int? propertyId,
        string capability,
        bool requireAllProperties,
        CancellationToken ct)
    {
        var scope = new WorkspaceReadScope(
            command.PortfolioId,
            command.ActorUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision);
        return requireAllProperties
            ? db.AuthorizedAssignmentsForScope(
                    scope,
                    [capability],
                    CapabilityAuthorizationTargetKind.Property,
                    now)
                .AnyAsync(assignment =>
                    assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties, ct)
            : db.Set<Property>().AsNoTracking()
                .WhereAuthorizedForScope(db, scope, capability, now)
                .AnyAsync(property => property.Id == propertyId, ct);
    }

    private async Task<(int? PropertyId, int? UnitId)> ResolveApplicationReferencesAsync(
        int portfolioId,
        int? requestedPropertyId,
        int? requestedUnitId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var resolved = await db.ResolvePropertyUnit(
                portfolioId, requestedPropertyId, requestedUnitId)
            .SingleOrDefaultAsync(ct)
            ?? throw new DomainValidationException("Portfolio was not found.");

        if (resolved.PropertyWasRequestedButNotFound)
            throw new DomainValidationException("Selected property was not found in this portfolio.");
        if (resolved.UnitWasRequestedButNotFound)
            throw new DomainValidationException("Selected unit was not found in this portfolio.");
        if (resolved.UnitDoesNotBelongToProperty)
            throw new DomainValidationException("Selected unit does not belong to the selected property.");

        return (resolved.ResolvedPropertyId, resolved.UnitId);
    }

    private Task<bool> OpenApplicationExistsAsync(
        int portfolioId,
        string normalizedEmail,
        RentalCommandDbContext db,
        CancellationToken ct) =>
        db.Set<RentalApplication>()
            .OpenForEmail(portfolioId, normalizedEmail)
            .AnyAsync(ct);

    private async Task RequireCompatibleScreeningDecisionAsync(
        int portfolioId,
        int applicationId,
        ScreeningDecision allowed,
        ScreeningDecision? alsoAllowed,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var latest = await db.Set<ApplicantScreening>().AsNoTracking()
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

    private async Task FlushOpenApplicationAsync(
        IAtomicCommandContext attempt,
        CancellationToken ct)
    {
        try
        {
            await attempt.FlushBusinessAsync(ct);
        }
        catch (DbUpdateException ex) when (
            FindPostgresException(ex) is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: ApplicationPolicy.OpenEmailUniqueConstraint,
            })
        {
            throw new DomainValidationException(
                ApplicationPolicy.OpenEmailConflictMessage,
                409);
        }
    }

    private PostgresException? FindPostgresException(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException!)
        {
            if (current is PostgresException postgres) return postgres;
            if (current.InnerException is null) return null;
        }
        return null;
    }

    private void StageDataUpdate(
        IAtomicCommandContext attempt,
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

    private AtomicSemanticAudit Audit(
        AtomicRentalMutationCommand command,
        string entityType,
        AuditLogOperation operation,
        string reason,
        int? entityId = null) => new(
            command.PortfolioId, entityType, entityId ?? command.EntityId, operation,
            UserId: command.ActorUserId, ChangeReason: reason);

    private T Read<T>(AtomicRentalMutationCommand command) where T : class =>
        JsonSerializer.Deserialize<T>(command.RequestJson)
        ?? throw new ArgumentException("The rental update is invalid.");

    private void Validate(AtomicRentalMutationCommand command)
    {
        if (command.PortfolioId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequestJson)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey)
            || command.DeliveryIdempotencyKey.Length > 200
            || (command.Operation != AtomicRentalMutationOperation.Create && command.EntityId <= 0))
            throw new ArgumentException("Portfolio, actor, access revision, operation, and delivery identifiers are required.");
    }

    private string RequireNonBlank(string value, string field)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0) throw new DomainValidationException($"{field} is required.");
        return trimmed;
    }

    private string? Normalize(string? value)
    {
        var trimmed = value?.Trim();
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    private string? NormalizeEmail(string? value) => Normalize(value)?.ToLowerInvariant();
    private DateTime? UtcDate(DateTime? value) => value is null
        ? null
        : DateTime.SpecifyKind(value.Value.Date, DateTimeKind.Utc);
    private AtomicRentalMutationResult Missing() => new(false, false, 0);
    private AtomicRentalMutationResult Applied(
        int id,
        int? relatedId = null,
        string? responseJson = null) => new(true, true, id, relatedId, responseJson);
    private UnauthorizedAccessException Denied() => new("The record is not authorized in the current workspace scope.");
    private DomainValidationException Conflict(string message) => new(message, 409);
    private string? BuildTenantNote(RentalApplication application)
    {
        var parts = new List<string> { $"Created from rental application #{application.Id}." };
        if (!string.IsNullOrWhiteSpace(application.Employer)) parts.Add($"Employer: {application.Employer}.");
        if (application.MonthlyIncome is > 0) parts.Add($"Stated monthly income: {application.MonthlyIncome:0.##}.");
        if (!string.IsNullOrWhiteSpace(application.CurrentAddress)) parts.Add($"Prior address: {application.CurrentAddress}.");
        if (!string.IsNullOrWhiteSpace(application.Notes)) parts.Add($"Applicant notes: {application.Notes}");
        var note = string.Join(" ", parts);
        return note.Length > 2000 ? note[..2000] : note;
    }

    private string ApprovalScreeningOperationKey(int applicationId) =>
        $"application-approval:screening:{applicationId}";

    private string ApprovalScreeningDeliveryId(int applicationId) =>
        $"application-approval:{applicationId}:screening-completed";

    private string ApprovalConversationSubject(int applicationId) =>
        $"Rental application #{applicationId}";

    private string ApprovalApplicationHome(RentalApplication application) =>
        application.UnitId is null
            ? "the selected home"
            : $"unit #{application.UnitId}";

    private static InvalidOperationException RetiredPath() => new(
        "Atomic rental mutations must use the shared write executor.");
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

    public static TransactionalWrite<AtomicRentalMutationCommand, AtomicRentalMutationResult> Write(
        AtomicRentalMutationCommand command,
        RentalCommandDbContext db)
    {
        var identity = Identity(command);
        var handler = new AtomicRentalMutationHandler(db);
        var lockPlan = command.EntityId > 0
            ? new WriteLockPlan(
                WriteLockProtocol.AuthorizationScopeApplication,
                WriteLock.For("AuthSession", command.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", command.AccessContextId),
                WriteLock.For("Portfolio", command.PortfolioId),
                WriteLock.For("RentalApplication", command.EntityId))
            : new WriteLockPlan(
                WriteLockProtocol.AuthorizationScope,
                WriteLock.For("AuthSession", command.AuthSessionId),
                WriteLock.For("WorkspaceAccessContext", command.AccessContextId),
                WriteLock.For("Portfolio", command.PortfolioId));
        return new(
            identity.CommandType,
            WriteIdempotencyPolicy.Required,
            command,
            Codec.ContractName,
            lockPlan,
            handler.ExecuteAsync,
            handler.AuthorizeAsync);
    }

    public static AtomicCommandIdentity Identity(AtomicRentalMutationCommand command) => new(
        $"rental.{command.Domain.ToString().ToLowerInvariant()}.{command.Operation.ToString().ToLowerInvariant()}",
        $"{command.PortfolioId}:{command.AccessContextId}:{command.Domain}:{command.Operation}:" +
        $"{command.EntityId}:{command.DeliveryIdempotencyKey}");

}
