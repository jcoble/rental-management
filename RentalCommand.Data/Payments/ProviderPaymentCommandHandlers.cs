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
            PreparedAtUtc = command.PreparedAtUtc,
            UpdatedAtUtc = command.PreparedAtUtc,
            NextAttemptAtUtc = command.PreparedAtUtc,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(paymentAttempt);
        await attempt.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt, "Provider payment attempt prepared");

        return new(PrepareProviderPaymentCreateOutcome.Prepared, command.PortfolioId,
            command.TenantAccountId, command.ChargeLedgerEntryId, paymentAttempt.Id,
            paymentAttempt.Amount, paymentAttempt.Currency, paymentAttempt.Provider,
            paymentAttempt.IdempotencyKey, target.ProviderCustomerId, target.ProviderPaymentMethodId);
    }
}

public sealed class PrepareProviderAutopaySetupHandler
    : IAtomicCommandHandler<PrepareProviderAutopaySetupCommand, PrepareProviderAutopaySetupResult>
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
            PreparedAtUtc = command.PreparedAtUtc,
            UpdatedAtUtc = command.PreparedAtUtc,
            NextAttemptAtUtc = command.PreparedAtUtc,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(verification);
        await attempt.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(attempt, verification, "Provider autopay setup prepared");
        return new(PrepareProviderAutopaySetupOutcome.Prepared, command.PortfolioId,
            command.TenantAccountId, target.AuthorizingPartyId, command.ActorUserId,
            verification.Id, command.Provider, command.IdempotencyKey);
    }
}

public sealed class FinalizeProviderPaymentCreateHandler
    : IAtomicCommandHandler<FinalizeProviderPaymentCreateCommand, FinalizeProviderPaymentCreateResult>
{
    public async Task<FinalizeProviderPaymentCreateResult> HandleAsync(
        FinalizeProviderPaymentCreateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>()
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
            return new(FinalizeProviderPaymentCreateOutcome.AlreadyFinalized, paymentAttempt.PortfolioId,
                paymentAttempt.TenantAccountId, paymentAttempt.Id, paymentAttempt.Provider,
                paymentAttempt.ProviderObjectId, paymentAttempt.State);
        }

        paymentAttempt.ProviderObjectId = command.ProviderPaymentId;
        paymentAttempt.SubmittedAtUtc ??= command.RecordedAtUtc;
        paymentAttempt.AttemptCount = Math.Max(1, paymentAttempt.AttemptCount + 1);
        paymentAttempt.FailureReason = command.FailureReason;
        paymentAttempt.UpdatedAtUtc = command.RecordedAtUtc;
        await ProviderPaymentHandlerSupport.ApplyStateAsync(
            paymentAttempt, command.State, command.RecordedAtUtc, attempt, ct);
        ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt, "Provider payment attempt finalized");

        return new(FinalizeProviderPaymentCreateOutcome.Applied, paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, paymentAttempt.Id, paymentAttempt.Provider,
            command.ProviderPaymentId, paymentAttempt.State);
    }
}

public sealed class FailProviderPaymentCreateHandler
    : IAtomicCommandHandler<FailProviderPaymentCreateCommand, FailProviderPaymentCreateResult>
{
    public async Task<FailProviderPaymentCreateResult> HandleAsync(
        FailProviderPaymentCreateCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>()
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

        paymentAttempt.State = TenantPaymentAttemptState.Failed;
        paymentAttempt.FailureCode = command.FailureCode;
        paymentAttempt.FailureReason = command.FailureReason;
        paymentAttempt.UpdatedAtUtc = command.RecordedAtUtc;
        paymentAttempt.NextAttemptAtUtc = command.RecordedAtUtc;
        ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt, "Provider payment attempt failed");
        return new(true, paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId,
            paymentAttempt.Id, paymentAttempt.State);
    }
}

public sealed class RecordVerifiedProviderPaymentEventHandler
    : IAtomicCommandHandler<RecordVerifiedProviderPaymentEventCommand, RecordVerifiedProviderPaymentEventResult>
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

        var now = command.ReceivedAtUtc;
        var inbox = ProviderPaymentHandlerSupport.Inbox(command);
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

        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>()
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
        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        await ProviderPaymentHandlerSupport.ApplyEventAsync(paymentAttempt, command.EventKind,
            command.FailureReason, command.OccurredAtUtc ?? now, attempt, ct);
        ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt,
            $"Verified provider event {command.ProviderEventId} reconciled");
        await attempt.FlushBusinessAsync(ct);
        return new(RecordProviderPaymentEventOutcome.Applied, inbox.Id, paymentAttempt.PortfolioId,
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
            from paymentAttempt in attempt.Persistence.Query<TenantPaymentAttempt>()
            join account in attempt.Persistence.Query<TenantAccount>()
                on new { Id = paymentAttempt.TenantAccountId, paymentAttempt.PortfolioId }
                equals new { account.Id, account.PortfolioId }
            join party in attempt.Persistence.Query<LeaseManagementParty>()
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

        var current = await attempt.Persistence.Query<TenantAutopayEnrollment>()
            .SingleOrDefaultAsync(enrollment => enrollment.TenantAccountId == accountId
                && enrollment.PortfolioId == portfolioId && enrollment.CanceledAtUtc == null, ct);
        if (current is not null && (!string.Equals(current.Provider, command.Provider, StringComparison.Ordinal)
            || !string.Equals(current.ProviderCustomerId, command.ProviderCustomerId, StringComparison.Ordinal)
            || !string.Equals(current.ProviderPaymentMethodId, command.ProviderPaymentMethodId, StringComparison.Ordinal)))
        {
            current.CanceledAtUtc = command.OccurredAtUtc ?? now;
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
                EnrolledAtUtc = command.OccurredAtUtc ?? now,
                CreatedByUserId = actorUserId,
            };
            attempt.Persistence.Add(current);
        }

        target.PaymentAttempt.State = TenantPaymentAttemptState.Succeeded;
        target.PaymentAttempt.SubmittedAtUtc ??= target.PaymentAttempt.UpdatedAtUtc;
        target.PaymentAttempt.SettledAtUtc = command.OccurredAtUtc ?? now;
        target.PaymentAttempt.UpdatedAtUtc = command.OccurredAtUtc ?? now;
        inbox.PortfolioId = portfolioId;
        ProviderPaymentHandlerSupport.CompleteInbox(inbox, now);
        await attempt.FlushBusinessAsync(ct);
        ProviderPaymentHandlerSupport.StageAudit(attempt, target.PaymentAttempt,
            $"Verified provider setup {command.ProviderEventId} enrolled autopay");
        return new(RecordProviderPaymentEventOutcome.Applied, inbox.Id, portfolioId,
            accountId, target.PaymentAttempt.Id, target.PaymentAttempt.State);
    }
}

public sealed class ReconcileClaimedProviderPaymentEventHandler
    : IAtomicCommandHandler<ReconcileClaimedProviderPaymentEventCommand, ReconcileClaimedProviderPaymentEventResult>
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

        var paymentAttempt = await attempt.Persistence.Query<TenantPaymentAttempt>()
            .SingleOrDefaultAsync(candidate => candidate.Provider == inbox.Provider
                && candidate.ProviderObjectId == inbox.ProviderObjectId, ct);
        if (paymentAttempt is null)
            return await ReleaseUnmatchedAsync(inbox, command.ReconciledAtUtc, attempt, ct);
        if (!ProviderPaymentHandlerSupport.MatchesReceipt(paymentAttempt, inbox.Amount, inbox.Currency))
        {
            inbox.DeadLetteredAtUtc = command.ReconciledAtUtc;
            inbox.FailureKind = ProviderInboxFailureKind.Permanent;
            inbox.LastError = "Provider amount or currency did not match the durable tenant payment attempt.";
            ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
            await attempt.FlushBusinessAsync(ct);
            return new(ReconcileProviderPaymentEventOutcome.DeadLettered, inbox.Id,
                paymentAttempt.PortfolioId, paymentAttempt.TenantAccountId, paymentAttempt.Id,
                paymentAttempt.State, null);
        }

        inbox.PortfolioId = paymentAttempt.PortfolioId;
        ProviderPaymentHandlerSupport.CompleteInbox(inbox, command.ReconciledAtUtc);
        ProviderPaymentHandlerSupport.ReleaseClaim(inbox);
        await ProviderPaymentHandlerSupport.ApplyEventAsync(paymentAttempt, inbox.EventKind,
            inbox.FailureReason, inbox.OccurredAtUtc ?? command.ReconciledAtUtc, attempt, ct);
        ProviderPaymentHandlerSupport.StageAudit(attempt, paymentAttempt,
            $"Claimed provider event {inbox.ProviderEventId} reconciled");
        await attempt.FlushBusinessAsync(ct);
        return new(ReconcileProviderPaymentEventOutcome.Applied, inbox.Id,
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
}

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

    internal static ProviderInboxEvent Inbox(RecordVerifiedProviderPaymentEventCommand command) => new()
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
        ReceivedAtUtc = command.ReceivedAtUtc,
        NextAttemptAtUtc = command.ReceivedAtUtc,
    };

    internal static bool MatchesReceipt(TenantPaymentAttempt paymentAttempt, decimal? amount, string? currency) =>
        paymentAttempt.AttemptType == TenantPaymentAttemptType.Verification
        || ((amount == null || amount == paymentAttempt.Amount)
            && (string.IsNullOrWhiteSpace(currency)
                || string.Equals(currency, paymentAttempt.Currency, StringComparison.OrdinalIgnoreCase)));

    internal static async Task ApplyEventAsync(TenantPaymentAttempt paymentAttempt,
        ProviderPaymentEventKind kind, string? failureReason, DateTime occurredAtUtc,
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
        paymentAttempt.FailureReason = state == TenantPaymentAttemptState.Succeeded ? null : failureReason;
        await ApplyStateAsync(paymentAttempt, state, occurredAtUtc, attempt, ct);
    }

    internal static async Task ApplyStateAsync(TenantPaymentAttempt paymentAttempt,
        TenantPaymentAttemptState state, DateTime occurredAtUtc,
        IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        if (paymentAttempt.State == TenantPaymentAttemptState.Succeeded && state != TenantPaymentAttemptState.Succeeded)
            return;
        paymentAttempt.State = state;
        paymentAttempt.UpdatedAtUtc = occurredAtUtc;
        if (state is TenantPaymentAttemptState.Submitted or TenantPaymentAttemptState.Succeeded)
            paymentAttempt.SubmittedAtUtc ??= occurredAtUtc;
        if (state != TenantPaymentAttemptState.Succeeded || paymentAttempt.AttemptType == TenantPaymentAttemptType.Verification)
        {
            paymentAttempt.SettledAtUtc = state == TenantPaymentAttemptState.Succeeded ? occurredAtUtc : null;
            return;
        }

        var alreadyPosted = await attempt.Persistence.Query<TenantLedgerEntry>().AsNoTracking()
            .AnyAsync(entry => entry.ProviderPaymentAttemptId == paymentAttempt.Id, ct);
        if (alreadyPosted)
        {
            paymentAttempt.SettledAtUtc = occurredAtUtc;
            return;
        }
        var times = await attempt.Persistence.ReadCommandTimesAsync(paymentAttempt.PortfolioId, ct);
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
        attempt.Persistence.Add(receipt);
        paymentAttempt.SettledAtUtc = occurredAtUtc;
        await attempt.FlushBusinessAsync(ct);
        await attempt.TenantMoney.AllocateOldestChargesAsync(paymentAttempt.PortfolioId,
            paymentAttempt.TenantAccountId, receipt.Id, receipt.Amount,
            $"provider-receipt:{paymentAttempt.Id}:allocation", paymentAttempt.CreatedByUserId,
            times.WallClockUtc, null, ct);
        StageOutbox(attempt, paymentAttempt, receipt.Id, times.WallClockUtc);
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

    internal static void StageAudit(IAtomicWriteAttempt attempt, TenantPaymentAttempt paymentAttempt, string reason) =>
        attempt.StageSemanticEvent(new AtomicSemanticAudit(paymentAttempt.PortfolioId,
            nameof(TenantAccount), paymentAttempt.TenantAccountId, AuditLogOperation.Updated,
            UserId: paymentAttempt.CreatedByUserId,
            NewValues: JsonSerializer.Serialize(new
            {
                paymentAttempt.Id, paymentAttempt.Provider, paymentAttempt.ProviderObjectId,
                paymentAttempt.IdempotencyKey, paymentAttempt.AttemptType, paymentAttempt.State,
                paymentAttempt.Amount, paymentAttempt.Currency,
            }), ChangeReason: reason));

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
