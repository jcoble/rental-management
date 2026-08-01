using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Payments;

namespace RentalCommand.Data.Leasing;

public sealed class GivePossessionHandler
    : IAtomicCommandHandler<GivePossessionCommand, GivePossessionResult>
{
    private readonly RentalCommandDbContext _db;

    public GivePossessionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<GivePossessionResult> HandleAsync(
        GivePossessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, command.LeaseManagementId,
            command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.BusinessNowUtc, command.DeliveryIdempotencyKey);

        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        await context.AcquireLockAsync("LeaseManagement", command.LeaseManagementId, ct);

        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        var target = await PossessionCommandAuthorization.AuthorizedRelationships(
                _db, command.PortfolioId, command.LeaseManagementId, command.UnitId,
                command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, businessNowUtc, securityNowUtc)
            .Select(relationship => new GiveTarget(
                relationship,
                relationship.TenantAccount != null && relationship.TenantAccount.ClosedAtUtc == null,
                relationship.Agreements.Any(agreement =>
                    agreement.FullyExecutedAtUtc != null
                    && agreement.ExecutedArtifactId != null
                    && agreement.VoidedAtUtc == null
                    && agreement.DraftCanceledAtUtc == null
                    && _db.Set<LeaseAgreementStatusProjection>().Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning)),
                _db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                    lifecycle.PortfolioId == command.PortfolioId
                    && lifecycle.LeaseManagementId == relationship.Id
                    && lifecycle.CurrentResidentCount > 0),
                _db.Set<LeaseManagement>().Any(other =>
                    other.PortfolioId == command.PortfolioId
                    && other.UnitId == command.UnitId
                    && other.Id != relationship.Id
                    && other.PossessionGivenAtUtc != null
                    && other.PossessionReturnedAtUtc == null),
                _db.Set<UnitOperationalPeriod>().Any(period =>
                    period.PortfolioId == command.PortfolioId
                    && period.UnitId == command.UnitId
                    && period.EndedAtUtc == null)))
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
        if (target.Relationship.CanceledAtUtc is not null || target.Relationship.PossessionReturnedAtUtc is not null)
        {
            return Empty(GivePossessionOutcome.RelationshipNotEligible, command,
                "The lease relationship is not eligible for posdb.");
        }
        if (target.Relationship.PossessionGivenAtUtc is not null)
        {
            return new(GivePossessionOutcome.AlreadyGiven, command.LeaseManagementId, command.UnitId,
                target.Relationship.PossessionGivenAtUtc, "Possession has already been given.");
        }
        if (!target.HasCurrentResident)
        {
            return Empty(GivePossessionOutcome.RelationshipNotEligible, command,
                "The lease relationship has no current resident party.");
        }
        if (!target.HasOpenAccount)
        {
            return Empty(GivePossessionOutcome.AccountNotOpen, command, "The tenant account must be open.");
        }
        if (!target.HasExecutedGoverningAgreement)
        {
            return Empty(GivePossessionOutcome.AgreementNotExecuted, command,
                "An executed governing agreement is required before possession can be given.");
        }
        if (target.HasOtherPossession || target.HasOpenOperationalPeriod)
        {
            return Empty(GivePossessionOutcome.UnitUnavailable, command,
                "The unit has conflicting possession or an open operational period.");
        }

        target.Relationship.PossessionGivenAtUtc = businessNowUtc;
        target.Relationship.UpdatedAtUtc = businessNowUtc;
        target.Relationship.RowVersion = Guid.NewGuid();
        context.BindSemanticAudit(target.Relationship, Updated(command.PortfolioId, command.LeaseManagementId,
            command.CreatedByUserId, "Possession given under an executed governing agreement."));
        context.StageOutbox(PossessionOutbox.Create(command.PortfolioId, command.DeliveryIdempotencyKey,
            businessNowUtc, "possession-given", nameof(LeaseManagement), command.LeaseManagementId,
            command.LeaseManagementId, command.UnitId));
        await context.FlushBusinessAsync(ct);

        return new(GivePossessionOutcome.Given, command.LeaseManagementId, command.UnitId, businessNowUtc, null);
    }

    public async Task AuthorizeReplayAsync(GivePossessionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, command.LeaseManagementId,
            command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedRelationships(_db, command.PortfolioId,
                command.LeaseManagementId, command.UnitId, command.CreatedByUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision, command.BusinessNowUtc,
                securityNowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
    }

    private static GivePossessionResult Empty(GivePossessionOutcome outcome, GivePossessionCommand command,
        string error) => new(outcome, command.LeaseManagementId, command.UnitId, null, error);

    private static AtomicSemanticAudit Updated(int portfolioId, int relationshipId, int userId, string reason) =>
        new(portfolioId, nameof(LeaseManagement), relationshipId, AuditLogOperation.Updated,
            UserId: userId, ChangeReason: reason);

    private sealed record GiveTarget(LeaseManagement Relationship, bool HasOpenAccount,
        bool HasExecutedGoverningAgreement, bool HasCurrentResident, bool HasOtherPossession,
        bool HasOpenOperationalPeriod);
}

public sealed class ReconcileHistoricalPossessionHandler
    : IAtomicCommandHandler<ReconcileHistoricalPossessionCommand, ReconcileHistoricalPossessionResult>
{
    private readonly RentalCommandDbContext _db;

    public ReconcileHistoricalPossessionHandler(RentalCommandDbContext db) => _db = db;

    private const string EligibleExceptionCode = "GoverningAgreementWithoutPossession";

    public async Task<ReconcileHistoricalPossessionResult> HandleAsync(
        ReconcileHistoricalPossessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        await context.AcquireLockAsync("LeaseManagement", command.LeaseManagementId, ct);

        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        var target = await LoadTarget(command, _db, businessNowUtc, securityNowUtc)
            .SingleOrDefaultAsync(ct);

        if (target is null)
        {
            throw new UnauthorizedAccessException(
                "The lease relationship is not authorized in the current property scope.");
        }
        if (target.Relationship.CanceledAtUtc is not null
            || target.Relationship.PossessionReturnedAtUtc is not null
            || !target.HasOnlyGoverningAgreementWithoutPossessionException)
        {
            return Empty(ReconcileHistoricalPossessionOutcome.RelationshipNotEligible, command,
                "Historical possession can only reconcile a governing agreement without posdb.");
        }
        if (target.Relationship.PossessionGivenAtUtc is not null)
        {
            return new ReconcileHistoricalPossessionResult(
                ReconcileHistoricalPossessionOutcome.AlreadyReconciled,
                command.LeaseManagementId,
                command.UnitId,
                target.Relationship.PossessionGivenAtUtc,
                "Possession has already been reconciled.");
        }
        if (!target.HasCurrentResident)
        {
            return Empty(ReconcileHistoricalPossessionOutcome.RelationshipNotEligible, command,
                "The lease relationship has no current resident party.");
        }
        if (!target.HasOpenAccount)
        {
            return Empty(ReconcileHistoricalPossessionOutcome.AccountNotOpen, command,
                "The tenant account must be open.");
        }
        if (!target.HasExecutedGoverningAgreement
            || target.GoverningTermStartOn is null)
        {
            return Empty(ReconcileHistoricalPossessionOutcome.AgreementNotExecuted, command,
                "An executed governing agreement is required before possession can be reconciled.");
        }
        if (command.PossessionGivenOn > target.BusinessDate)
        {
            return Empty(ReconcileHistoricalPossessionOutcome.DateAfterBusinessDate, command,
                "Possession date cannot be after the current business date.");
        }
        if (command.PossessionGivenOn < target.GoverningTermStartOn.Value
            || (target.GoverningTermEndOn is { } endOn && command.PossessionGivenOn > endOn))
        {
            return Empty(ReconcileHistoricalPossessionOutcome.DateOutsideAgreementTerm, command,
                "Possession date must fall within the governing agreement term.");
        }
        if (target.HasOtherPossession || target.HasOpenOperationalPeriod)
        {
            return Empty(ReconcileHistoricalPossessionOutcome.UnitUnavailable, command,
                "The unit has conflicting possession or an open operational period.");
        }

        var possessionGivenAtUtc = command.PossessionGivenOn.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        target.Relationship.PossessionGivenAtUtc = possessionGivenAtUtc;
        target.Relationship.UpdatedAtUtc = businessNowUtc;
        target.Relationship.RowVersion = Guid.NewGuid();
        context.BindSemanticAudit(target.Relationship, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(LeaseManagement),
            command.LeaseManagementId,
            AuditLogOperation.Updated,
            UserId: command.CreatedByUserId,
            ChangeReason: "Historical possession reconciled under an executed governing agreement."));
        context.StageOutbox(PossessionOutbox.Create(
            command.PortfolioId,
            command.DeliveryIdempotencyKey,
            businessNowUtc,
            "historical-possession-reconciled",
            nameof(LeaseManagement),
            command.LeaseManagementId,
            command.LeaseManagementId,
            command.UnitId));
        await context.FlushBusinessAsync(ct);

        return new ReconcileHistoricalPossessionResult(
            ReconcileHistoricalPossessionOutcome.Reconciled,
            command.LeaseManagementId,
            command.UnitId,
            possessionGivenAtUtc,
            null);
    }

    public async Task AuthorizeReplayAsync(
        ReconcileHistoricalPossessionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedRelationships(
                _db,
                command.PortfolioId,
                command.LeaseManagementId,
                command.UnitId,
                command.CreatedByUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision,
                command.BusinessNowUtc,
                securityNowUtc)
            .AnyAsync(ct))
        {
            throw new UnauthorizedAccessException(
                "The lease relationship is not authorized in the current property scope.");
        }
    }

    internal static IQueryable<HistoricalPossessionTarget> LoadTarget(
        ReconcileHistoricalPossessionCommand command,
        RentalCommandDbContext db,
        DateTime businessNowUtc,
        DateTime securityNowUtc)
    {
        var statuses = db.Set<LeaseAgreementStatusProjection>();
        var exceptions = db.Set<LeaseReconciliationExceptionProjection>();
        return PossessionCommandAuthorization.AuthorizedRelationships(
                db,
                command.PortfolioId,
                command.LeaseManagementId,
                command.UnitId,
                command.CreatedByUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision,
                businessNowUtc,
                securityNowUtc)
            .Select(relationship => new HistoricalPossessionTarget(
                relationship,
                db.Set<LeaseManagementLifecycleProjection>()
                    .Where(lifecycle => lifecycle.PortfolioId == command.PortfolioId
                        && lifecycle.LeaseManagementId == relationship.Id)
                    .Select(lifecycle => lifecycle.BusinessDate)
                    .FirstOrDefault(),
                relationship.TenantAccount != null
                    && relationship.TenantAccount.ClosedAtUtc == null,
                relationship.Agreements.Any(agreement =>
                    agreement.FullyExecutedAtUtc != null
                    && agreement.ExecutedArtifactId != null
                    && agreement.VoidedAtUtc == null
                    && agreement.DraftCanceledAtUtc == null
                    && statuses.Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning)),
                relationship.Agreements
                    .Where(agreement => statuses.Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning))
                    .Select(agreement => (DateOnly?)agreement.TermStartOn)
                    .FirstOrDefault(),
                relationship.Agreements
                    .Where(agreement => statuses.Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning))
                    .Select(agreement => agreement.TermEndOn)
                    .FirstOrDefault(),
                db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                    lifecycle.PortfolioId == command.PortfolioId
                    && lifecycle.LeaseManagementId == relationship.Id
                    && lifecycle.CurrentResidentCount > 0),
                exceptions.Any(candidate =>
                    candidate.PortfolioId == command.PortfolioId
                    && candidate.LeaseManagementId == relationship.Id
                    && candidate.ExceptionCode == EligibleExceptionCode)
                && !exceptions.Any(candidate =>
                    candidate.PortfolioId == command.PortfolioId
                    && candidate.LeaseManagementId == relationship.Id
                    && candidate.ExceptionCode != EligibleExceptionCode),
                db.Set<LeaseManagement>().Any(other =>
                    other.PortfolioId == command.PortfolioId
                    && other.UnitId == command.UnitId
                    && other.Id != relationship.Id
                    && other.PossessionGivenAtUtc != null
                    && other.PossessionReturnedAtUtc == null),
                db.Set<UnitOperationalPeriod>().Any(period =>
                    period.PortfolioId == command.PortfolioId
                    && period.UnitId == command.UnitId
                    && period.EndedAtUtc == null)));
    }

    private static void Validate(ReconcileHistoricalPossessionCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(
            command.PortfolioId,
            command.LeaseManagementId,
            command.UnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (command.PossessionGivenOn == default)
        {
            throw new ArgumentException("A historical possession date is required.");
        }
    }

    private static ReconcileHistoricalPossessionResult Empty(
        ReconcileHistoricalPossessionOutcome outcome,
        ReconcileHistoricalPossessionCommand command,
        string error) => new(outcome, command.LeaseManagementId, command.UnitId, null, error);

    internal sealed record HistoricalPossessionTarget(
        LeaseManagement Relationship,
        DateOnly BusinessDate,
        bool HasOpenAccount,
        bool HasExecutedGoverningAgreement,
        DateOnly? GoverningTermStartOn,
        DateOnly? GoverningTermEndOn,
        bool HasCurrentResident,
        bool HasOnlyGoverningAgreementWithoutPossessionException,
        bool HasOtherPossession,
        bool HasOpenOperationalPeriod);
}

/// <summary>
/// One transaction for the Unit Command Center's physical move-in confirmation. Deposit funding,
/// possession, and the optional MoveIn appointment completion either all commit with one receipt or
/// all roll back. Account ids and the governing deposit amount are always resolved in PostgreSQL.
/// </summary>
public sealed class ConfirmMoveInHandler
    : IAtomicCommandHandler<ConfirmMoveInCommand, ConfirmMoveInResult>
{
    private readonly RentalCommandDbContext _db;

    public ConfirmMoveInHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ConfirmMoveInResult> HandleAsync(
        ConfirmMoveInCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        await context.AcquireLockAsync(
            "LeaseManagement", command.LeaseManagementId, ct);

        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        var target = await LoadTarget(command, _db, businessNowUtc, securityNowUtc)
            .SingleOrDefaultAsync(ct);
        if (target is null)
        {
            throw new UnauthorizedAccessException(
                "The lease relationship is not authorized in the current property scope.");
        }
        if (target.Relationship.CanceledAtUtc is not null
            || target.Relationship.PossessionReturnedAtUtc is not null)
        {
            return Empty(ConfirmMoveInOutcome.RelationshipNotEligible, command,
                "The lease relationship is not eligible for posdb.");
        }
        if (target.Relationship.PossessionGivenAtUtc is not null)
        {
            return new ConfirmMoveInResult(
                ConfirmMoveInOutcome.AlreadyConfirmed,
                command.LeaseManagementId,
                command.UnitId,
                target.Relationship.PossessionGivenAtUtc,
                null,
                null,
                null,
                "Move-in has already been confirmed.");
        }
        if (!target.HasCurrentResident)
        {
            return Empty(ConfirmMoveInOutcome.RelationshipNotEligible, command,
                "The lease relationship has no current resident party.");
        }
        if (!target.HasOpenAccount || target.TenantAccountId is null)
        {
            return Empty(ConfirmMoveInOutcome.AccountNotOpen, command,
                "The tenant account must be open.");
        }
        if (!target.HasExecutedGoverningAgreement
            || target.SecurityDepositObligation is null)
        {
            return Empty(ConfirmMoveInOutcome.AgreementNotExecuted, command,
                "An executed governing agreement is required before move-in can be confirmed.");
        }
        if (target.HasOtherPossession || target.HasOpenOperationalPeriod)
        {
            return Empty(ConfirmMoveInOutcome.UnitUnavailable, command,
                "The unit has conflicting possession or an open operational period.");
        }

        Appointment? appointment = null;
        if (command.MoveInAppointmentId is { } appointmentId)
        {
            appointment = await _db.Set<Appointment>()
                .Where(candidate => candidate.Id == appointmentId
                    && candidate.PortfolioId == command.PortfolioId
                    && candidate.PropertyId == target.PropertyId
                    && candidate.UnitId == command.UnitId
                    && candidate.Type == AppointmentType.MoveIn
                    && (candidate.Status == AppointmentStatus.Scheduled
                        || candidate.Status == AppointmentStatus.Confirmed))
                .SingleOrDefaultAsync(ct);
            if (appointment is null)
            {
                return Empty(ConfirmMoveInOutcome.AppointmentInvalid, command,
                    "The selected move-in appointment is not open for this unit.");
            }
        }

        SecurityDepositMutationResult? fundedDeposit = null;
        var depositAmount = target.SecurityDepositObligation.Value;
        if (depositAmount > 0m)
        {
            if (target.SecurityDepositAccountId is null)
            {
                return Empty(ConfirmMoveInOutcome.DepositNotConfigured, command,
                    "Prepare the security deposit account before confirming move-in.");
            }
            if (command.DepositEffectiveOn is null
                || string.IsNullOrWhiteSpace(command.DepositPaymentMethodSummary))
            {
                return Empty(ConfirmMoveInOutcome.DepositNotConfigured, command,
                    "Deposit received date and payment method are required.");
            }

            await context.AcquireLockAsync(
                "TenantAccount", target.TenantAccountId.Value, ct);
            var depositCommand = DepositCommand(
                command,
                target.TenantAccountId.Value,
                target.SecurityDepositAccountId.Value,
                target.GoverningAgreementNumber,
                depositAmount);
            fundedDeposit = await new FundSecurityDepositHandler(_db)
                .HandleAsync(depositCommand, context, ct);
            if (!fundedDeposit.Applied)
            {
                return Empty(ConfirmMoveInOutcome.DepositConflict, command,
                    fundedDeposit.Error ?? "The security deposit could not be funded.");
            }
        }

        target.Relationship.PossessionGivenAtUtc = businessNowUtc;
        target.Relationship.UpdatedAtUtc = businessNowUtc;
        target.Relationship.RowVersion = Guid.NewGuid();
        context.BindSemanticAudit(target.Relationship, new AtomicSemanticAudit(
            command.PortfolioId,
            nameof(LeaseManagement),
            command.LeaseManagementId,
            AuditLogOperation.Updated,
            UserId: command.CreatedByUserId,
            ChangeReason: "Move-in confirmed under an executed governing agreement."));
        context.StageOutbox(PossessionOutbox.Create(
            command.PortfolioId,
            $"{command.DeliveryIdempotencyKey}:possession",
            businessNowUtc,
            "move-in-confirmed",
            nameof(LeaseManagement),
            command.LeaseManagementId,
            command.LeaseManagementId,
            command.UnitId));

        if (appointment is not null)
        {
            appointment.Status = AppointmentStatus.Completed;
            appointment.UpdatedAt = businessNowUtc;
            context.BindSemanticAudit(appointment, new AtomicSemanticAudit(
                command.PortfolioId,
                nameof(Appointment),
                appointment.Id,
                AuditLogOperation.Updated,
                UserId: command.CreatedByUserId,
                NewValues: JsonSerializer.Serialize(new { appointment.Status }),
                ChangeReason: "Completed when move-in was confirmed."));
            context.StageOutbox(PossessionOutbox.Create(
                command.PortfolioId,
                $"{command.DeliveryIdempotencyKey}:appointment",
                businessNowUtc,
                "move-in-appointment-completed",
                nameof(Appointment),
                appointment.Id,
                command.LeaseManagementId,
                command.UnitId));
        }

        await context.FlushBusinessAsync(ct);
        return new ConfirmMoveInResult(
            ConfirmMoveInOutcome.Confirmed,
            command.LeaseManagementId,
            command.UnitId,
            businessNowUtc,
            fundedDeposit?.SecurityDepositEntryId,
            fundedDeposit?.TenantLedgerEntryId,
            appointment?.Id,
            null);
    }

    public async Task AuthorizeReplayAsync(
        ConfirmMoveInCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        var target = await LoadTarget(command, _db, command.BusinessNowUtc, securityNowUtc)
            .Select(candidate => new
            {
                candidate.PropertyId,
            })
            .SingleOrDefaultAsync(ct);
        if (target is null)
        {
            throw new UnauthorizedAccessException(
                "The lease relationship is not authorized in the current property scope.");
        }
        if (command.DepositEffectiveOn.HasValue
            && !await StaffOperationAuthorization.CanManagePropertyAsync(
                command.PortfolioId,
                new StaffOperationActor(
                    command.CreatedByUserId,
                    command.AuthSessionId,
                    command.AccessContextId,
                    command.ExpectedAccessRevision),
                target.PropertyId,
                CapabilityKeys.MoneyDepositsManage,
                _db,
                command.BusinessNowUtc,
                securityNowUtc,
                ct))
        {
            throw new UnauthorizedAccessException(
                "The current access context cannot manage deposits for this property.");
        }
    }

    private static IQueryable<ConfirmMoveInTarget> LoadTarget(
        ConfirmMoveInCommand command,
        RentalCommandDbContext db,
        DateTime businessNowUtc,
        DateTime securityNowUtc)
    {
        var statuses = db.Set<LeaseAgreementStatusProjection>();
        return PossessionCommandAuthorization.AuthorizedRelationships(
                db,
                command.PortfolioId,
                command.LeaseManagementId,
                command.UnitId,
                command.CreatedByUserId,
                command.AuthSessionId,
                command.AccessContextId,
                command.ExpectedAccessRevision,
                businessNowUtc,
                securityNowUtc)
            .Select(relationship => new ConfirmMoveInTarget(
                relationship,
                relationship.PropertyId,
                relationship.TenantAccount != null ? relationship.TenantAccount.Id : null,
                relationship.TenantAccount != null
                    && relationship.TenantAccount.SecurityDepositAccount != null
                        ? relationship.TenantAccount.SecurityDepositAccount.Id
                        : null,
                relationship.TenantAccount != null
                    && relationship.TenantAccount.ClosedAtUtc == null,
                relationship.Agreements.Any(agreement =>
                    agreement.FullyExecutedAtUtc != null
                    && agreement.ExecutedArtifactId != null
                    && agreement.VoidedAtUtc == null
                    && agreement.DraftCanceledAtUtc == null
                    && statuses.Any(status => status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning)),
                relationship.Agreements
                    .Where(agreement => statuses.Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning))
                    .Select(agreement => (decimal?)agreement.SecurityDepositObligation)
                    .FirstOrDefault(),
                relationship.Agreements
                    .Where(agreement => statuses.Any(status =>
                        status.PortfolioId == command.PortfolioId
                        && status.LeaseManagementId == relationship.Id
                        && status.AgreementId == agreement.Id
                        && status.IsGoverning))
                    .Select(agreement => agreement.AgreementNumber)
                    .FirstOrDefault(),
                db.Set<LeaseManagement>().Any(other =>
                    other.PortfolioId == command.PortfolioId
                    && other.UnitId == command.UnitId
                    && other.Id != relationship.Id
                    && other.PossessionGivenAtUtc != null
                    && other.PossessionReturnedAtUtc == null),
                db.Set<UnitOperationalPeriod>().Any(period =>
                    period.PortfolioId == command.PortfolioId
                    && period.UnitId == command.UnitId
                    && period.EndedAtUtc == null),
                db.Set<LeaseManagementLifecycleProjection>().Any(lifecycle =>
                    lifecycle.PortfolioId == command.PortfolioId
                    && lifecycle.LeaseManagementId == relationship.Id
                    && lifecycle.CurrentResidentCount > 0)));
    }

    private static FundSecurityDepositCommand DepositCommand(
        ConfirmMoveInCommand command,
        int tenantAccountId,
        int securityDepositAccountId,
        string? governingAgreementNumber,
        decimal amount) => new(
            command.PortfolioId,
            tenantAccountId,
            securityDepositAccountId,
            amount,
            command.DepositEffectiveOn!.Value,
            $"Security deposit received at move-in for {governingAgreementNumber ?? command.LeaseManagementId.ToString()}",
            command.DepositPaymentMethodSummary!.Trim(),
            Clean(command.DepositExternalReference),
            null,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            CapabilityKeys.MoneyDepositsManage,
            $"{command.DeliveryIdempotencyKey}:deposit",
            $"{command.DeliveryIdempotencyKey}:deposit");

    private static void Validate(ConfirmMoveInCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(
            command.PortfolioId,
            command.LeaseManagementId,
            command.UnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (command.MoveInAppointmentId is <= 0
            || command.DepositPaymentMethodSummary?.Trim().Length > 200
            || command.DepositExternalReference?.Trim().Length > 200)
        {
            throw new ArgumentException(
                "Move-in appointment and deposit reference values are invalid.");
        }
    }

    private static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ConfirmMoveInResult Empty(
        ConfirmMoveInOutcome outcome,
        ConfirmMoveInCommand command,
        string error) => new(
            outcome,
            command.LeaseManagementId,
            command.UnitId,
            null,
            null,
            null,
            null,
            error);

    private sealed record ConfirmMoveInTarget(
        LeaseManagement Relationship,
        int PropertyId,
        int? TenantAccountId,
        int? SecurityDepositAccountId,
        bool HasOpenAccount,
        bool HasExecutedGoverningAgreement,
        decimal? SecurityDepositObligation,
        string? GoverningAgreementNumber,
        bool HasOtherPossession,
        bool HasOpenOperationalPeriod,
        bool HasCurrentResident);
}

public sealed class ReturnPossessionHandler
    : IAtomicCommandHandler<ReturnPossessionCommand, ReturnPossessionResult>
{
    private readonly RentalCommandDbContext _db;

    public ReturnPossessionHandler(RentalCommandDbContext db) => _db = db;

    public async Task<ReturnPossessionResult> HandleAsync(
        ReturnPossessionCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        await context.AcquireLockAsync("LeaseManagement", command.LeaseManagementId, ct);

        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        var authorized = await PossessionCommandAuthorization.AuthorizedRelationships(
                _db, command.PortfolioId, command.LeaseManagementId, command.UnitId,
                command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, businessNowUtc, securityNowUtc)
            .AnyAsync(ct);

        if (!authorized)
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }

        var mutation = await AtomicLeaseMutationPersistence.ReturnPossessionAsync(_db,
            context, command.PortfolioId,
            command.LeaseManagementId,
            command.UnitId,
            command.Parties.Select(item => new AtomicReturnPossessionPartyInput(
                item.LeaseManagementPartyId, item.Disposition)).ToArray(),
            command.Accesses.Select(item => new AtomicReturnPossessionAccessInput(
                item.TenantUserAccessId, item.Disposition)).ToArray(),
            command.CreatedByUserId,
            businessNowUtc,
            command.TurnoverReason,
            ct);

        if (mutation.Outcome != ReturnPossessionOutcome.Returned)
        {
            return mutation.Outcome switch
            {
                ReturnPossessionOutcome.AlreadyReturned => new(
                    mutation.Outcome, command.LeaseManagementId, command.UnitId,
                    null, mutation.PossessionReturnedAtUtc, "Possession has already been returned."),
                ReturnPossessionOutcome.PossessionNotGiven => Empty(
                    mutation.Outcome, command, "Possession cannot be returned before it has been given."),
                ReturnPossessionOutcome.TurnoverAlreadyOpen => Empty(
                    mutation.Outcome, command, "The unit already has an open turnover period."),
                ReturnPossessionOutcome.InvalidPartyDisposition => Empty(
                    mutation.Outcome, command,
                    "Every current party must have one valid disposition."),
                ReturnPossessionOutcome.InvalidAccessDisposition => Empty(
                    mutation.Outcome, command, "Every active tenant access must have one explicit disposition."),
                _ => throw new InvalidOperationException("Return possession produced an unknown outcome."),
            };
        }

        foreach (var partyId in mutation.EndedPartyIds)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
                nameof(LeaseManagementParty), partyId, AuditLogOperation.Updated,
                UserId: command.CreatedByUserId,
                ChangeReason: "Party membership ended when possession returned."), businessNowUtc);
        }
        foreach (var accessId in mutation.RevokedAccessIds)
        {
            context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
                nameof(TenantUserAccess), accessId, AuditLogOperation.Updated,
                UserId: command.CreatedByUserId,
                ChangeReason: "Tenant access revoked when possession returned."), businessNowUtc);
        }
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(LeaseManagement), command.LeaseManagementId, AuditLogOperation.Updated,
            UserId: command.CreatedByUserId,
            ChangeReason: "Possession returned; tenant account remains open."), businessNowUtc);
        context.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId,
            nameof(UnitOperationalPeriod), mutation.TurnoverPeriodId!.Value, AuditLogOperation.Created,
            UserId: command.CreatedByUserId,
            ChangeReason: "Turnover opened after possession return."), businessNowUtc);
        context.StageOutbox(PossessionOutbox.Create(command.PortfolioId, command.DeliveryIdempotencyKey,
            businessNowUtc, "possession-returned", nameof(LeaseManagement), command.LeaseManagementId,
            command.LeaseManagementId, command.UnitId));

        return new(ReturnPossessionOutcome.Returned, command.LeaseManagementId, command.UnitId,
            mutation.TurnoverPeriodId, mutation.PossessionReturnedAtUtc, null);
    }

    public async Task AuthorizeReplayAsync(ReturnPossessionCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedRelationships(_db, command.PortfolioId,
                command.LeaseManagementId, command.UnitId, command.CreatedByUserId, command.AuthSessionId,
                command.AccessContextId, command.ExpectedAccessRevision, command.BusinessNowUtc,
                securityNowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException("The lease relationship is not authorized in the current property scope.");
        }
    }

    private static void Validate(ReturnPossessionCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, command.LeaseManagementId,
            command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        if (string.IsNullOrWhiteSpace(command.TurnoverReason)
            || command.TurnoverReason.Length > 1000
            || command.Parties.Count == 0
            || command.Parties.Select(item => item.LeaseManagementPartyId).Distinct().Count() != command.Parties.Count
            || command.Accesses.Select(item => item.TenantUserAccessId).Distinct().Count() != command.Accesses.Count
            || command.Parties.Any(item => item.LeaseManagementPartyId <= 0 || !Enum.IsDefined(item.Disposition))
            || command.Accesses.Any(item => item.TenantUserAccessId <= 0 || !Enum.IsDefined(item.Disposition)))
        {
            throw new ArgumentException("Return possession dispositions and turnover reason are invalid.");
        }
    }

    private static ReturnPossessionResult Empty(ReturnPossessionOutcome outcome,
        ReturnPossessionCommand command, string error) =>
        new(outcome, command.LeaseManagementId, command.UnitId, null, null, error);
}

public sealed class CompleteTurnoverHandler
    : IAtomicCommandHandler<CompleteTurnoverCommand, CompleteTurnoverResult>
{
    private readonly RentalCommandDbContext _db;

    public CompleteTurnoverHandler(RentalCommandDbContext db) => _db = db;

    public async Task<CompleteTurnoverResult> HandleAsync(CompleteTurnoverCommand command,
        IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("Unit", command.UnitId, ct);
        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        context.UseDatabaseWallClockForAudit(businessNowUtc);
        var authorizedUnits = PossessionCommandAuthorization.AuthorizedUnits(_db,
            command.PortfolioId, command.UnitId, command.CreatedByUserId, command.AuthSessionId,
            command.AccessContextId, command.ExpectedAccessRevision, businessNowUtc, securityNowUtc);
        var period = await _db.Set<UnitOperationalPeriod>()
            .Where(candidate => candidate.Id == command.TurnoverPeriodId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.UnitId == command.UnitId
                && candidate.Type == UnitOperationalPeriodType.Turnover
                && authorizedUnits.Any(unit => unit.Id == candidate.UnitId
                    && unit.PropertyId == candidate.PropertyId))
            .SingleOrDefaultAsync(ct);
        if (period is null)
        {
            return new(CompleteTurnoverOutcome.TurnoverNotFound, command.UnitId,
                command.TurnoverPeriodId, null, "The turnover period was not found in the authorized unit.");
        }
        if (period.EndedAtUtc is not null)
        {
            return new(CompleteTurnoverOutcome.AlreadyCompleted, command.UnitId,
                command.TurnoverPeriodId, period.EndedAtUtc, "Turnover has already been completed.");
        }

        period.EndedAtUtc = businessNowUtc;
        context.BindSemanticAudit(period, new AtomicSemanticAudit(command.PortfolioId,
            nameof(UnitOperationalPeriod), period.Id, AuditLogOperation.Updated,
            UserId: command.CreatedByUserId, ChangeReason: "Turnover completed."));
        context.StageOutbox(PossessionOutbox.Create(command.PortfolioId, command.DeliveryIdempotencyKey,
            businessNowUtc, "turnover-completed", nameof(UnitOperationalPeriod), period.Id,
            period.SourceLeaseManagementId, command.UnitId));
        await context.FlushBusinessAsync(ct);
        return new(CompleteTurnoverOutcome.Completed, command.UnitId, period.Id, businessNowUtc, null);
    }

    public async Task AuthorizeReplayAsync(CompleteTurnoverCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await PossessionCommandAuthorization.AuthorizedUnits(_db, command.PortfolioId,
                command.UnitId, command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
                command.ExpectedAccessRevision, command.BusinessNowUtc, securityNowUtc).AnyAsync(ct))
        {
            throw new UnauthorizedAccessException("The unit is not authorized in the current property scope.");
        }
    }

    private static void Validate(CompleteTurnoverCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(command.PortfolioId, 1, command.UnitId,
            command.CreatedByUserId, command.AuthSessionId, command.AccessContextId,
            command.ExpectedAccessRevision, command.BusinessNowUtc, command.DeliveryIdempotencyKey);
        if (command.TurnoverPeriodId <= 0)
        {
            throw new ArgumentException("A turnover period id is required.");
        }
    }
}

internal static class PossessionCommandAuthorization
{
    internal static IQueryable<LeaseManagement> AuthorizedRelationships(RentalCommandDbContext db,
        int portfolioId, int relationshipId, int unitId, int userId, Guid sessionId,
        int accessContextId, long accessRevision, DateTime businessNowUtc, DateTime securityNowUtc)
    {
        var memberships = AuthorizedMemberships(db, portfolioId, userId, sessionId,
            accessContextId, accessRevision, businessNowUtc, securityNowUtc);
        var assignments = EffectiveAssignments(db, portfolioId, businessNowUtc);
        return db.Set<LeaseManagement>().Where(relationship =>
            relationship.Id == relationshipId && relationship.PortfolioId == portfolioId
            && relationship.UnitId == unitId && relationship.Unit != null
            && relationship.Unit.PortfolioId == portfolioId
            && relationship.Property != null && relationship.Property.PortfolioId == portfolioId
            && memberships.Any(membership => assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PropertyId == relationship.PropertyId && scope.PortfolioId == portfolioId)))
                && (assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage
                        && capability.CapabilityDefinition.AuthorizationTargetKind
                            == CapabilityAuthorizationTargetKind.Property)
                    || assignment.RoleProfile.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingOnboardingManage
                        && capability.CapabilityDefinition.AuthorizationTargetKind
                            == CapabilityAuthorizationTargetKind.Property)))));
    }

    internal static IQueryable<Unit> AuthorizedUnits(RentalCommandDbContext db,
        int portfolioId, int unitId, int userId, Guid sessionId, int accessContextId,
        long accessRevision, DateTime businessNowUtc, DateTime securityNowUtc)
    {
        var memberships = AuthorizedMemberships(db, portfolioId, userId, sessionId,
            accessContextId, accessRevision, businessNowUtc, securityNowUtc);
        var assignments = EffectiveAssignments(db, portfolioId, businessNowUtc);
        return db.Set<Unit>().Where(unit => unit.Id == unitId && unit.PortfolioId == portfolioId
            && unit.Property != null && unit.Property.PortfolioId == portfolioId
            && memberships.Any(membership => assignments.Any(assignment =>
                assignment.WorkspaceMembershipId == membership.Id
                && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                    || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                        && assignment.SelectedProperties.Any(scope =>
                            scope.PropertyId == unit.PropertyId && scope.PortfolioId == portfolioId)))
                && (assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.RentalsManage
                        && capability.CapabilityDefinition.AuthorizationTargetKind
                            == CapabilityAuthorizationTargetKind.Property)
                    || assignment.RoleProfile.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == CapabilityKeys.LeasingOnboardingManage
                        && capability.CapabilityDefinition.AuthorizationTargetKind
                            == CapabilityAuthorizationTargetKind.Property)))));
    }

    private static IQueryable<WorkspaceMembership> AuthorizedMemberships(
        RentalCommandDbContext db, int portfolioId, int userId, Guid sessionId,
        int accessContextId, long accessRevision, DateTime businessNowUtc, DateTime securityNowUtc)
        => db.Set<WorkspaceMembership>().Where(membership =>
            db.Set<AuthSession>().Any(session => session.Id == sessionId
                && session.UserId == userId && session.ActiveAccessContextId == accessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && db.Set<WorkspaceAccessContext>().Any(context => context.Id == accessContextId
                && context.UserId == userId && context.PortfolioId == portfolioId
                && context.AccessRevision == accessRevision && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
                && membership.AccessContextId == accessContextId && membership.PortfolioId == portfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= businessNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > businessNowUtc));

    private static IQueryable<MembershipRoleAssignment> EffectiveAssignments(
        RentalCommandDbContext db, int portfolioId, DateTime nowUtc) =>
        db.Set<MembershipRoleAssignment>().Where(assignment =>
            assignment.PortfolioId == portfolioId
            && assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= nowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > nowUtc));

    internal static void ValidateShape(int portfolioId, int relationshipId, int unitId, int userId,
        Guid sessionId, int accessContextId, long accessRevision, DateTime businessNowUtc,
        string deliveryIdempotencyKey)
    {
        if (portfolioId <= 0 || relationshipId <= 0 || unitId <= 0 || userId <= 0
            || sessionId == Guid.Empty || accessContextId <= 0 || accessRevision <= 0
            || businessNowUtc == default
            || string.IsNullOrWhiteSpace(deliveryIdempotencyKey) || deliveryIdempotencyKey.Length > 200)
        {
            throw new ArgumentException(
                "Portfolio, relationship, unit, actor, access, business time, and delivery ids are required.");
        }
    }
}

internal static class PossessionOutbox
{
    internal static OutboxMessage Create(int portfolioId, string idempotencyKey, DateTime nowUtc,
        string eventName, string entityType, int entityId, int? relationshipId, int unitId) => new()
    {
        PortfolioId = portfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new
        {
            entityType,
            entityId,
            data = new { eventName, leaseManagementId = relationshipId, unitId },
        }),
        IdempotencyKey = idempotencyKey,
        CreatedAtUtc = nowUtc,
        NextAttemptAtUtc = nowUtc,
    };
}
