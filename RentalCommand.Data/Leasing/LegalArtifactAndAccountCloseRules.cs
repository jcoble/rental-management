using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Leasing;

namespace RentalCommand.Data.Leasing;

public sealed class VoidLeaseAgreementRule
{
    private readonly RentalCommandDbContext _db;

    public VoidLeaseAgreementRule(RentalCommandDbContext db) => _db = db;

    public async Task<VoidLegalArtifactResult> ExecuteAsync(
        VoidLeaseAgreementCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        LegalArtifactCommandSupport.Validate(command, command.LeaseAgreementId);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(command, _db, now)
            .SelectMany(item => item.Agreements)
            .Select(agreement => new
            {
                Agreement = agreement,
                ActivePossessionDependsOnAgreement = agreement.LeaseManagement!.PossessionGivenAtUtc != null
                    && agreement.LeaseManagement.PossessionReturnedAtUtc == null
                    && _db.Set<LeaseAgreementStatusProjection>().Any(status =>
                        status.PortfolioId == command.PortfolioId && status.AgreementId == agreement.Id
                        && status.LeaseManagementId == command.LeaseManagementId && status.IsGoverning),
                ActiveAddendaDependOnAgreement = agreement.Addenda.Any(addendum =>
                    addendum.FullyExecutedAtUtc != null && addendum.VoidedAtUtc == null
                    && addendum.DraftCanceledAtUtc == null),
                HasUnreversedPostedMoney = _db.Set<TenantLedgerEntry>().Any(entry =>
                        entry.PortfolioId == command.PortfolioId && entry.LeaseAgreementId == agreement.Id
                        && entry.ReversesEntryId == null
                        && !entry.ReversalEntries.Any())
                    || _db.Set<SecurityDepositEntry>().Any(entry =>
                        entry.PortfolioId == command.PortfolioId && entry.LeaseAgreementId == agreement.Id
                        && entry.ReversesEntryId == null
                        && !entry.ReversalEntries.Any()),
            })
            .SingleOrDefaultAsync(item => item.Agreement.Id == command.LeaseAgreementId, ct)
            ?? throw LegalArtifactCommandSupport.Unauthorized("Agreement");
        if (target.Agreement.VoidedAtUtc.HasValue)
            return LegalArtifactCommandSupport.AgreementError(VoidLegalArtifactOutcome.AlreadyVoided,
                command, target.Agreement.VoidedAtUtc, "The Agreement is already void.");
        if (target.Agreement.IssuedAtUtc == null || target.Agreement.IssuedArtifactId == null)
            return LegalArtifactCommandSupport.AgreementError(VoidLegalArtifactOutcome.NotIssued,
                command, null, "An unissued draft must be canceled, not voided.");
        if (target.ActivePossessionDependsOnAgreement)
            return LegalArtifactCommandSupport.AgreementError(VoidLegalArtifactOutcome.ActivePossessionDependsOnAgreement,
                command, null, "Return possession or execute a governing replacement before voiding this Agreement.");
        if (target.ActiveAddendaDependOnAgreement)
            return LegalArtifactCommandSupport.AgreementError(VoidLegalArtifactOutcome.ActiveAddendaDependOnAgreement,
                command, null, "Every executed Addendum attached to this Agreement must be voided or superseded first.");
        if (target.HasUnreversedPostedMoney)
            return LegalArtifactCommandSupport.AgreementError(VoidLegalArtifactOutcome.PostedMoneyRequiresResolution,
                command, null, "Reverse every unresolved ledger/deposit effect before voiding this Agreement.");

        target.Agreement.VoidedAtUtc = now;
        target.Agreement.VoidReasonCode = command.VoidReasonCode.Trim();
        target.Agreement.VoidNote = command.VoidNote?.Trim();
        target.Agreement.UpdatedAtUtc = now;
        context.BindSemanticAudit(target.Agreement, LegalArtifactCommandSupport.Audit(command,
            nameof(LeaseAgreement), target.Agreement.Id, "Explicitly voided an issued Agreement."));
        await LegalArtifactCommandSupport.VoidOpenPacketAsync(_db, command.PortfolioId,
            target.Agreement.Id, null, command.ActorUserId, now, context, ct);
        LegalArtifactCommandSupport.StageOutbox(context, command, now, nameof(LeaseAgreement),
            target.Agreement.Id, "agreement-voided");
        return new(VoidLegalArtifactOutcome.Voided, command.LeaseManagementId,
            target.Agreement.Id, null, now, null);
    }

    public Task AuthorizeReplayAsync(VoidLeaseAgreementCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class VoidLeaseAddendumRule
{
    private readonly RentalCommandDbContext _db;

    public VoidLeaseAddendumRule(RentalCommandDbContext db) => _db = db;

    public async Task<VoidLegalArtifactResult> ExecuteAsync(
        VoidLeaseAddendumCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        LegalArtifactCommandSupport.Validate(command, command.LeaseAddendumId);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await LeaseAddendumCommandSupport.AuthorizedRelationships(command, _db, now)
            .SelectMany(item => item.Addenda)
            .Select(addendum => new
            {
                Addendum = addendum,
                HasPostedMoney = _db.Set<TenantLedgerEntry>().Any(entry =>
                        entry.PortfolioId == command.PortfolioId && entry.LeaseAddendumId == addendum.Id
                        && entry.ReversesEntryId == null
                        && !entry.ReversalEntries.Any())
                    || _db.Set<SecurityDepositEntry>().Any(entry =>
                        entry.PortfolioId == command.PortfolioId && entry.LeaseAddendumId == addendum.Id
                        && entry.ReversesEntryId == null
                        && !entry.ReversalEntries.Any()),
            })
            .SingleOrDefaultAsync(item => item.Addendum.Id == command.LeaseAddendumId, ct)
            ?? throw LegalArtifactCommandSupport.Unauthorized("Addendum");
        if (target.Addendum.VoidedAtUtc.HasValue)
            return LegalArtifactCommandSupport.AddendumError(VoidLegalArtifactOutcome.AlreadyVoided,
                command, target.Addendum.VoidedAtUtc, "The Addendum is already void.");
        if (target.Addendum.IssuedAtUtc == null || target.Addendum.IssuedArtifactId == null)
            return LegalArtifactCommandSupport.AddendumError(VoidLegalArtifactOutcome.NotIssued,
                command, null, "An unissued draft must be canceled, not voided.");
        if (target.HasPostedMoney)
            return LegalArtifactCommandSupport.AddendumError(VoidLegalArtifactOutcome.PostedMoneyRequiresResolution,
                command, null, "Post explicit adjustments or reversals before voiding an Addendum with ledger effects.");

        target.Addendum.VoidedAtUtc = now;
        target.Addendum.VoidReasonCode = command.VoidReasonCode.Trim();
        target.Addendum.VoidNote = command.VoidNote?.Trim();
        target.Addendum.UpdatedAtUtc = now;
        context.BindSemanticAudit(target.Addendum, LegalArtifactCommandSupport.Audit(command,
            nameof(LeaseAddendum), target.Addendum.Id, "Explicitly voided an issued Addendum."));
        await LegalArtifactCommandSupport.VoidOpenPacketAsync(_db, command.PortfolioId,
            null, target.Addendum.Id, command.ActorUserId, now, context, ct);
        LegalArtifactCommandSupport.StageOutbox(context, command, now, nameof(LeaseAddendum),
            target.Addendum.Id, "addendum-voided");
        return new(VoidLegalArtifactOutcome.Voided, command.LeaseManagementId,
            null, target.Addendum.Id, now, null);
    }

    public Task AuthorizeReplayAsync(VoidLeaseAddendumCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeaseAddendumCommandSupport.AuthorizeReplayAsync(command, _db, ct);
}

public sealed class CloseTenantAccountRule
{
    private readonly RentalCommandDbContext _db;

    public CloseTenantAccountRule(RentalCommandDbContext db) => _db = db;

    public async Task<CloseTenantAccountResult> ExecuteAsync(
        CloseTenantAccountCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        Validate(command);
        await context.AcquireLockAsync("LeaseManagement", command.LeaseManagementId, ct);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var target = await LeaseAgreementDraftCommandSupport.AuthorizedRelationships(command, _db, now)
            .Select(relationship => new
            {
                Relationship = relationship,
                Account = relationship.TenantAccount,
                ReceivableBalance = _db.Set<TenantAccountBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.LeaseManagementId == command.LeaseManagementId
                        && balance.TenantAccountId == command.TenantAccountId)
                    .Select(balance => balance.ReceivableBalance).FirstOrDefault(),
                UnappliedCredit = _db.Set<TenantAccountBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.LeaseManagementId == command.LeaseManagementId
                        && balance.TenantAccountId == command.TenantAccountId)
                    .Select(balance => balance.UnappliedCredit).FirstOrDefault(),
                HeldDeposit = _db.Set<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.LeaseManagementId == command.LeaseManagementId
                        && balance.TenantAccountId == command.TenantAccountId)
                    .Select(balance => balance.HeldBalance).FirstOrDefault(),
                HasUnfinishedWorkflow = _db.Set<TenantPaymentAttempt>().Any(payment =>
                        payment.PortfolioId == command.PortfolioId
                        && payment.TenantAccountId == command.TenantAccountId
                        && payment.State != TenantPaymentAttemptState.Succeeded
                        && payment.State != TenantPaymentAttemptState.Failed
                        && payment.State != TenantPaymentAttemptState.Canceled)
                    || _db.Set<TenantAutopayEnrollment>().Any(enrollment =>
                        enrollment.PortfolioId == command.PortfolioId
                        && enrollment.TenantAccountId == command.TenantAccountId
                        && enrollment.CanceledAtUtc == null)
                    || _db.Set<SignatureRequest>().Any(request =>
                        request.PortfolioId == command.PortfolioId
                        && ((request.LeaseAgreementId != null && request.LeaseAgreement!.LeaseManagementId == command.LeaseManagementId)
                            || (request.LeaseAddendumId != null && request.LeaseAddendum!.LeaseManagementId == command.LeaseManagementId))
                        && request.Status != SignatureRequestStatus.Completed
                        && request.Status != SignatureRequestStatus.Declined
                        && request.Status != SignatureRequestStatus.Voided)
                    || relationship.Agreements.Any(agreement => agreement.IssuedAtUtc == null
                        && agreement.DraftCanceledAtUtc == null)
                    || relationship.Addenda.Any(addendum => addendum.IssuedAtUtc == null
                        && addendum.DraftCanceledAtUtc == null),
            })
            .SingleOrDefaultAsync(item => item.Account != null && item.Account.Id == command.TenantAccountId, ct)
            ?? throw new UnauthorizedAccessException("The Tenant Account is not authorized in the current property scope.");

        if (target.Account!.ClosedAtUtc.HasValue)
            return Error(CloseTenantAccountOutcome.AlreadyClosed, command, target.Account.ClosedAtUtc,
                "The Tenant Account is already closed.");
        if (target.Relationship.PossessionReturnedAtUtc == null)
            return Error(CloseTenantAccountOutcome.PossessionNotReturned, command, null,
                "Possession must be returned before the Tenant Account can close.");
        if (target.ReceivableBalance != 0 || target.UnappliedCredit != 0)
            return Error(CloseTenantAccountOutcome.NonzeroReceivableBalance, command, null,
                "Resolve all receivable balances and unapplied credits with explicit ledger commands before closing.");
        if (target.HeldDeposit != 0)
            return Error(CloseTenantAccountOutcome.NonzeroDepositBalance, command, null,
                "Refund, deduct, adjust, or transfer the held deposit before closing.");
        if (target.HasUnfinishedWorkflow)
            return Error(CloseTenantAccountOutcome.UnfinishedWorkflow, command, null,
                "Finish or cancel draft, signature, payment, and autopay workflows before closing.");

        target.Account.ClosedAtUtc = now;
        target.Account.CloseReasonCode = command.CloseReasonCode.Trim();
        target.Account.CloseNote = command.CloseNote?.Trim();
        target.Relationship.AccountClosedAtUtc = now;
        target.Relationship.UpdatedAtUtc = now;
        target.Relationship.RowVersion = Guid.NewGuid();
        context.BindSemanticAudit(target.Account, new AtomicSemanticAudit(command.PortfolioId,
            nameof(TenantAccount), target.Account.Id, AuditLogOperation.Updated, UserId: command.ActorUserId,
            ChangeReason: "Closed zero-balance Tenant Account after possession return."));
        context.BindSemanticAudit(target.Relationship, new AtomicSemanticAudit(command.PortfolioId,
            nameof(LeaseManagement), target.Relationship.Id, AuditLogOperation.Updated, UserId: command.ActorUserId,
            ChangeReason: "Closed lease relationship after Tenant Account settlement."));
        context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId, MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = nameof(TenantAccount),
                entityId = target.Account.Id, leaseManagementId = command.LeaseManagementId,
                action = "tenant-account-closed" }),
            IdempotencyKey = command.DeliveryIdempotencyKey, CreatedAtUtc = now, NextAttemptAtUtc = now,
        });
        return new(CloseTenantAccountOutcome.Closed, command.LeaseManagementId,
            target.Account.Id, now, null);
    }

    public Task AuthorizeReplayAsync(CloseTenantAccountCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        LeaseAgreementDraftCommandSupport.AuthorizeReplayAsync(command, _db, ct);

    private static void Validate(CloseTenantAccountCommand command)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        if (command.TenantAccountId <= 0 || string.IsNullOrWhiteSpace(command.CloseReasonCode)
            || command.CloseReasonCode.Trim().Length > 40 || command.CloseNote?.Trim().Length > 1000)
            throw new ArgumentException("Tenant Account and bounded close reason are required.");
    }

    private static CloseTenantAccountResult Error(CloseTenantAccountOutcome outcome,
        CloseTenantAccountCommand command, DateTime? closedAt, string error) =>
        new(outcome, command.LeaseManagementId, command.TenantAccountId, closedAt, error);
}

internal static class LegalArtifactCommandSupport
{
    internal static void Validate(ILeaseAgreementDraftCommand command, int artifactId)
    {
        LeaseAgreementDraftCommandSupport.ValidateAuthorizationShape(command);
        var reason = command switch
        {
            VoidLeaseAgreementCommand agreement => (agreement.VoidReasonCode, agreement.VoidNote),
            VoidLeaseAddendumCommand addendum => (addendum.VoidReasonCode, addendum.VoidNote),
            _ => throw new ArgumentException("Void command is invalid."),
        };
        if (artifactId <= 0 || string.IsNullOrWhiteSpace(reason.Item1)
            || reason.Item1.Trim().Length > 40 || reason.Item2?.Trim().Length > 2000)
            throw new ArgumentException("Legal artifact and bounded void reason are required.");
    }

    internal static async Task VoidOpenPacketAsync(RentalCommandDbContext db,
        int portfolioId, int? agreementId, int? addendumId,
        int actorUserId, DateTime now, IAtomicCommandContext context, CancellationToken ct)
    {
        var packet = await db.Set<SignatureRequest>()
            .SingleOrDefaultAsync(request => request.PortfolioId == portfolioId
                && request.LeaseAgreementId == agreementId && request.LeaseAddendumId == addendumId
                && request.Status != SignatureRequestStatus.Completed
                && request.Status != SignatureRequestStatus.Declined
                && request.Status != SignatureRequestStatus.Voided, ct);
        if (packet is null) return;
        packet.Status = SignatureRequestStatus.Voided;
        packet.VoidedAtUtc = now;
        packet.ExecutionClaimOwner = null;
        packet.ExecutionClaimToken = null;
        packet.ExecutionClaimExpiresAtUtc = null;
        db.Add(new SignatureAuditEvent
        {
            PortfolioId = portfolioId, SignatureRequestId = packet.Id,
            Type = SignatureAuditEventType.Voided, OccurredAtUtc = now,
            Detail = "Signature packet voided with its legal artifact.",
        });
        context.StageSemanticEvent(new AtomicSemanticAudit(portfolioId, nameof(SignatureRequest),
            packet.Id, AuditLogOperation.Updated, UserId: actorUserId,
            ChangeReason: "Voided open signature packet with legal artifact."), now);
    }

    internal static AtomicSemanticAudit Audit(ILeaseAgreementDraftCommand command,
        string type, int id, string reason) => new(command.PortfolioId, type, id,
        AuditLogOperation.Updated, UserId: command.ActorUserId, ChangeReason: reason);
    internal static void StageOutbox(IAtomicCommandContext context, ILeaseAgreementDraftCommand command,
        DateTime now, string type, int id, string action) => context.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId, MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType = type, entityId = id,
                leaseManagementId = command.LeaseManagementId, action }),
            IdempotencyKey = command.DeliveryIdempotencyKey, CreatedAtUtc = now, NextAttemptAtUtc = now,
        });
    internal static VoidLegalArtifactResult AgreementError(VoidLegalArtifactOutcome outcome,
        VoidLeaseAgreementCommand command, DateTime? voidedAt, string error) =>
        new(outcome, command.LeaseManagementId, command.LeaseAgreementId, null, voidedAt, error);
    internal static VoidLegalArtifactResult AddendumError(VoidLegalArtifactOutcome outcome,
        VoidLeaseAddendumCommand command, DateTime? voidedAt, string error) =>
        new(outcome, command.LeaseManagementId, null, command.LeaseAddendumId, voidedAt, error);
    internal static UnauthorizedAccessException Unauthorized(string kind) => new(
        $"The {kind} is not authorized in the current access context and property scope.");
}
