using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;

namespace RentalCommand.Data.Payments;

public sealed class PrepareProviderPaymentCreateHandler
    : IAtomicCommandHandler<PrepareProviderPaymentCreateCommand, PrepareProviderPaymentCreateResult>,
      IAtomicReplayAuthorizer<PrepareProviderPaymentCreateCommand>
{
    public async Task<PrepareProviderPaymentCreateResult> HandleAsync(
        PrepareProviderPaymentCreateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ProviderPaymentHandlerSupport.Validate(command.PortfolioId, command.TenantAccountId,
            command.ActorUserId, command.Provider, command.IdempotencyKey, command.Currency);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);

        var target = await (
            from balance in attempt.Persistence.Query<TenantChargeBalanceProjection>().AsNoTracking()
            join entry in attempt.Persistence.Query<TenantLedgerEntry>().AsNoTracking()
                on new { Id = balance.TenantLedgerEntryId, balance.PortfolioId, balance.TenantAccountId }
                equals new { entry.Id, entry.PortfolioId, entry.TenantAccountId }
            join account in attempt.Persistence.Query<TenantAccount>().AsNoTracking()
                on new { Id = entry.TenantAccountId, entry.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            where entry.Id == command.ChargeLedgerEntryId
                && entry.PortfolioId == command.PortfolioId
                && entry.TenantAccountId == command.TenantAccountId
                && entry.Direction == TenantLedgerDirection.Debit
                && balance.OpenAmount > 0m
                && account.ClosedAtUtc == null
                && attempt.Persistence.Query<ApplicationUser>().Any(user => user.Id == command.ActorUserId)
                && (command.TenantId == null || attempt.Persistence.Query<LeaseManagementParty>().Any(party =>
                    party.LeaseManagementId == account.LeaseManagementId
                    && party.PortfolioId == command.PortfolioId
                    && party.TenantId == command.TenantId
                    && party.EffectiveFrom <= times.BusinessDate
                    && (party.EffectiveThrough == null || party.EffectiveThrough >= times.BusinessDate)
                    && party.Role != LeaseManagementPartyRole.Occupant
                    && party.UserAccesses.Any(access => access.ApplicationUserId == command.ActorUserId
                        && access.PortfolioId == command.PortfolioId && access.RevokedAtUtc == null)))
                && (command.AutopayEnrollmentId == null || attempt.Persistence.Query<TenantAutopayEnrollment>().Any(enrollment =>
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
                ProviderCustomerId = attempt.Persistence.Query<TenantAutopayEnrollment>()
                    .Where(enrollment => enrollment.Id == command.AutopayEnrollmentId)
                    .Select(enrollment => enrollment.ProviderCustomerId)
                    .FirstOrDefault(),
                ProviderPaymentMethodId = attempt.Persistence.Query<TenantAutopayEnrollment>()
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
        attempt.Persistence.Add(paymentAttempt);
        await attempt.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(
            attempt, paymentAttempt, "Provider payment attempt prepared", userId: command.ActorUserId);

        return new(PrepareProviderPaymentCreateOutcome.Prepared, command.PortfolioId,
            command.TenantAccountId, command.ChargeLedgerEntryId, paymentAttempt.Id,
            paymentAttempt.Amount, paymentAttempt.Currency, paymentAttempt.Provider,
            paymentAttempt.IdempotencyKey, target.ProviderCustomerId, target.ProviderPaymentMethodId);
    }

    public Task AuthorizeReplayAsync(
        PrepareProviderPaymentCreateCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizePaymentPrepareReplayAsync(command, persistence, ct);
}

public sealed class PrepareProviderAutopaySetupHandler
    : IAtomicCommandHandler<PrepareProviderAutopaySetupCommand, PrepareProviderAutopaySetupResult>,
      IAtomicReplayAuthorizer<PrepareProviderAutopaySetupCommand>
{
    public async Task<PrepareProviderAutopaySetupResult> HandleAsync(
        PrepareProviderAutopaySetupCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        ProviderPaymentHandlerSupport.Validate(command.PortfolioId, command.TenantAccountId,
            command.ActorUserId, command.Provider, command.IdempotencyKey, command.Currency);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);

        var target = await (
            from account in attempt.Persistence.Query<TenantAccount>().AsNoTracking()
            join party in attempt.Persistence.Query<LeaseManagementParty>().AsNoTracking()
                on new { account.LeaseManagementId, account.PortfolioId }
                equals new { party.LeaseManagementId, party.PortfolioId }
            join access in attempt.Persistence.Query<TenantUserAccess>().AsNoTracking()
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
        attempt.Persistence.Add(verification);
        await attempt.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(
            attempt, verification, "Provider autopay setup prepared", userId: command.ActorUserId);
        return new(PrepareProviderAutopaySetupOutcome.Prepared, command.PortfolioId,
            command.TenantAccountId, target.AuthorizingPartyId, command.ActorUserId,
            verification.Id, command.Provider, command.IdempotencyKey);
    }

    public Task AuthorizeReplayAsync(
        PrepareProviderAutopaySetupCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeAutopaySetupReplayAsync(command, persistence, ct);
}

public sealed class FinalizeProviderPaymentCreateHandler
    : IAtomicCommandHandler<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult>,
      IAtomicReplayAuthorizer<FinalizeProviderPaymentCreateCommand>
{
    public async Task<FinalizeProviderPaymentCreateResult> HandleAsync(
        FinalizeProviderPaymentCreateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
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
                    $"Tenant payment attempt {paymentAttempt.Id} is already bound to another provider object.");
        }

        var stateResult = await ProviderPaymentHandlerSupport.ApplyStateAsync(
            paymentAttempt, command.State, command.ProviderPaymentId, null,
            command.FailureReason, null, "provider-create-finalize", attempt, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
            throw new AtomicReceiptInvariantException(stateResult.ConflictReason!);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(
                attempt, paymentAttempt, "Provider payment attempt finalized",
                actorLabel: "provider:create-finalize");

        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? FinalizeProviderPaymentCreateOutcome.Applied
                : FinalizeProviderPaymentCreateOutcome.AlreadyFinalized,
            paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, paymentAttempt.Id, paymentAttempt.Provider,
            command.ProviderPaymentId, paymentAttempt.State);
    }

    public Task AuthorizeReplayAsync(
        FinalizeProviderPaymentCreateCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeSystemAttemptReplayAsync(
            command.PaymentAttemptId, command.TenantAccountId, command.PortfolioId,
            command.Provider, command.IdempotencyKey, persistence, ct);
}

public sealed class FailProviderPaymentCreateHandler
    : IAtomicCommandHandler<FailProviderPaymentCreateCommand, FailProviderPaymentCreateResult>,
      IAtomicReplayAuthorizer<FailProviderPaymentCreateCommand>
{
    public async Task<FailProviderPaymentCreateResult> HandleAsync(
        FailProviderPaymentCreateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
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

        var databaseNow = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var stateResult = await ProviderPaymentHandlerSupport.ApplyStateAsync(
            paymentAttempt, TenantPaymentAttemptState.Failed, paymentAttempt.ProviderObjectId,
            command.FailureCode, command.FailureReason, databaseNow,
            "provider-create-failure", attempt, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
            throw new AtomicReceiptInvariantException(stateResult.ConflictReason!);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(
                attempt, paymentAttempt, "Provider payment attempt failed",
                actorLabel: "provider:create-failure");
        return new(true, paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
            paymentAttempt.Id, paymentAttempt.State);
    }

    public Task AuthorizeReplayAsync(
        FailProviderPaymentCreateCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeSystemAttemptReplayAsync(
            command.PaymentAttemptId, command.TenantAccountId, command.PortfolioId,
            command.Provider, command.IdempotencyKey, persistence, ct);
}

public sealed class RecordVerifiedProviderPaymentEventHandler
    : IAtomicCommandHandler<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult>,
      IAtomicReplayAuthorizer<RecordVerifiedProviderPaymentEventCommand>
{
    public async Task<RecordVerifiedProviderPaymentEventResult> HandleAsync(
        RecordVerifiedProviderPaymentEventCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var existing = await (
            from candidate in attempt.Persistence.Query<ProviderInboxEvent>().AsNoTracking()
            join providerAttempt in attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
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

        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        var inbox = ProviderPaymentHandlerSupport.Inbox(command, now);
        attempt.Persistence.Add(inbox);
        if (command.EventKind == ProviderPaymentEventKind.Ignored)
        {
            ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
            await attempt.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Applied, inbox.Id, null, null, null, null);
        }
        if (command.EventKind == ProviderPaymentEventKind.SetupCompleted)
            return await ApplySetupCompletedAsync(command, attempt, inbox, now, ct);
        if (string.IsNullOrWhiteSpace(command.ProviderPaymentId))
            return await ProviderPaymentHandlerSupport.DeadLetterAsync(inbox,
                "Verified event did not identify a provider payment object.", now, attempt, ct);

        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Provider == command.Provider
                && candidate.ProviderObjectId == command.ProviderPaymentId, ct);
        if (paymentAttempt is null)
        {
            inbox.FailureKind = ProviderInboxFailureKind.Unmatched;
            inbox.LastError = "No canonical tenant payment attempt matched this verified event.";
            await attempt.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Unmatched, inbox.Id, null, null, null, null);
        }
        if (!ProviderPaymentHandlerSupport.MatchesReceipt(paymentAttempt, command.Amount, command.Currency))
            return await ProviderPaymentHandlerSupport.DeadLetterAsync(inbox,
                "Provider amount or currency did not match the durable tenant payment attempt.",
                now, attempt, ct, paymentAttempt);

        inbox.PortfolioId = paymentAttempt.PortfolioId;
        var stateResult = await ProviderPaymentHandlerSupport.ApplyEventAsync(
            paymentAttempt, command.EventKind, command.FailureReason,
            $"provider-event:{command.ProviderEventId}", attempt, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
        {
            ProviderPaymentHandlerSupport.DeadLetterInbox(inbox, stateResult.ConflictReason!, now);
            await attempt.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Conflict, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
                paymentAttempt.Id, paymentAttempt.State);
        }

        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt,
                $"Verified provider event {command.ProviderEventId} reconciled",
                actorLabel: $"provider:webhook:{command.Provider}");
        await attempt.FlushBusinessAsync(ct);
        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? RecordProviderPaymentEventOutcome.Applied
                : RecordProviderPaymentEventOutcome.AlreadyInState,
            inbox.Id, paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, paymentAttempt.Id, paymentAttempt.State);
    }

    private static async Task<RecordVerifiedProviderPaymentEventResult> ApplySetupCompletedAsync(
        RecordVerifiedProviderPaymentEventCommand command, IAtomicWriteAttempt attempt,
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
                "Verified setup event did not contain complete canonical enrollment facts.", now, attempt, ct);

        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, accountId, ct);
        var target = await (
            from paymentAttempt in attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
            join account in attempt.Persistence.Query<TenantAccount>().AsNoTracking()
                on new { Id = paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            join party in attempt.Persistence.Query<LeaseManagementParty>().AsNoTracking()
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
                "Verified setup event did not match its durable account, party, and verification attempt.",
                now, attempt, ct);

        var stateResult = await ProviderPaymentHandlerSupport.ApplyStateAsync(
            target.PaymentAttempt, TenantPaymentAttemptState.Succeeded,
            target.PaymentAttempt.ProviderObjectId, null, null, null,
            $"provider-setup:{command.ProviderEventId}", attempt, ct);
        var completedAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
        {
            ProviderPaymentHandlerSupport.DeadLetterInbox(inbox, stateResult.ConflictReason!, now);
            await attempt.FlushBusinessAsync(ct);
            return new(RecordProviderPaymentEventOutcome.Conflict, inbox.Id, portfolioId,
                accountId, completedAttempt.Id, completedAttempt.State);
        }

        var current = await attempt.Persistence.Query<TenantAutopayEnrollment>()
            .SingleOrDefaultAsync(enrollment => enrollment.TenantAccountId == accountId
                && enrollment.PortfolioId == portfolioId && enrollment.CanceledAtUtc == null, ct);
        if (current is not null && (!string.Equals(current.Provider, command.Provider, StringComparison.Ordinal)
            || !string.Equals(current.ProviderCustomerId, command.ProviderCustomerId, StringComparison.Ordinal)
            || !string.Equals(current.ProviderPaymentMethodId, command.ProviderPaymentMethodId, StringComparison.Ordinal)))
        {
            current.CanceledAtUtc = now;
            current.CancelReason = "Replaced by a verified provider setup.";
            await attempt.FlushBusinessAsync(ct);
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
            attempt.Persistence.Add(current);
        }

        inbox.PortfolioId = portfolioId;
        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(attempt, completedAttempt,
                $"Verified provider setup {command.ProviderEventId} enrolled autopay",
                actorLabel: $"provider:webhook:{command.Provider}");
        await attempt.FlushBusinessAsync(ct);
        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? RecordProviderPaymentEventOutcome.Applied
                : RecordProviderPaymentEventOutcome.AlreadyInState,
            inbox.Id, portfolioId, accountId, completedAttempt.Id, completedAttempt.State);
    }

    public Task AuthorizeReplayAsync(
        RecordVerifiedProviderPaymentEventCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeVerifiedEventReplayAsync(
            command.Provider, command.ProviderEventId, persistence, ct);
}

public sealed class ReconcileClaimedProviderPaymentEventHandler
    : IAtomicCommandHandler<ReconcileClaimedProviderPaymentEventCommand, ReconcileClaimedProviderPaymentEventResult>,
      IAtomicReplayAuthorizer<ReconcileClaimedProviderPaymentEventCommand>
{
    internal const int MaximumAttempts = 8;

    public async Task<ReconcileClaimedProviderPaymentEventResult> HandleAsync(
        ReconcileClaimedProviderPaymentEventCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var inbox = await attempt.ProviderInbox.LockOwnedAsync(
            command.ProviderInboxEventId, command.ClaimOwner, command.ClaimToken, ct)
            ?? throw new AtomicReceiptInvariantException(
                $"Provider inbox event {command.ProviderInboxEventId} is not owned by the supplied fenced claim.");
        if (inbox.EventKind is ProviderPaymentEventKind.SetupCompleted or ProviderPaymentEventKind.Ignored
            || string.IsNullOrWhiteSpace(inbox.ProviderObjectId))
            throw new AtomicReceiptInvariantException($"Provider inbox event {inbox.Id} is not replayable payment work.");

        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
            .SingleOrDefaultAsync(candidate => candidate.Provider == inbox.Provider
                && candidate.ProviderObjectId == inbox.ProviderObjectId, ct);
        var now = await attempt.Persistence.ReadDatabaseClockUtcAsync(ct);
        if (paymentAttempt is null)
            return await ReleaseUnmatchedAsync(inbox, now, attempt, ct);
        if (!ProviderPaymentHandlerSupport.MatchesReceipt(paymentAttempt, inbox.Amount, inbox.Currency))
        {
            inbox.DeadLetteredAtUtc = now;
            inbox.FailureKind = ProviderInboxFailureKind.Permanent;
            inbox.LastError = "Provider amount or currency did not match the durable tenant payment attempt.";
            ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
            await attempt.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.DeadLettered, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId, paymentAttempt.Id,
                paymentAttempt.State, null);
        }

        inbox.PortfolioId = paymentAttempt.PortfolioId;
        var stateResult = await ProviderPaymentHandlerSupport.ApplyEventAsync(
            paymentAttempt, inbox.EventKind, inbox.FailureReason,
            $"provider-inbox:{inbox.Id}:{command.ClaimOwner}", attempt, ct);
        paymentAttempt = stateResult.Attempt;
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Conflict)
        {
            ProviderPaymentHandlerSupport.DeadLetterInbox(inbox, stateResult.ConflictReason!, now);
            ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
            await attempt.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.Conflict, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
                paymentAttempt.Id, paymentAttempt.State, null);
        }

        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
        if (stateResult.Disposition == ProviderPaymentApplyDisposition.Applied)
            ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt,
                $"Claimed provider event {inbox.ProviderEventId} reconciled",
                actorLabel: $"provider:worker:{command.ClaimOwner}");
        await attempt.FlushBusinessAsync(ct);
        return new(stateResult.Disposition == ProviderPaymentApplyDisposition.Applied
                ? ReconcileProviderPaymentEventOutcome.Applied
                : ReconcileProviderPaymentEventOutcome.AlreadyInState,
            inbox.Id,
            paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId, paymentAttempt.Id,
            paymentAttempt.State, null);
    }

    private static async Task<ReconcileClaimedProviderPaymentEventResult> ReleaseUnmatchedAsync(
        ProviderInboxEvent inbox, DateTime now, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        inbox.FailureKind = ProviderInboxFailureKind.Unmatched;
        inbox.LastError = "No canonical tenant payment attempt matched this verified event.";
        ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
        if (inbox.AttemptCount >= MaximumAttempts)
        {
            inbox.DeadLetteredAtUtc = now;
            await attempt.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.DeadLettered, inbox.Id,
                inbox.PortfolioId, null, null, null, null);
        }
        inbox.NextAttemptAtUtc = now.Add(inbox.AttemptCount switch
        {
            <= 1 => TimeSpan.FromMinutes(1), 2 => TimeSpan.FromMinutes(5),
            3 => TimeSpan.FromMinutes(15), 4 => TimeSpan.FromHours(1),
            5 => TimeSpan.FromHours(3), 6 => TimeSpan.FromHours(12), _ => TimeSpan.FromDays(1),
        });
        await attempt.FlushBusinessAsync(ct);
        return new(ReconcileProviderPaymentEventOutcome.RetryScheduled, inbox.Id,
            inbox.PortfolioId, null, null, null, inbox.NextAttemptAtUtc);
    }

    public Task AuthorizeReplayAsync(
        ReconcileClaimedProviderPaymentEventCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct) =>
        ProviderPaymentHandlerSupport.AuthorizeReconcileReplayAsync(
            command.ProviderInboxEventId, persistence, ct);
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

    internal static async Task<ProviderPaymentApplyResult> ApplyEventAsync(TenantPaymentAttempt paymentAttempt,
        ProviderPaymentEventKind kind, string? failureReason, string claimOwner,
        IAtomicWriteAttempt attempt, CancellationToken ct)
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
                $"Stale pending event cannot regress failed payment attempt {paymentAttempt.Id}.");

        return await ApplyStateAsync(paymentAttempt, state, paymentAttempt.ProviderObjectId, null,
            state == TenantPaymentAttemptState.Succeeded ? null : failureReason,
            state is TenantPaymentAttemptState.Failed or TenantPaymentAttemptState.Unknown
                ? paymentAttempt.NextAttemptAtUtc
                : null,
            claimOwner, attempt, ct);
    }

    internal static async Task<ProviderPaymentApplyResult> ApplyStateAsync(TenantPaymentAttempt paymentAttempt,
        TenantPaymentAttemptState state, string? providerObjectId, string? failureCode,
        string? failureReason, DateTime? nextAttemptAtUtc, string claimOwner,
        IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(paymentAttempt.ProviderObjectId)
            && !string.IsNullOrWhiteSpace(providerObjectId)
            && !string.Equals(paymentAttempt.ProviderObjectId, providerObjectId, StringComparison.Ordinal))
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Tenant payment attempt {paymentAttempt.Id} is already bound to another provider object.");
        if (paymentAttempt.State == state)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.AlreadyInState);
        if (paymentAttempt.State == TenantPaymentAttemptState.Failed
            && state == TenantPaymentAttemptState.Submitted)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Payment attempt {paymentAttempt.Id} requires an explicit retry command before it can be submitted again.");
        if (paymentAttempt.State is TenantPaymentAttemptState.Succeeded or TenantPaymentAttemptState.Canceled)
            return new(paymentAttempt, ProviderPaymentApplyDisposition.Conflict,
                $"Terminal payment attempt {paymentAttempt.Id} cannot transition from {paymentAttempt.State} to {state}.");

        if (state == TenantPaymentAttemptState.Succeeded
            && paymentAttempt.AttemptType != TenantPaymentAttemptType.Verification)
            await attempt.Locking.AcquireAsync(
                AtomicLockResource.TenantAccount, paymentAttempt.TenantAccountId, ct);

        var claimToken = await attempt.ProviderPayments.ClaimExactAsync(
            paymentAttempt.Id, paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId,
            claimOwner, ct);
        if (claimToken is null)
        {
            var current = await attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
                .SingleAsync(candidate => candidate.Id == paymentAttempt.Id
                    && candidate.TenantAccountId == paymentAttempt.TenantAccountId
                    && candidate.PortfolioId == paymentAttempt.PortfolioId, ct);
            if (current.State == state)
                return new(current, ProviderPaymentApplyDisposition.AlreadyInState);
            if (current.State is TenantPaymentAttemptState.Succeeded or TenantPaymentAttemptState.Canceled)
                return new(current, ProviderPaymentApplyDisposition.Conflict,
                    $"Terminal payment attempt {current.Id} cannot transition from {current.State} to {state}.");
            throw new AtomicReceiptInvariantException(
                $"Tenant payment attempt {paymentAttempt.Id} could not acquire its database claim.");
        }

        long? receiptId = null;
        DateTime? outboxAtUtc = null;
        if (state == TenantPaymentAttemptState.Succeeded
            && paymentAttempt.AttemptType != TenantPaymentAttemptType.Verification)
        {
            (receiptId, outboxAtUtc) = await PostReceiptAsync(paymentAttempt, attempt, ct);
        }

        var applied = await attempt.ProviderPayments.TransitionAsync(
            paymentAttempt.Id, paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId,
            claimToken.Value, state, providerObjectId, failureCode, failureReason,
            nextAttemptAtUtc, ct);
        if (!applied)
            throw new AtomicReceiptInvariantException(
                $"Tenant payment attempt {paymentAttempt.Id} lost its fenced database claim.");

        var updated = await attempt.Persistence.Query<TenantPaymentAttempt>().AsNoTracking()
            .SingleAsync(candidate => candidate.Id == paymentAttempt.Id
                && candidate.TenantAccountId == paymentAttempt.TenantAccountId
                && candidate.PortfolioId == paymentAttempt.PortfolioId, ct);
        if (receiptId is long postedReceiptId && outboxAtUtc is DateTime postedAtUtc)
            StageOutbox(attempt, updated, postedReceiptId, postedAtUtc);
        return new(updated, ProviderPaymentApplyDisposition.Applied);
    }

    private static async Task<(long? ReceiptId, DateTime? PostedAtUtc)> PostReceiptAsync(
        TenantPaymentAttempt paymentAttempt, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var alreadyPosted = await attempt.Persistence.Query<TenantLedgerEntry>().AsNoTracking()
            .Where(entry => entry.ProviderPaymentAttemptId == paymentAttempt.Id)
            .Select(entry => new { entry.Id, entry.PostedAtUtc })
            .SingleOrDefaultAsync(ct);
        if (alreadyPosted is not null)
            return (alreadyPosted.Id, alreadyPosted.PostedAtUtc);
        var times = await attempt.Persistence.ReadCommandTimesAsync(paymentAttempt.PortfolioId, ct);
        var receipt = new TenantLedgerEntry
        {
            PortfolioId = paymentAttempt.PortfolioId,
            TenantAccountId = paymentAttempt.TenantAccountId,
            EntryType = paymentAttempt.AttemptType == TenantPaymentAttemptType.Refund
                ? TenantLedgerEntryType.Refund
                : TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = paymentAttempt.Amount,
            Currency = paymentAttempt.Currency,
            EffectiveOn = times.BusinessDate,
            PostedAtUtc = times.WallClockUtc,
            Description = paymentAttempt.AttemptType == TenantPaymentAttemptType.Refund
                ? $"{paymentAttempt.Provider} refund"
                : $"{paymentAttempt.Provider} payment receipt",
            BusinessKey = $"provider-receipt:{paymentAttempt.Id}",
            ProviderPaymentAttemptId = paymentAttempt.Id,
            CreatedByUserId = paymentAttempt.CreatedByUserId,
        };
        attempt.Persistence.Add(receipt);
        await attempt.FlushBusinessAsync(ct);
        if (paymentAttempt.AttemptType == TenantPaymentAttemptType.Charge)
            await attempt.TenantMoney.AllocateOldestChargesAsync(paymentAttempt.PortfolioId,
                paymentAttempt.TenantAccountId, receipt.Id, receipt.Amount,
                $"provider-receipt:{paymentAttempt.Id}:allocation", paymentAttempt.CreatedByUserId,
                times.WallClockUtc, null, ct);
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
        ProviderInboxEvent inbox, string reason, DateTime now, IAtomicWriteAttempt attempt,
        CancellationToken ct, TenantPaymentAttempt? paymentAttempt = null)
    {
        inbox.PortfolioId = paymentAttempt?.PortfolioId ?? inbox.PortfolioId;
        inbox.FailureKind = ProviderInboxFailureKind.Permanent;
        inbox.LastError = reason;
        inbox.DeadLetteredAtUtc = now;
        await attempt.FlushBusinessAsync(ct);
        return new(RecordProviderPaymentEventOutcome.Unmatched, inbox.Id, inbox.PortfolioId,
            paymentAttempt?.TenantAccountId, paymentAttempt?.Id, paymentAttempt?.State);
    }

    internal static void StageAudit(
        IAtomicWriteAttempt attempt,
        TenantPaymentAttempt paymentAttempt,
        string reason,
        int? userId = null,
        string? actorLabel = null) =>
        attempt.StageSemanticEvent(new AtomicSemanticAudit(paymentAttempt.PortfolioId,
            nameof(TenantAccount), paymentAttempt.TenantAccountId, AuditLogOperation.Updated,
            UserId: userId,
            ActorLabel: actorLabel,
            NewValues: JsonSerializer.Serialize(new
            {
                paymentAttempt.Id, paymentAttempt.Provider, paymentAttempt.ProviderObjectId,
                paymentAttempt.IdempotencyKey, paymentAttempt.AttemptType, paymentAttempt.State,
                paymentAttempt.Amount, paymentAttempt.Currency,
                InitiatedByUserId = paymentAttempt.CreatedByUserId,
            }), ChangeReason: reason));

    internal static async Task AuthorizePaymentPrepareReplayAsync(
        PrepareProviderPaymentCreateCommand command,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var times = await persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var allowed = await (
            from balance in persistence.Query<TenantChargeBalanceProjection>().AsNoTracking()
            join entry in persistence.Query<TenantLedgerEntry>().AsNoTracking()
                on new { Id = balance.TenantLedgerEntryId, balance.PortfolioId, balance.TenantAccountId }
                equals new { entry.Id, entry.PortfolioId, entry.TenantAccountId }
            join account in persistence.Query<TenantAccount>().AsNoTracking()
                on new { Id = entry.TenantAccountId, entry.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            where entry.Id == command.ChargeLedgerEntryId
                && entry.PortfolioId == command.PortfolioId
                && entry.TenantAccountId == command.TenantAccountId
                && account.ClosedAtUtc == null
                && persistence.Query<ApplicationUser>().Any(user => user.Id == command.ActorUserId)
                && (command.TenantId == null || persistence.Query<LeaseManagementParty>().Any(party =>
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
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var times = await persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var allowed = await (
            from account in persistence.Query<TenantAccount>().AsNoTracking()
            join party in persistence.Query<LeaseManagementParty>().AsNoTracking()
                on new { account.LeaseManagementId, account.PortfolioId }
                equals new { party.LeaseManagementId, party.PortfolioId }
            join access in persistence.Query<TenantUserAccess>().AsNoTracking()
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
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var admitted = await persistence.Query<TenantPaymentAttempt>().AsNoTracking()
            .AnyAsync(candidate => candidate.Id == paymentAttemptId
                && candidate.TenantAccountId == tenantAccountId
                && candidate.PortfolioId == portfolioId
                && candidate.Provider == provider
                && candidate.IdempotencyKey == idempotencyKey, ct);
        if (!admitted)
            throw new UnauthorizedAccessException(
                "The durable provider attempt no longer admits this replay.");
    }

    internal static async Task AuthorizeVerifiedEventReplayAsync(
        string provider,
        string providerEventId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var admitted = await persistence.Query<ProviderInboxEvent>().AsNoTracking()
            .AnyAsync(candidate => candidate.Provider == provider
                && candidate.ProviderEventId == providerEventId, ct);
        if (!admitted)
            throw new UnauthorizedAccessException(
                "The verified provider event no longer admits this replay.");
    }

    internal static async Task AuthorizeReconcileReplayAsync(
        long providerInboxEventId,
        IAtomicPersistenceSession persistence,
        CancellationToken ct)
    {
        var admitted = await persistence.Query<ProviderInboxEvent>().AsNoTracking()
            .AnyAsync(candidate => candidate.Id == providerInboxEventId
                && (candidate.ProcessedAtUtc != null
                    || candidate.DeadLetteredAtUtc != null
                    || (candidate.FailureKind == ProviderInboxFailureKind.Unmatched
                        && candidate.ClaimToken == null)), ct);
        if (!admitted)
            throw new UnauthorizedAccessException(
                "The durable provider inbox result no longer admits this replay.");
    }

    private static void StageOutbox(IAtomicWriteAttempt attempt, TenantPaymentAttempt paymentAttempt,
        long receiptId, DateTime now) => attempt.StageOutbox(new OutboxMessage
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
