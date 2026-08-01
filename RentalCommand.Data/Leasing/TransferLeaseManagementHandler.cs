using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class TransferLeaseManagementHandler
    : IAtomicCommandHandler<TransferLeaseManagementCommand, TransferLeaseManagementResult>
{
    private readonly RentalCommandDbContext _db;

    public TransferLeaseManagementHandler(RentalCommandDbContext db) => _db = db;

    public async Task<TransferLeaseManagementResult> HandleAsync(
        TransferLeaseManagementCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        Validate(command);

        // Every transfer takes Unit locks in numeric order, then the source relationship lock.
        // Opposing transfers therefore cannot deadlock by taking their Unit locks in reverse order.
        foreach (var unitId in new[] { command.SourceUnitId, command.DestinationUnitId }.OrderBy(id => id))
        {
            await context.AcquireLockAsync("Unit", unitId, ct);
        }
        await context.AcquireLockAsync(
            "LeaseManagement", command.SourceLeaseManagementId, ct);

        var securityNowUtc = await context.ReadDatabaseClockUtcAsync(ct);
        var businessNowUtc = command.BusinessNowUtc;
        if (!await AuthorizedTransfer(command, _db, businessNowUtc, securityNowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }

        var destinationPropertyId = await _db.Set<Unit>()
            .Where(unit => unit.Id == command.DestinationUnitId
                && unit.PortfolioId == command.PortfolioId && unit.DeletedAt == null)
            .Select(unit => unit.PropertyId)
            .SingleAsync(ct);
        var sourceVersion = await AtomicLeaseMutationPersistence.ResolveAuthoredDocumentSourceVersionAsync(_db,
            context, command.PortfolioId, destinationPropertyId, 0, command.DestinationDocumentTemplateId,
            command.CreatedByUserId, businessNowUtc, ct);
        if (!sourceVersion.Resolved)
        {
            return Error(command, new AtomicTransferLeaseManagementMutationResult(
                TransferLeaseManagementOutcome.InvalidTemplate, command.TransferPublicId,
                0, null, 0, 0, 0, null, 0, null, null, 0, 0,
                [], [], [], [], [], [], []));
        }

        var mutation = await AtomicLeaseMutationPersistence.TransferLeaseManagementAsync(_db,
            context, command, sourceVersion.DocumentSourceVersionId, businessNowUtc, ct);
        if (mutation.Outcome != TransferLeaseManagementOutcome.Transferred)
        {
            return Error(command, mutation);
        }

        StageAudits(command, mutation, context, businessNowUtc);
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new
            {
                entityType = nameof(LeaseManagement),
                entityId = mutation.DestinationLeaseManagementId,
                data = new
                {
                    eventName = "lease-management-transferred",
                    transferPublicId = mutation.TransferPublicId,
                    sourceLeaseManagementId = command.SourceLeaseManagementId,
                    sourceUnitId = command.SourceUnitId,
                    destinationLeaseManagementId = mutation.DestinationLeaseManagementId,
                    destinationUnitId = command.DestinationUnitId,
                },
            }),
            IdempotencyKey = command.DeliveryIdempotencyKey,
            CreatedAtUtc = businessNowUtc,
            NextAttemptAtUtc = businessNowUtc,
        });

        return Success(command, mutation);
    }

    public async Task AuthorizeReplayAsync(
        TransferLeaseManagementCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        var securityNowUtc = await _db.Database.SqlQuery<DateTime>($"SELECT clock_timestamp() AS \"Value\"").SingleAsync(ct);
        if (!await AuthorizedTransfer(
                command, _db, command.BusinessNowUtc, securityNowUtc).AnyAsync(ct))
        {
            throw Unauthorized();
        }
    }

    private static IQueryable<LeaseManagement> AuthorizedTransfer(
        TransferLeaseManagementCommand command,
        RentalCommandDbContext db,
        DateTime businessNowUtc,
        DateTime securityNowUtc)
    {
        var source = PossessionCommandAuthorization.AuthorizedRelationships(
            db,
            command.PortfolioId,
            command.SourceLeaseManagementId,
            command.SourceUnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            businessNowUtc,
            securityNowUtc);
        var destination = PossessionCommandAuthorization.AuthorizedUnits(
            db,
            command.PortfolioId,
            command.DestinationUnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            businessNowUtc,
            securityNowUtc);
        return source.Where(_ => destination.Any());
    }

    private static void Validate(TransferLeaseManagementCommand command)
    {
        PossessionCommandAuthorization.ValidateShape(
            command.PortfolioId,
            command.SourceLeaseManagementId,
            command.SourceUnitId,
            command.CreatedByUserId,
            command.AuthSessionId,
            command.AccessContextId,
            command.ExpectedAccessRevision,
            command.BusinessNowUtc,
            command.DeliveryIdempotencyKey);
        if (command.DestinationUnitId <= 0
            || command.DestinationUnitId == command.SourceUnitId
            || command.TransferPublicId == Guid.Empty
            || command.EffectiveOn == default
            || command.DestinationDocumentTemplateId <= 0
            || string.IsNullOrWhiteSpace(command.TransferReason)
            || command.TransferReason.Trim().Length > 500
            || (command.GiveDestinationPossessionNow
                && (string.IsNullOrWhiteSpace(command.PossessionAgreementExceptionReason)
                    || command.PossessionAgreementExceptionReason.Trim().Length > 1000))
            || (!command.GiveDestinationPossessionNow
                && !string.IsNullOrWhiteSpace(command.PossessionAgreementExceptionReason)))
        {
            throw new ArgumentException(
                "Destination Unit, transfer date, template, reason, and possession policy are invalid.");
        }
    }

    private static void StageAudits(
        TransferLeaseManagementCommand command,
        AtomicTransferLeaseManagementMutationResult mutation,
        IAtomicCommandContext context,
        DateTime nowUtc)
    {
        context.StageSemanticEvent(Audit(command, nameof(LeaseManagement),
            command.SourceLeaseManagementId, AuditLogOperation.Updated,
            "Source possession closed by Unit transfer."), nowUtc);
        context.StageSemanticEvent(Audit(command, nameof(LeaseManagement),
            mutation.DestinationLeaseManagementId, AuditLogOperation.Created,
            "Destination relationship created by Unit transfer."), nowUtc);
        context.StageSemanticEvent(Audit(command, nameof(TenantAccount),
            mutation.DestinationTenantAccountId, AuditLogOperation.Created,
            "Destination tenant account opened by Unit transfer."), nowUtc);
        context.StageSemanticEvent(Audit(command, nameof(LeaseAgreement),
            mutation.DestinationAgreementId, AuditLogOperation.Created,
            "Destination Agreement draft copied with cross-relationship transfer provenance."), nowUtc);
        context.StageSemanticEvent(Audit(command, nameof(UnitOperationalPeriod),
            mutation.TurnoverPeriodId, AuditLogOperation.Created,
            "Source Unit turnover started by Unit transfer."), nowUtc);

        foreach (var id in mutation.EndedSourcePartyIds)
        {
            context.StageSemanticEvent(Audit(command, nameof(LeaseManagementParty), id,
                AuditLogOperation.Updated, "Source party membership ended by Unit transfer."), nowUtc);
        }
        foreach (var id in mutation.DestinationPartyIds)
        {
            context.StageSemanticEvent(Audit(command, nameof(LeaseManagementParty), id,
                AuditLogOperation.Created, "Effective party membership continued on destination relationship."), nowUtc);
        }
        foreach (var id in mutation.DestinationSignerIds)
        {
            context.StageSemanticEvent(Audit(command, nameof(LeaseAgreementSigner), id,
                AuditLogOperation.Created, "Destination Agreement signer draft copied from governing Agreement."), nowUtc);
        }
        foreach (var id in mutation.RevokedSourceAccessIds)
        {
            context.StageSemanticEvent(Audit(command, nameof(TenantUserAccess), id,
                AuditLogOperation.Updated, "Source relationship access revoked by Unit transfer."), nowUtc);
        }
        foreach (var id in mutation.DestinationAccessIds)
        {
            context.StageSemanticEvent(Audit(command, nameof(TenantUserAccess), id,
                AuditLogOperation.Created, "Tenant access continued on destination relationship."), nowUtc);
        }
        if (mutation.DestinationSecurityDepositAccountId is int depositAccountId)
        {
            context.StageSemanticEvent(Audit(command, nameof(SecurityDepositAccount), depositAccountId,
                AuditLogOperation.Created, "Destination security-deposit account opened by Unit transfer."), nowUtc);
        }
        if (mutation.TenantLedgerEntryIds.Count > 0)
        {
            context.StageSemanticEvent(Audit(command, nameof(TenantAccount), mutation.SourceTenantAccountId,
                AuditLogOperation.Updated,
                $"Balance transferred out under {mutation.TransferPublicId}."), nowUtc);
            context.StageSemanticEvent(Audit(command, nameof(TenantAccount),
                mutation.DestinationTenantAccountId, AuditLogOperation.Updated,
                $"Balance transferred in under {mutation.TransferPublicId}."), nowUtc);
        }
        if (mutation.SecurityDepositEntryIds.Count > 0
            && mutation.SourceSecurityDepositAccountId is int sourceDepositId
            && mutation.DestinationSecurityDepositAccountId is int destinationDepositId)
        {
            context.StageSemanticEvent(Audit(command, nameof(SecurityDepositAccount), sourceDepositId,
                AuditLogOperation.Updated,
                $"Security deposit transferred out under {mutation.TransferPublicId}."), nowUtc);
            context.StageSemanticEvent(Audit(command, nameof(SecurityDepositAccount), destinationDepositId,
                AuditLogOperation.Updated,
                $"Security deposit transferred in under {mutation.TransferPublicId}."), nowUtc);
        }
    }

    private static AtomicSemanticAudit Audit(
        TransferLeaseManagementCommand command,
        string entityType,
        int entityId,
        AuditLogOperation operation,
        string reason) => new(
        command.PortfolioId,
        entityType,
        entityId,
        operation,
        UserId: command.CreatedByUserId,
        ChangeReason: reason);

    private static TransferLeaseManagementResult Success(
        TransferLeaseManagementCommand command,
        AtomicTransferLeaseManagementMutationResult mutation) => new(
        mutation.Outcome,
        mutation.TransferPublicId,
        command.SourceLeaseManagementId,
        command.SourceUnitId,
        mutation.DestinationLeaseManagementId,
        command.DestinationUnitId,
        mutation.DestinationTenantAccountId,
        mutation.DestinationAgreementId,
        mutation.DestinationSecurityDepositAccountId,
        mutation.TurnoverPeriodId,
        mutation.SourcePossessionReturnedAtUtc,
        mutation.DestinationPossessionGivenAtUtc,
        mutation.CarriedTenantBalance,
        mutation.CarriedSecurityDeposit,
        mutation.EndedSourcePartyIds,
        mutation.DestinationPartyIds,
        mutation.DestinationSignerIds,
        mutation.RevokedSourceAccessIds,
        mutation.DestinationAccessIds,
        mutation.TenantLedgerEntryIds,
        mutation.SecurityDepositEntryIds,
        null);

    private static TransferLeaseManagementResult Error(
        TransferLeaseManagementCommand command,
        AtomicTransferLeaseManagementMutationResult mutation) => new(
        mutation.Outcome,
        mutation.TransferPublicId,
        command.SourceLeaseManagementId,
        command.SourceUnitId,
        mutation.DestinationLeaseManagementId,
        command.DestinationUnitId,
        mutation.DestinationTenantAccountId,
        mutation.DestinationAgreementId,
        mutation.DestinationSecurityDepositAccountId,
        mutation.TurnoverPeriodId,
        mutation.SourcePossessionReturnedAtUtc,
        mutation.DestinationPossessionGivenAtUtc,
        0,
        0,
        [], [], [], [], [], [], [],
        mutation.Outcome switch
        {
            TransferLeaseManagementOutcome.AlreadyTransferred =>
                "This source relationship has already been transferred.",
            TransferLeaseManagementOutcome.SourcePossessionNotOpen =>
                "Source possession must be open before a Unit transfer.",
            TransferLeaseManagementOutcome.DestinationUnavailable =>
                "The destination Unit is unavailable or has an open operational period.",
            TransferLeaseManagementOutcome.GoverningAgreementRequired =>
                "An executed governing Agreement is required to prepare the destination Agreement draft.",
            TransferLeaseManagementOutcome.TenantAccountNotOpen =>
                "The source Tenant Account must be open.",
            TransferLeaseManagementOutcome.InvalidTemplate =>
                "The destination lease template is not active for the destination property.",
            TransferLeaseManagementOutcome.InvalidHousehold =>
                "The effective household, signer set, or business date is invalid for transfer.",
            TransferLeaseManagementOutcome.InvalidFinancialState =>
                "Pending payment/autopay work or an invalid deposit balance must be resolved before transfer.",
            _ => "The Unit transfer could not be completed.",
        });

    private static UnauthorizedAccessException Unauthorized() => new(
        "The source relationship or destination Unit is not authorized in the current property scope.");
}
