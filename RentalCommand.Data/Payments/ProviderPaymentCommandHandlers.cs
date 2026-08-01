using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;

namespace RentalCommand.Data.Payments;

public sealed class PrepareProviderPaymentCreateHandler
    : IAtomicCommandHandler<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult>
{
    private readonly RentalCommandDbContext _db;

    public PrepareProviderPaymentCreateHandler(RentalCommandDbContext db) => _db = db;

    public async Task<PrepareProviderPaymentCreateResult> HandleAsync(
        PrepareProviderPaymentCreateCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ProviderPaymentHandlerSupport.Validate(command.PortfolioId, command.TenantAccountId,
            command.ActorUserId, command.Provider, command.IdempotencyKey, command.Currency);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);

        var target = await (
            from balance in _db.Set<TenantChargeBalanceProjection>().AsNoTracking()
            join entry in _db.Set<TenantLedgerEntry>().AsNoTracking()
                on new { Id = balance.TenantLedgerEntryId, balance.PortfolioId, balance.TenantAccountId }
                equals new { entry.Id, entry.PortfolioId, entry.TenantAccountId }
            join account in _db.Set<TenantAccount>().AsNoTracking()
                on new { Id = entry.TenantAccountId, entry.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            where entry.Id == command.ChargeLedgerEntryId
                && entry.PortfolioId == command.PortfolioId
                && entry.TenantAccountId == command.TenantAccountId
                && entry.Direction == TenantLedgerDirection.Debit
                && balance.OpenAmount > 0m
                && account.ClosedAtUtc == null
                && _db.Set<ApplicationUser>().Any(user => user.Id == command.ActorUserId)
                && (command.TenantId == null || _db.Set<LeaseManagementParty>().Any(party =>
                    party.LeaseManagementId == account.LeaseManagementId
                    && party.PortfolioId == command.PortfolioId
                    && party.TenantId == command.TenantId
                    && party.EffectiveFrom <= times.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= times.BusinessDate)
                    && party.Role != LeaseManagementPartyRole.Occupant
                    && party.UserAccesses.Any(access => access.ApplicationUserId == command.ActorUserId
                        && access.PortfolioId == command.PortfolioId && access.RevokedAtUtc == null)))
                && (command.AutopayEnrollmentId == null || _db.Set<TenantAutopayEnrollment>().Any(enrollment =>
                    enrollment.Id == command.AutopayEnrollmentId
                    && enrollment.PortfolioId == command.PortfolioId
                    && enrollment.TenantAccountId == command.TenantAccountId
                    && enrollment.Provider == command.Provider
                    && enrollment.CanceledAtUtc == null))
            select new
            {
                account.Id,
                Amount = balance.OpenAmount,
                account.Currency,
                ProviderCustomerId = _db.Set<TenantAutopayEnrollment>()
                    .Where(enrollment => enrollment.Id == command.AutopayEnrollmentId)
                    .Select(enrollment => enrollment.ProviderCustomerId)
                    .FirstOrDefault(),
                ProviderPaymentMethodId = _db.Set<TenantAutopayEnrollment>()
                    .Where(enrollment => enrollment.Id == command.AutopayEnrollmentId)
                    .Select(enrollment => enrollment.ProviderPaymentMethodId)
                    .FirstOrDefault(),
            }).SingleOrDefaultAsync(ct);

        if (target is null)
            return new(PrepareProviderPaymentCreateOutcome.NotFound, command.PortfolioId,
                command.TenantAccountId, command.ChargeLedgerEntryId, 0, 0, command.Currency,
                command.Provider, command.IdempotencyKey, null, null);
        if (!string.Equals(target.Currency, command.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provider currency must match the tenant account currency.");

        var paymentAttempt = new TenantPaymentAttempt
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            ChargeLedgerEntryId = command.ChargeLedgerEntryId,
            Provider = command.Provider,
            IdempotencyKey = command.IdempotencyKey,
            AttemptType = TenantPaymentAttemptType.Charge,
            State = TenantPaymentAttemptState.Prepared,
            Amount = target.Amount,
            Currency = target.Currency,
            PreparedAtUtc = times.WallClockUtc,
            UpdatedAtUtc = times.WallClockUtc,
            NextAttemptAtUtc = times.WallClockUtc,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(paymentAttempt);
        await context.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(
            context, paymentAttempt, "Provider payment context prepared", userId: command.ActorUserId);

        return new(PrepareProviderPaymentCreateOutcome.Prepared, command.PortfolioId,
            command.TenantAccountId, command.ChargeLedgerEntryId, paymentAttempt.Id,
            paymentAttempt.Amount, paymentAttempt.Currency, paymentAttempt.Provider,
            paymentAttempt.IdempotencyKey, target.ProviderCustomerId, target.ProviderPaymentMethodId);
    }

    public Task AuthorizeReplayAsync(
        PrepareProviderPaymentCreateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizePaymentPrepareReplayAsync(command, _db, ct);
}

public sealed class PrepareProviderAutopaySetupHandler
    : IAtomicCommandHandler<PrepareProviderAutopaySetupCommand, PrepareProviderAutopaySetupResult>
{
    private readonly RentalCommandDbContext _db;

    public PrepareProviderAutopaySetupHandler(RentalCommandDbContext db) => _db = db;

    public async Task<PrepareProviderAutopaySetupResult> HandleAsync(
        PrepareProviderAutopaySetupCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        ProviderPaymentHandlerSupport.Validate(command.PortfolioId, command.TenantAccountId,
            command.ActorUserId, command.Provider, command.IdempotencyKey, command.Currency);
        await context.AcquireLockAsync("TenantAccount", command.TenantAccountId, ct);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);

        var target = await (
            from account in _db.Set<TenantAccount>().AsNoTracking()
            join party in _db.Set<LeaseManagementParty>().AsNoTracking()
                on new { account.LeaseManagementId, account.PortfolioId }
                equals new { party.LeaseManagementId, party.PortfolioId }
            join access in _db.Set<TenantUserAccess>().AsNoTracking()
                on new { LeaseManagementPartyId = party.Id, party.PortfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            where account.Id == command.TenantAccountId
                && account.PortfolioId == command.PortfolioId
                && account.ClosedAtUtc == null
                && party.TenantId == command.TenantId
                && party.EffectiveFrom <= times.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= times.BusinessDate)
                && party.Role != LeaseManagementPartyRole.Occupant
                && access.ApplicationUserId == command.ActorUserId
                && access.RevokedAtUtc == null
            orderby party.Id
            select new { account.Id, account.Currency, AuthorizingPartyId = party.Id })
            .FirstOrDefaultAsync(ct);
        if (target is null)
            return new(PrepareProviderAutopaySetupOutcome.NotFound, command.PortfolioId,
                command.TenantAccountId, 0, command.ActorUserId, 0, command.Provider,
                command.IdempotencyKey);
        if (!string.Equals(target.Currency, command.Currency, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Provider currency must match the tenant account currency.");

        var verification = new TenantPaymentAttempt
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            Provider = command.Provider,
            IdempotencyKey = command.IdempotencyKey,
            AttemptType = TenantPaymentAttemptType.Verification,
            State = TenantPaymentAttemptState.Prepared,
            Amount = 0m,
            Currency = target.Currency,
            PreparedAtUtc = times.WallClockUtc,
            UpdatedAtUtc = times.WallClockUtc,
            NextAttemptAtUtc = times.WallClockUtc,
            CreatedByUserId = command.ActorUserId,
        };
        _db.Add(verification);
        await context.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(
            context, verification, "Provider autopay setup prepared", userId: command.ActorUserId);
        return new(PrepareProviderAutopaySetupOutcome.Prepared, command.PortfolioId,
            command.TenantAccountId, target.AuthorizingPartyId, command.ActorUserId,
            verification.Id, command.Provider, command.IdempotencyKey);
    }

    public Task AuthorizeReplayAsync(
        PrepareProviderAutopaySetupCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeAutopaySetupReplayAsync(command, _db, ct);
}

public sealed class FinalizeProviderPaymentCreateHandler
    : IAtomicCommandHandler<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult>
{
    private readonly RentalCommandDbContext _db;

    public FinalizeProviderPaymentCreateHandler(RentalCommandDbContext db) => _db = db;

    public async Task<FinalizeProviderPaymentCreateResult> HandleAsync(
        FinalizeProviderPaymentCreateCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var paymentAttempt = await _db.Set<TenantPaymentAttempt>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.PaymentAttemptId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.TenantAccountId == command.TenantAccountId
                && candidate.Provider == command.Provider
                && candidate.IdempotencyKey == command.IdempotencyKey, ct);
        if (paymentAttempt is null)
            return new(FinalizeProviderPaymentCreateOutcome.NotFound, command.PortfolioId,
                command.TenantAccountId, command.PaymentAttemptId, command.Provider,
                command.ProviderPaymentId, command.State);

        if (!string.IsNullOrWhiteSpace(paymentAttempt.ProviderObjectId))
        {
            if (!string.Equals(paymentAttempt.ProviderObjectId, command.ProviderPaymentId, StringComparison.Ordinal))
                throw new AtomicReceiptInvariantException(
                    $"Tenant payment context {paymentAttempt.Id} is already bound to another provider object.");
        }

        var stateResult = await ProviderPaymentHandlerSupport.ApplyStateAsync(
            _db, paymentAttempt, command.State, command.ProviderPaymentId, null,
            command.FailureReason, null, "provider-create-finalize", context, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
            throw new AtomicReceiptInvariantException(stateResult.ConflictReason!);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(
                context, paymentAttempt, "Provider payment context finalized",
                actorLabel: "provider:create-finalize");

        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? FinalizeProviderPaymentCreateOutcome.Applied
                : FinalizeProviderPaymentCreateOutcome.AlreadyFinalized,
            paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, paymentAttempt.Id, paymentAttempt.Provider,
            command.ProviderPaymentId, paymentAttempt.State);
    }

    public Task AuthorizeReplayAsync(
        FinalizeProviderPaymentCreateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeSystemAttemptReplayAsync(
            command.PaymentAttemptId, command.TenantAccountId, command.PortfolioId,
            command.Provider, command.IdempotencyKey, _db, ct);
}

public sealed class FailProviderPaymentCreateHandler
    : IAtomicCommandHandler<FailProviderPaymentCreateCommand, FailProviderPaymentCreateResult>
{
    private readonly RentalCommandDbContext _db;

    public FailProviderPaymentCreateHandler(RentalCommandDbContext db) => _db = db;

    public async Task<FailProviderPaymentCreateResult> HandleAsync(
        FailProviderPaymentCreateCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var paymentAttempt = await _db.Set<TenantPaymentAttempt>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Id == command.PaymentAttemptId
                && candidate.PortfolioId == command.PortfolioId
                && candidate.TenantAccountId == command.TenantAccountId
                && candidate.Provider == command.Provider
                && candidate.IdempotencyKey == command.IdempotencyKey, ct);
        if (paymentAttempt is null)
            return new(false, command.PortfolioId, command.TenantAccountId,
                command.PaymentAttemptId, TenantPaymentAttemptState.Unknown);
        if (paymentAttempt.State == TenantPaymentAttemptState.Succeeded)
            return new(true, paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
                paymentAttempt.Id, paymentAttempt.State);

        var databaseNow = await context.ReadDatabaseClockUtcAsync(ct);
        var stateResult = await ProviderPaymentHandlerSupport.ApplyStateAsync(
            _db, paymentAttempt, TenantPaymentAttemptState.Failed, paymentAttempt.ProviderObjectId,
            command.FailureCode, command.FailureReason, databaseNow,
            "provider-create-failure", context, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
            throw new AtomicReceiptInvariantException(stateResult.ConflictReason!);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(
                context, paymentAttempt, "Provider payment context failed",
                actorLabel: "provider:create-failure");
        return new(true, paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
            paymentAttempt.Id, paymentAttempt.State);
    }

    public Task AuthorizeReplayAsync(
        FailProviderPaymentCreateCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeSystemAttemptReplayAsync(
            command.PaymentAttemptId, command.TenantAccountId, command.PortfolioId,
            command.Provider, command.IdempotencyKey, _db, ct);
}

public sealed class RecordVerifiedProviderPaymentEventHandler
    : IAtomicCommandHandler<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult>
{
    private readonly RentalCommandDbContext _db;

    public RecordVerifiedProviderPaymentEventHandler(RentalCommandDbContext db) => _db = db;

    public async Task<RecordVerifiedProviderPaymentEventResult> HandleAsync(
        RecordVerifiedProviderPaymentEventCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var existing = await (
            from candidate in _db.Set<ProviderInboxEvent>().AsNoTracking()
            join providerAttempt in _db.Set<TenantPaymentAttempt>().AsNoTracking()
                on new { candidate.Provider, ProviderObjectId = candidate.ProviderObjectId }
                equals new { providerAttempt.Provider, ProviderObjectId = (string?)providerAttempt.ProviderObjectId }
                into matchingAttempts
            from providerAttempt in matchingAttempts.DefaultIfEmpty()
            where candidate.Provider == command.Provider && candidate.ProviderEventId == command.ProviderEventId
            select new RecordVerifiedProviderPaymentEventResult(
                RecordProviderPaymentEventOutcome.Duplicate, candidate.Id, candidate.PortfolioId,
                providerAttempt == null ? null : providerAttempt.TenantAccountId,
                providerAttempt == null ? null : providerAttempt.Id,
                providerAttempt == null ? null : providerAttempt.State)).SingleOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var now = await context.ReadDatabaseClockUtcAsync(ct);
        var inbox = ProviderPaymentHandlerSupport.Inbox(command, now);
        _db.Add(inbox);
        if (command.EventKind == ProviderPaymentEventKind.Ignored)
        {
            ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
            await context.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Applied, inbox.Id, null, null, null, null);
        }
        if (command.EventKind == ProviderPaymentEventKind.SetupCompleted)
            return await ApplySetupCompletedAsync(command, context, inbox, now, ct);
        if (string.IsNullOrWhiteSpace(command.ProviderPaymentId))
            return await ProviderPaymentHandlerSupport.DeadLetterAsync(inbox,
                "Verified event did not identify a provider payment object.", now, context, ct);

        var paymentAttempt = await _db.Set<TenantPaymentAttempt>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Provider == command.Provider
                && candidate.ProviderObjectId == command.ProviderPaymentId, ct);
        if (paymentAttempt is null)
        {
            inbox.FailureKind = ProviderInboxFailureKind.Unmatched;
            inbox.LastError = "No canonical tenant payment context matched this verified event.";
            await context.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Unmatched, inbox.Id, null, null, null, null);
        }
        if (!ProviderPaymentHandlerSupport.MatchesReceipt(paymentAttempt, command.Amount, command.Currency))
            return await ProviderPaymentHandlerSupport.DeadLetterAsync(inbox,
                "Provider amount or currency did not match the durable tenant payment context.",
                now, context, ct, paymentAttempt);

        inbox.PortfolioId = paymentAttempt.PortfolioId;
        var stateResult = await ProviderPaymentHandlerSupport.ApplyEventAsync(
            _db, paymentAttempt, command.EventKind, command.FailureReason,
            $"provider-event:{command.ProviderEventId}", context, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
        {
            ProviderPaymentHandlerSupport.DeadLetterInbox(inbox, stateResult.ConflictReason!, now);
            await context.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Conflict, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
                paymentAttempt.Id, paymentAttempt.State);
        }

        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(context, paymentAttempt,
                $"Verified provider event {command.ProviderEventId} reconciled",
                actorLabel: $"provider:webhook:{command.Provider}");
        await context.FlushBusinessAsync(ct);
        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? RecordProviderPaymentEventOutcome.Applied
                : RecordProviderPaymentEventOutcome.AlreadyInState,
            inbox.Id, paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, paymentAttempt.Id, paymentAttempt.State);
    }

    private async Task<RecordVerifiedProviderPaymentEventResult> ApplySetupCompletedAsync(
        RecordVerifiedProviderPaymentEventCommand command, IAtomicCommandContext context,
        ProviderInboxEvent inbox, DateTime now, CancellationToken ct)
    {
        if (command.EnrollmentPortfolioId is not int portfolioId
            || command.EnrollmentTenantAccountId is not int accountId
            || command.EnrollmentAuthorizingPartyId is not int partyId
            || command.EnrollmentActorUserId is not int actorUserId
            || command.EnrollmentPaymentAttemptId is not long attemptId
            || string.IsNullOrWhiteSpace(command.ProviderCustomerId)
            || string.IsNullOrWhiteSpace(command.ProviderPaymentMethodId))
            return await ProviderPaymentHandlerSupport.DeadLetterAsync(inbox,
                "Verified setup event did not contain complete canonical enrollment facts.", now, context, ct);

        await context.AcquireLockAsync("TenantAccount", accountId, ct);
        var target = await (
            from paymentAttempt in _db.Set<TenantPaymentAttempt>().AsNoTracking()
            join account in _db.Set<TenantAccount>().AsNoTracking()
                on new { Id = paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            join party in _db.Set<LeaseManagementParty>().AsNoTracking()
                on new { account.LeaseManagementId, account.PortfolioId }
                equals new { party.LeaseManagementId, party.PortfolioId }
            where paymentAttempt.Id == attemptId && paymentAttempt.PortfolioId == portfolioId
                && paymentAttempt.TenantAccountId == accountId && paymentAttempt.Provider == command.Provider
                && paymentAttempt.ProviderObjectId == command.ProviderPaymentId
                && paymentAttempt.AttemptType == TenantPaymentAttemptType.Verification
                && paymentAttempt.CreatedByUserId == actorUserId
                && party.Id == partyId && party.Role != LeaseManagementPartyRole.Occupant
                && party.UserAccesses.Any(access => access.ApplicationUserId == actorUserId
                    && access.PortfolioId == portfolioId && access.RevokedAtUtc == null)
            select new { PaymentAttempt = paymentAttempt, Account = account, Party = party })
            .SingleOrDefaultAsync(ct);
        if (target is null)
            return await ProviderPaymentHandlerSupport.DeadLetterAsync(inbox,
                "Verified setup event did not match its durable account, party, and verification context.",
                now, context, ct);

        var stateResult = await ProviderPaymentHandlerSupport.ApplyStateAsync(
            _db, target.PaymentAttempt, TenantPaymentAttemptState.Succeeded,
            target.PaymentAttempt.ProviderObjectId, null, null, null,
            $"provider-setup:{command.ProviderEventId}", context, ct);
        var completedAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
        {
            ProviderPaymentHandlerSupport.DeadLetterInbox(inbox, stateResult.ConflictReason!, now);
            await context.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Conflict, inbox.Id, portfolioId,
                accountId, completedAttempt.Id, completedAttempt.State);
        }

        var current = await _db.Set<TenantAutopayEnrollment>()
            .SingleOrDefaultAsync(enrollment => enrollment.TenantAccountId == accountId
                && enrollment.PortfolioId == portfolioId && enrollment.CanceledAtUtc == null, ct);
        if (current is not null && (!string.Equals(current.Provider, command.Provider, StringComparison.Ordinal)
            || !string.Equals(current.ProviderCustomerId, command.ProviderCustomerId, StringComparison.Ordinal)
            || !string.Equals(current.ProviderPaymentMethodId, command.ProviderPaymentMethodId, StringComparison.Ordinal)))
        {
            current.CanceledAtUtc = now;
            current.CancelReason = "Replaced by a verified provider setup.";
            await context.FlushBusinessAsync(ct);
            current = null;
        }
        if (current is null)
        {
            current = new TenantAutopayEnrollment
            {
                PortfolioId = portfolioId,
                TenantAccountId = accountId,
                AuthorizingPartyId = partyId,
                Provider = command.Provider,
                ProviderCustomerId = command.ProviderCustomerId,
                ProviderPaymentMethodId = command.ProviderPaymentMethodId,
                EnrolledAtUtc = now,
                CreatedByUserId = actorUserId,
            };
            _db.Add(current);
        }

        inbox.PortfolioId = portfolioId;
        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(context, completedAttempt,
                $"Verified provider setup {command.ProviderEventId} enrolled autopay",
                actorLabel: $"provider:webhook:{command.Provider}");
        await context.FlushBusinessAsync(ct);
        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? RecordProviderPaymentEventOutcome.Applied
                : RecordProviderPaymentEventOutcome.AlreadyInState,
            inbox.Id, portfolioId, accountId, completedAttempt.Id, completedAttempt.State);
    }

    public Task AuthorizeReplayAsync(
        RecordVerifiedProviderPaymentEventCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeVerifiedEventReplayAsync(
            command.Provider, command.ProviderEventId, _db, ct);
}

public sealed class ReconcileClaimedProviderPaymentEventHandler
    : IAtomicCommandHandler<ReconcileClaimedProviderPaymentEventCommand, ReconcileClaimedProviderPaymentEventResult>
{
    private readonly RentalCommandDbContext _db;

    public ReconcileClaimedProviderPaymentEventHandler(RentalCommandDbContext db) => _db = db;

    internal const int MaximumAttempts = 8;

    public async Task<ReconcileClaimedProviderPaymentEventResult> HandleAsync(
        ReconcileClaimedProviderPaymentEventCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        var inbox = await AtomicProviderInboxPersistence.LockOwnedAsync(_db,
            context, command.ProviderInboxEventId, command.ClaimOwner, command.ClaimToken, ct)
            ?? throw new AtomicReceiptInvariantException(
                $"Provider inbox event {command.ProviderInboxEventId} is not owned by the supplied fenced claim.");
        if (inbox.EventKind is ProviderPaymentEventKind.SetupCompleted or ProviderPaymentEventKind.Ignored
            || string.IsNullOrWhiteSpace(inbox.ProviderObjectId))
            throw new AtomicReceiptInvariantException($"Provider inbox event {inbox.Id} is not replayable payment work.");

        var paymentAttempt = await _db.Set<TenantPaymentAttempt>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Provider == inbox.Provider
                && candidate.ProviderObjectId == inbox.ProviderObjectId, ct);
        var now = await context.ReadDatabaseClockUtcAsync(ct);
        if (paymentAttempt is null)
            return await ReleaseUnmatchedAsync(inbox, now, context, ct);
        if (!ProviderPaymentHandlerSupport.MatchesReceipt(paymentAttempt, inbox.Amount, inbox.Currency))
        {
            inbox.DeadLetteredAtUtc = now;
            inbox.FailureKind = ProviderInboxFailureKind.Permanent;
            inbox.LastError = "Provider amount or currency did not match the durable tenant payment context.";
            ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
            await context.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.DeadLettered, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId, paymentAttempt.Id,
                paymentAttempt.State, null);
        }

        inbox.PortfolioId = paymentAttempt.PortfolioId;
        var stateResult = await ProviderPaymentHandlerSupport.ApplyEventAsync(
            _db, paymentAttempt, inbox.EventKind, inbox.FailureReason,
            $"provider-inbox:{inbox.Id}:{command.ClaimOwner}", context, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
        {
            ProviderPaymentHandlerSupport.DeadLetterInbox(inbox, stateResult.ConflictReason!, now);
            ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
            await context.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.Conflict, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
                paymentAttempt.Id, paymentAttempt.State, null);
        }

        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(context, paymentAttempt,
                $"Claimed provider event {inbox.ProviderEventId} reconciled",
                actorLabel: $"provider:worker:{command.ClaimOwner}");
        await context.FlushBusinessAsync(ct);
        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? ReconcileProviderPaymentEventOutcome.Applied
                : ReconcileProviderPaymentEventOutcome.AlreadyInState,
            inbox.Id,
            paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId, paymentAttempt.Id,
            paymentAttempt.State, null);
    }

    private static async Task<ReconcileClaimedProviderPaymentEventResult> ReleaseUnmatchedAsync(
        ProviderInboxEvent inbox, DateTime now, IAtomicCommandContext context, CancellationToken ct)
    {
        inbox.FailureKind = ProviderInboxFailureKind.Unmatched;
        inbox.LastError = "No canonical tenant payment context matched this verified event.";
        ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
        if (inbox.AttemptCount >= MaximumAttempts)
        {
            inbox.DeadLetteredAtUtc = now;
            await context.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.DeadLettered, inbox.Id,
                inbox.PortfolioId, null, null, null, null);
        }
        inbox.NextAttemptAtUtc = now.Add(inbox.AttemptCount switch
        {
            <= 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15), 4 => TimeSpan.FromHours(1),
            5 => TimeSpan.FromHours(3), 6 => TimeSpan.FromHours(12), _ => TimeSpan.FromDays(1),
        });
        await context.FlushBusinessAsync(ct);
        return new(ReconcileProviderPaymentEventOutcome.RetryScheduled, inbox.Id,
            inbox.PortfolioId, null, null, null, inbox.NextAttemptAtUtc);
    }

    public Task AuthorizeReplayAsync(
        ReconcileClaimedProviderPaymentEventCommand command, IAtomicCommandContext context, CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeReconcileReplayAsync(
            command.ProviderInboxEventId, _db, ct);
}

internal enum ProviderPaymentApplyDisposition
{
    Applied,
    AlreadyInState,
    Conflict,
}

internal sealed record ProviderPaymentApplyResult(
    TenantPaymentAttempt Attempt,
    ProviderPaymentApplyDisposition Disposition,
    string? ConflictReason = null);

internal static class ProviderPaymentHandlerSupport
{
    internal static void Validate(int portfolioId, int tenantAccountId, int actorUserId,
        string provider, string key, string currency)
    {
        if (portfolioId <= 0 || tenantAccountId <= 0 || actorUserId <= 0
            || string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(key)
            || key.Length > 200 || currency.Length != 3)
            throw new ArgumentException("Portfolio, account, actor, provider, currency, and idempotency key are required.");
    }

    internal static ProviderInboxEvent Inbox(
        RecordVerifiedProviderPaymentEventCommand command, DateTime databaseNow) => new()
    {
        Provider = command.Provider,
        ProviderEventId = command.ProviderEventId,
        EventType = command.ProviderEventType,
        Payload = command.PayloadJson,
        ProviderObjectId = command.ProviderPaymentId,
        EventKind = command.EventKind,
        Amount = command.Amount,
        Currency = command.Currency,
        FailureReason = command.FailureReason,
        OccurredAtUtc = command.OccurredAtUtc,
        ReceivedAtUtc = databaseNow,
        NextAttemptAtUtc = databaseNow,
    };

    internal static bool MatchesReceipt(TenantPaymentAttempt paymentAttempt, decimal? amount, string? currency) =>
        paymentAttempt.AttemptType == TenantPaymentAttemptType.Verification
        || ((amount == null || amount == paymentAttempt.Amount)
            && (string.IsNullOrWhiteSpace(currency)
                || string.Equals(currency, paymentAttempt.Currency, StringComparison.OrdinalIgnoreCase)));

    internal static async Task<ProviderPaymentApplyResult> ApplyEventAsync(
        RentalCommandDbContext db,
        TenantPaymentAttempt paymentAttempt,
        ProviderPaymentEventKind kind, string? failureReason, string claimOwner,
        IAtomicCommandContext context, CancellationToken ct)
    {
        var state = kind switch
        {
            ProviderPaymentEventKind.Pending => TenantPaymentAttemptState.Submitted,
            ProviderPaymentEventKind.Succeeded => TenantPaymentAttemptState.Succeeded,
            ProviderPaymentEventKind.Failed => TenantPaymentAttemptState.Failed,
            ProviderPaymentEventKind.Canceled => TenantPaymentAttemptState.Canceled,
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        if (paymentAttempt.State == TenantPaymentAttemptState.Failed
            && state == TenantPaymentAttemptState.Submitted)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Stale pending event cannot regress failed payment context {paymentAttempt.Id}.");

        return await ApplyStateAsync(db, paymentAttempt, state, paymentAttempt.ProviderObjectId, null,
            state == TenantPaymentAttemptState.Succeeded ? null : failureReason,
            state is TenantPaymentAttemptState.Failed or TenantPaymentAttemptState.Unknown
                ? paymentAttempt.NextAttemptAtUtc
                : null,
            claimOwner, context, ct);
    }

    internal static async Task<ProviderPaymentApplyResult> ApplyStateAsync(
        RentalCommandDbContext db,
        TenantPaymentAttempt paymentAttempt,
        TenantPaymentAttemptState state, string? providerObjectId, string? failureCode,
        string? failureReason, DateTime? nextAttemptAtUtc, string claimOwner,
        IAtomicCommandContext context, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(paymentAttempt.ProviderObjectId)
            && !string.IsNullOrWhiteSpace(providerObjectId)
            && !string.Equals(paymentAttempt.ProviderObjectId, providerObjectId, StringComparison.Ordinal))
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Tenant payment context {paymentAttempt.Id} is already bound to another provider object.");
        if (paymentAttempt.State == state)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.AlreadyInState);
        if (paymentAttempt.State == TenantPaymentAttemptState.Failed
            && state == TenantPaymentAttemptState.Submitted)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Payment context {paymentAttempt.Id} requires an explicit retry command before it can be submitted again.");
        if (paymentAttempt.State is TenantPaymentAttemptState.Succeeded or TenantPaymentAttemptState.Canceled)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Terminal payment context {paymentAttempt.Id} cannot transition from {paymentAttempt.State} to {state}.");

        (long ChargeLedgerEntryId, decimal OpenAmount)? receiptTarget = null;
        if (state == TenantPaymentAttemptState.Succeeded
            && paymentAttempt.AttemptType != TenantPaymentAttemptType.Verification)
        {
            await context.AcquireLockAsync(
                "TenantAccount", paymentAttempt.TenantAccountId, ct);
            receiptTarget = await ValidateReceiptTargetAsync(db, paymentAttempt, context, ct);
        }

        var claimToken = await AtomicProviderPaymentPersistence.ClaimExactAsync(db,
            context, paymentAttempt.Id, paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId,
            claimOwner, ct);
        if (claimToken is null)
        {
            var current = await db.Set<TenantPaymentAttempt>().AsNoTracking()
                .SingleAsync(candidate => candidate.Id == paymentAttempt.Id
                    && candidate.TenantAccountId == paymentAttempt.TenantAccountId
                    && candidate.PortfolioId == paymentAttempt.PortfolioId, ct);
            if (current.State == state)
                return new(current, ProviderPaymentApplyDisposition.AlreadyInState);
            if (current.State is TenantPaymentAttemptState.Succeeded or TenantPaymentAttemptState.Canceled)
                return new(current, ProviderPaymentApplyDisposition.Conflict,
                    $"Terminal payment context {current.Id} cannot transition from {current.State} to {state}.");
            throw new AtomicReceiptInvariantException(
                $"Tenant payment context {paymentAttempt.Id} could not acquire its database claim.");
        }

        long? receiptId = null;
        DateTime? outboxAtUtc = null;
        if (state == TenantPaymentAttemptState.Succeeded
            && paymentAttempt.AttemptType != TenantPaymentAttemptType.Verification)
        {
            (receiptId, outboxAtUtc) = await PostReceiptAsync(
                db, paymentAttempt, receiptTarget!.Value, context, ct);
        }

        var applied = await AtomicProviderPaymentPersistence.TransitionAsync(db,
            context, paymentAttempt.Id, paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId,
            claimToken.Value, state, providerObjectId, failureCode, failureReason,
            nextAttemptAtUtc, ct);
        if (!applied)
            throw new AtomicReceiptInvariantException(
                $"Tenant payment context {paymentAttempt.Id} lost its fenced database claim.");

        var updated = await db.Set<TenantPaymentAttempt>().AsNoTracking()
            .SingleAsync(candidate => candidate.Id == paymentAttempt.Id
                && candidate.TenantAccountId == paymentAttempt.TenantAccountId
                && candidate.PortfolioId == paymentAttempt.PortfolioId, ct);
        if (receiptId is long postedReceiptId && outboxAtUtc is DateTime postedAtUtc)
            StageOutbox(context, updated, postedReceiptId, postedAtUtc);
        return new(updated, ProviderPaymentApplyDisposition.Applied);
    }

    private static async Task<(long ChargeLedgerEntryId, decimal OpenAmount)> ValidateReceiptTargetAsync(
        RentalCommandDbContext db,
        TenantPaymentAttempt paymentAttempt,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        if (paymentAttempt.AttemptType != TenantPaymentAttemptType.Charge)
            throw new AtomicReceiptInvariantException(
                $"Provider receipt finalization does not support {paymentAttempt.AttemptType} attempts.");
        if (paymentAttempt.ChargeLedgerEntryId is not long targetChargeEntryId)
            throw new AtomicReceiptInvariantException(
                $"Provider charge context {paymentAttempt.Id} has no durable target charge.");

        // Revalidate the immutable intent after taking the account lock and before adding the
        // receipt. A fully settled target remains valid; in that case the entire provider receipt
        // is unapplied. A missing, reversed, cross-account, or non-debit target fails closed.
        var target = await (
            from entry in db.Set<TenantLedgerEntry>().AsNoTracking()
            join balance in db.Set<TenantChargeBalanceProjection>().AsNoTracking()
                on new { Id = entry.Id, entry.PortfolioId, entry.TenantAccountId }
                equals new { Id = balance.TenantLedgerEntryId, balance.PortfolioId, balance.TenantAccountId }
            where entry.Id == targetChargeEntryId
                && entry.PortfolioId == paymentAttempt.PortfolioId
                && entry.TenantAccountId == paymentAttempt.TenantAccountId
                && entry.Direction == TenantLedgerDirection.Debit
                && entry.EntryType != TenantLedgerEntryType.Refund
                && entry.EntryType != TenantLedgerEntryType.Reversal
                && entry.EntryType != TenantLedgerEntryType.TransferOut
                && entry.Currency == paymentAttempt.Currency
                && balance.ReversedAmount == 0m
            select new { balance.OpenAmount }).SingleOrDefaultAsync(ct);
        if (target is null)
            throw new AtomicReceiptInvariantException(
                $"Provider charge context {paymentAttempt.Id} no longer identifies a valid target charge.");
        return (targetChargeEntryId, target.OpenAmount);
    }

    private static async Task<(long? ReceiptId, DateTime? PostedAtUtc)> PostReceiptAsync(
        RentalCommandDbContext db,
        TenantPaymentAttempt paymentAttempt,
        (long ChargeLedgerEntryId, decimal OpenAmount) target,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        var alreadyPosted = await db.Set<TenantLedgerEntry>().AsNoTracking()
            .Where(entry => entry.ProviderPaymentAttemptId == paymentAttempt.Id)
            .Select(entry => new { entry.Id, entry.PostedAtUtc })
            .SingleOrDefaultAsync(ct);
        if (alreadyPosted is not null)
            return (alreadyPosted.Id, alreadyPosted.PostedAtUtc);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(db, paymentAttempt.PortfolioId, ct);
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = paymentAttempt.PortfolioId,
            TenantAccountId = paymentAttempt.TenantAccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = paymentAttempt.Amount,
            Currency = paymentAttempt.Currency,
            EffectiveOn = times.BusinessDate,
            PostedAtUtc = times.WallClockUtc,
            Description = $"{paymentAttempt.Provider} payment receipt",
            BusinessKey = $"provider-receipt:{paymentAttempt.Id}",
            ProviderPaymentAttemptId = paymentAttempt.Id,
            CreatedByUserId = paymentAttempt.CreatedByUserId,
        };
        db.Add(receipt);
        await context.FlushBusinessAsync(ct);
        await TenantAccountingPosting.PostTenantReceiptAsync(
            db,
            context,
            receipt,
            paymentAttempt.CreatedByUserId,
            cashSystemKey: "undeposited-funds",
            ct);
        var allocation = await TenantMoneyPersistence.AllocateTargetChargeAsync(db, context,
            paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, receipt.Id, target.ChargeLedgerEntryId, receipt.Amount,
            $"provider-receipt:{paymentAttempt.Id}:allocation", paymentAttempt.CreatedByUserId,
            times.WallClockUtc, ct, spillToOtherCharges: false);
        var expectedAllocation = Math.Min(target.OpenAmount, receipt.Amount);
        if (allocation.AllocatedAmount != expectedAllocation
            || allocation.AllocationCount != (expectedAllocation > 0m ? 1 : 0))
            throw new AtomicReceiptInvariantException(
                $"Provider charge context {paymentAttempt.Id} did not settle its exact target.");
        return (receipt.Id, times.WallClockUtc);
    }

    internal static void CompleteInbox(ProviderInboxEvent inbox, DateTime now)
    {
        inbox.AttemptCount = Math.Max(1, inbox.AttemptCount);
        inbox.LastAttemptAtUtc = now;
        inbox.ProcessedAtUtc = now;
        inbox.NextAttemptAtUtc = now;
        inbox.FailureKind = null;
        inbox.LastError = null;
    }

    internal static void DeadLetterInbox(ProviderInboxEvent inbox, string reason, DateTime now)
    {
        inbox.FailureKind = ProviderInboxFailureKind.Permanent;
        inbox.LastError = reason;
        inbox.DeadLetteredAtUtc = now;
        inbox.NextAttemptAtUtc = now;
    }

    internal static void ReleaseClaim(ProviderInboxEvent inbox)
    {
        inbox.ClaimOwner = null;
        inbox.ClaimToken = null;
        inbox.ClaimExpiresAtUtc = null;
    }

    internal static async Task<RecordVerifiedProviderPaymentEventResult> DeadLetterAsync(
        ProviderInboxEvent inbox, string reason, DateTime now, IAtomicCommandContext context,
        CancellationToken ct, TenantPaymentAttempt? paymentAttempt = null)
    {
        inbox.PortfolioId = paymentAttempt?.PortfolioId ?? inbox.PortfolioId;
        inbox.FailureKind = ProviderInboxFailureKind.Permanent;
        inbox.LastError = reason;
        inbox.DeadLetteredAtUtc = now;
        await context.FlushBusinessAsync(ct);
        return new(RecordProviderPaymentEventOutcome.Unmatched, inbox.Id, inbox.PortfolioId,
            paymentAttempt?.TenantAccountId, paymentAttempt?.Id, paymentAttempt?.State);
    }

    internal static void StageAudit(
        IAtomicCommandContext context,
        TenantPaymentAttempt paymentAttempt,
        string reason,
        int? userId = null,
        string? actorLabel = null) =>
        context.StageSemanticEvent(new AtomicSemanticAudit(paymentAttempt.PortfolioId,
            nameof(TenantAccount), paymentAttempt.TenantAccountId, AuditLogOperation.Updated,
            UserId: userId,
            ActorLabel: actorLabel,
            NewValues: JsonSerializer.Serialize(new
            {
                paymentAttempt.Id, paymentAttempt.Provider, paymentAttempt.ProviderObjectId,
                paymentAttempt.ChargeLedgerEntryId, paymentAttempt.RefundsPaymentAttemptId,
                paymentAttempt.IdempotencyKey, paymentAttempt.AttemptType, paymentAttempt.State,
                paymentAttempt.Amount, paymentAttempt.Currency,
                InitiatedByUserId = paymentAttempt.CreatedByUserId,
            }), ChangeReason: reason));

    internal static async Task AuthorizePaymentPrepareReplayAsync(
        PrepareProviderPaymentCreateCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(db, command.PortfolioId, ct);
        var allowed = await (
            from balance in db.Set<TenantChargeBalanceProjection>().AsNoTracking()
            join entry in db.Set<TenantLedgerEntry>().AsNoTracking()
                on new { Id = balance.TenantLedgerEntryId, balance.PortfolioId, balance.TenantAccountId }
                equals new { entry.Id, entry.PortfolioId, entry.TenantAccountId }
            join account in db.Set<TenantAccount>().AsNoTracking()
                on new { Id = entry.TenantAccountId, entry.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            where entry.Id == command.ChargeLedgerEntryId
                && entry.PortfolioId == command.PortfolioId
                && entry.TenantAccountId == command.TenantAccountId
                && account.ClosedAtUtc == null
                && db.Set<ApplicationUser>().Any(user => user.Id == command.ActorUserId)
                && (command.TenantId == null || db.Set<LeaseManagementParty>().Any(party =>
                    party.LeaseManagementId == account.LeaseManagementId
                    && party.PortfolioId == command.PortfolioId
                    && party.TenantId == command.TenantId
                    && party.EffectiveFrom <= times.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= times.BusinessDate)
                    && party.Role != LeaseManagementPartyRole.Occupant
                    && party.UserAccesses.Any(access => access.ApplicationUserId == command.ActorUserId
                        && access.PortfolioId == command.PortfolioId && access.RevokedAtUtc == null)))
            select entry.Id).AnyAsync(ct);
        if (!allowed)
            throw new UnauthorizedAccessException(
                "Current provider payment scope no longer authorizes this replay.");
    }

    internal static async Task AuthorizeAutopaySetupReplayAsync(
        PrepareProviderAutopaySetupCommand command,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(db, command.PortfolioId, ct);
        var allowed = await (
            from account in db.Set<TenantAccount>().AsNoTracking()
            join party in db.Set<LeaseManagementParty>().AsNoTracking()
                on new { account.LeaseManagementId, account.PortfolioId }
                equals new { party.LeaseManagementId, party.PortfolioId }
            join access in db.Set<TenantUserAccess>().AsNoTracking()
                on new { LeaseManagementPartyId = party.Id, party.PortfolioId }
                equals new { access.LeaseManagementPartyId, access.PortfolioId }
            where account.Id == command.TenantAccountId
                && account.PortfolioId == command.PortfolioId
                && account.ClosedAtUtc == null
                && party.TenantId == command.TenantId
                && party.EffectiveFrom <= times.BusinessDate
                && (party.EffectiveThrough == null || party.EffectiveThrough >= times.BusinessDate)
                && party.Role != LeaseManagementPartyRole.Occupant
                && access.ApplicationUserId == command.ActorUserId
                && access.RevokedAtUtc == null
            select access.Id).AnyAsync(ct);
        if (!allowed)
            throw new UnauthorizedAccessException(
                "Current autopay scope no longer authorizes this replay.");
    }

    internal static async Task AuthorizeSystemAttemptReplayAsync(
        long paymentAttemptId,
        int tenantAccountId,
        int portfolioId,
        string provider,
        string idempotencyKey,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var admitted = await db.Set<TenantPaymentAttempt>().AsNoTracking()
            .AnyAsync(candidate => candidate.Id == paymentAttemptId
                && candidate.TenantAccountId == tenantAccountId
                && candidate.PortfolioId == portfolioId
                && candidate.Provider == provider
                && candidate.IdempotencyKey == idempotencyKey, ct);
        if (!admitted)
            throw new UnauthorizedAccessException(
                "The durable provider context no longer admits this replay.");
    }

    internal static async Task AuthorizeVerifiedEventReplayAsync(
        string provider,
        string providerEventId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var admitted = await db.Set<ProviderInboxEvent>().AsNoTracking()
            .AnyAsync(candidate => candidate.Provider == provider
                && candidate.ProviderEventId == providerEventId, ct);
        if (!admitted)
            throw new UnauthorizedAccessException(
                "The verified provider event no longer admits this replay.");
    }

    internal static async Task AuthorizeReconcileReplayAsync(
        long providerInboxEventId,
        RentalCommandDbContext db,
        CancellationToken ct)
    {
        var admitted = await db.Set<ProviderInboxEvent>().AsNoTracking()
            .AnyAsync(candidate => candidate.Id == providerInboxEventId
                && (candidate.ProcessedAtUtc != null
                    || candidate.DeadLetteredAtUtc != null
                    || (candidate.FailureKind == ProviderInboxFailureKind.Unmatched
                        && candidate.ClaimToken == null)), ct);
        if (!admitted)
            throw new UnauthorizedAccessException(
                "The durable provider inbox result no longer admits this replay.");
    }

    private static void StageOutbox(IAtomicCommandContext context, TenantPaymentAttempt paymentAttempt,
        long receiptId, DateTime now) => context.StageOutbox(new OutboxMessage
    {
        PortfolioId = paymentAttempt.PortfolioId,
        MessageType = "data-update",
        Payload = JsonSerializer.Serialize(new
        {
            entityType = nameof(TenantLedgerEntry), entityId = receiptId,
            data = new { paymentAttempt.TenantAccountId, mutation = "Provider payment receipt posted" },
        }),
        IdempotencyKey = OutboxIdempotency.Create("provider-receipt", paymentAttempt.Id.ToString()),
        CreatedAtUtc = now,
        NextAttemptAtUtc = now,
    });
}
