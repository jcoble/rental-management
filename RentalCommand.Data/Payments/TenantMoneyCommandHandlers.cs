using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Authorization;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Outbox;
using RentalCommand.Core.Payments;

namespace RentalCommand.Data.Payments;

public sealed class RecordTenantReceiptHandler
    : IAtomicCommandHandler<RecordTenantReceiptCommand, RecordTenantReceiptResult>,
      IAtomicReplayAuthorizer<RecordTenantReceiptCommand>
{
    public async Task<RecordTenantReceiptResult> HandleAsync(
        RecordTenantReceiptCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport.AuthorizedAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Receipt source provenance must belong to the current portfolio.");

        var recordedAtUtc = TenantMoneyCommandSupport.CommandTimestamp(command.RecordedAtUtc, times.WallClockUtc);
        var paymentAttempt = TenantMoneyCommandSupport.ManualAttempt(
            command, account.Currency, command.Amount, command.PaymentMethodSummary,
            command.ExternalReference, command.PayerName, command.CheckNumber, command.BankName, recordedAtUtc);
        attempt.Persistence.Add(paymentAttempt);
        await attempt.FlushBusinessAsync(ct);

        var receipt = new TenantLedgerEntry
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            EntryType = TenantLedgerEntryType.PaymentReceipt,
            Direction = TenantLedgerDirection.Credit,
            Amount = command.Amount,
            Currency = account.Currency,
            EffectiveOn = command.EffectiveOn,
            PostedAtUtc = recordedAtUtc,
            Description = command.Description.Trim(),
            BusinessKey = command.BusinessKey,
            ProviderPaymentAttemptId = paymentAttempt.Id,
            SourceStoredFileId = command.SourceStoredFileId,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(receipt);
        await attempt.FlushBusinessAsync(ct);

        var allocations = command.AllocateOldestCharges
            ? await TenantMoneyCommandSupport.AllocateOldestAsync(
                command.PortfolioId, command.TenantAccountId, receipt.Id, command.Amount,
                command.BusinessKey, command.ActorUserId, recordedAtUtc, attempt, ct)
            : new AtomicLedgerAllocationSummary();

        TenantMoneyCommandSupport.StageMutation(
            attempt, command, recordedAtUtc, nameof(TenantLedgerEntry), receipt.Id,
            "Tenant payment receipt recorded", new { receipt.Amount, receipt.EffectiveOn, allocations = allocations.AllocationCount });
        return new RecordTenantReceiptResult(true, account.Id, receipt.Id, paymentAttempt.Id,
            receipt.Amount, allocations.AllocatedAmount, allocations.AllocationCount);
    }

    public Task AuthorizeReplayAsync(RecordTenantReceiptCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class PostTenantChargeHandler
    : IAtomicCommandHandler<PostTenantChargeCommand, TenantChargeMutationResult>,
      IAtomicReplayAuthorizer<PostTenantChargeCommand>
{
    public async Task<TenantChargeMutationResult> HandleAsync(
        PostTenantChargeCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport
            .AuthorizedAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Charge source provenance must belong to the current portfolio.");

        var charge = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit, command.Amount,
            account.Currency, command.EffectiveOn, command.DueOn, command.Description,
            command.BusinessKey, times.WallClockUtc, sourceStoredFileId: command.SourceStoredFileId);
        attempt.Persistence.Add(charge);
        await attempt.FlushBusinessAsync(ct);

        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), charge.Id,
            "Tenant charge posted", new
            {
                entryType = nameof(TenantLedgerEntryType.ManualCharge),
                charge.Amount,
                charge.EffectiveOn,
                charge.DueOn,
                charge.SourceStoredFileId,
            });
        return new TenantChargeMutationResult(true, true, account.Id, charge.Id, null,
            charge.Amount, null);
    }

    public Task AuthorizeReplayAsync(PostTenantChargeCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ReverseTenantChargeHandler
    : IAtomicCommandHandler<ReverseTenantChargeCommand, TenantChargeMutationResult>,
      IAtomicReplayAuthorizer<ReverseTenantChargeCommand>
{
    public async Task<TenantChargeMutationResult> HandleAsync(
        ReverseTenantChargeCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await (
            from account in TenantMoneyCommandSupport.AuthorizedAccounts(
                command, attempt.Persistence, times.WallClockUtc)
            join entry in attempt.Persistence.Query<TenantLedgerEntry>()
                on new { TenantAccountId = account.Id, account.PortfolioId }
                equals new { entry.TenantAccountId, entry.PortfolioId }
            where entry.Id == command.ReversesEntryId
                && entry.Direction == TenantLedgerDirection.Debit
                && (entry.EntryType == TenantLedgerEntryType.RentCharge
                    || entry.EntryType == TenantLedgerEntryType.AddendumCharge
                    || entry.EntryType == TenantLedgerEntryType.LateFeeCharge
                    || entry.EntryType == TenantLedgerEntryType.DepositCharge
                    || entry.EntryType == TenantLedgerEntryType.ManualCharge)
            select new
            {
                account.Id,
                EntryId = entry.Id,
                entry.Amount,
                entry.Currency,
                ReversedAmount = attempt.Persistence.Query<TenantLedgerEntry>()
                    .Where(reversal => reversal.PortfolioId == command.PortfolioId
                        && reversal.TenantAccountId == command.TenantAccountId
                        && reversal.EntryType == TenantLedgerEntryType.Reversal
                        && reversal.ReversesEntryId == entry.Id)
                    .Sum(reversal => (decimal?)reversal.Amount) ?? 0m,
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException("Reversal source provenance must belong to the current portfolio.");
        if (target.ReversedAmount > 0m)
        {
            return new TenantChargeMutationResult(true, false, target.Id, 0, target.EntryId,
                target.Amount, "The charge has already been reversed.");
        }

        var reversal = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Reversal, TenantLedgerDirection.Credit,
            target.Amount, target.Currency, command.EffectiveOn, null, command.Reason,
            command.BusinessKey, times.WallClockUtc, sourceStoredFileId: command.SourceStoredFileId);
        reversal.ReversesEntryId = target.EntryId;
        attempt.Persistence.Add(reversal);
        await attempt.FlushBusinessAsync(ct);

        var reversedAllocations = await attempt.TenantMoney.ReverseEntryAllocationsAsync(
            command.PortfolioId, command.TenantAccountId, target.EntryId,
            $"{command.BusinessKey}:allocation", command.ActorUserId, times.WallClockUtc, ct);
        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), reversal.Id,
            "Tenant charge reversed", new
            {
                reversal.Amount,
                reversal.EffectiveOn,
                reversal.ReversesEntryId,
                reason = command.Reason.Trim(),
                reversedAllocations.AllocationCount,
                reversedAllocations.AllocatedAmount,
            });
        return new TenantChargeMutationResult(true, true, target.Id, reversal.Id,
            target.EntryId, reversal.Amount, null);
    }

    public Task AuthorizeReplayAsync(ReverseTenantChargeCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class PostTenantCreditHandler
    : IAtomicCommandHandler<PostTenantCreditCommand, TenantLedgerMutationResult>,
      IAtomicReplayAuthorizer<PostTenantCreditCommand>
{
    public async Task<TenantLedgerMutationResult> HandleAsync(
        PostTenantCreditCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport
            .AuthorizedAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Credit source provenance must belong to the current portfolio.");

        var credit = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Credit, TenantLedgerDirection.Credit,
            command.Amount, account.Currency, command.EffectiveOn, null,
            command.Description, command.BusinessKey, times.WallClockUtc,
            sourceStoredFileId: command.SourceStoredFileId);
        attempt.Persistence.Add(credit);
        await attempt.FlushBusinessAsync(ct);

        var allocations = command.AllocateOldestCharges
            ? await TenantMoneyCommandSupport.AllocateOldestAsync(
                command.PortfolioId, command.TenantAccountId, credit.Id, command.Amount,
                command.BusinessKey, command.ActorUserId, times.WallClockUtc, attempt, ct)
            : new AtomicLedgerAllocationSummary();
        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), credit.Id,
            "Tenant credit posted", new
            {
                credit.Amount,
                credit.EffectiveOn,
                credit.SourceStoredFileId,
                allocations.AllocationCount,
                allocations.AllocatedAmount,
            });
        return new TenantLedgerMutationResult(true, true, account.Id, credit.Id, null,
            credit.EntryType, credit.Direction, credit.Amount, allocations.AllocatedAmount,
            allocations.AllocationCount, null);
    }

    public Task AuthorizeReplayAsync(PostTenantCreditCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class PostTenantAdjustmentHandler
    : IAtomicCommandHandler<PostTenantAdjustmentCommand, TenantLedgerMutationResult>,
      IAtomicReplayAuthorizer<PostTenantAdjustmentCommand>
{
    public async Task<TenantLedgerMutationResult> HandleAsync(
        PostTenantAdjustmentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var account = await TenantMoneyCommandSupport
            .AuthorizedAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            })
            .SingleOrDefaultAsync(ct);
        if (account is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!account.SourceAllowed)
            throw new ArgumentException("Adjustment source provenance must belong to the current portfolio.");

        var adjustment = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Adjustment, command.Direction,
            command.Amount, account.Currency, command.EffectiveOn, null,
            command.Description, command.BusinessKey, times.WallClockUtc,
            sourceStoredFileId: command.SourceStoredFileId);
        attempt.Persistence.Add(adjustment);
        await attempt.FlushBusinessAsync(ct);

        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), adjustment.Id,
            "Tenant adjustment posted", new
            {
                adjustment.Amount,
                adjustment.Direction,
                adjustment.EffectiveOn,
                adjustment.SourceStoredFileId,
            });
        return new TenantLedgerMutationResult(true, true, account.Id, adjustment.Id, null,
            adjustment.EntryType, adjustment.Direction, adjustment.Amount, 0m, 0, null);
    }

    public Task AuthorizeReplayAsync(PostTenantAdjustmentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class ReverseTenantLedgerEntryHandler
    : IAtomicCommandHandler<ReverseTenantLedgerEntryCommand, TenantLedgerMutationResult>,
      IAtomicReplayAuthorizer<ReverseTenantLedgerEntryCommand>
{
    public async Task<TenantLedgerMutationResult> HandleAsync(
        ReverseTenantLedgerEntryCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await (
            from account in TenantMoneyCommandSupport.AuthorizedAccounts(
                command, attempt.Persistence, times.WallClockUtc)
            join entry in attempt.Persistence.Query<TenantLedgerEntry>()
                on new { TenantAccountId = account.Id, account.PortfolioId }
                equals new { entry.TenantAccountId, entry.PortfolioId }
            where entry.Id == command.ReversesEntryId
                && entry.EntryType != TenantLedgerEntryType.Reversal
                && entry.EntryType != TenantLedgerEntryType.TransferIn
                && entry.EntryType != TenantLedgerEntryType.TransferOut
                && entry.EntryType != TenantLedgerEntryType.PaymentReceipt
                && entry.EntryType != TenantLedgerEntryType.Refund
            select new
            {
                account.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Direction,
                entry.Amount,
                entry.Currency,
                IsSecurityDepositLinked = attempt.Persistence.Query<SecurityDepositEntry>()
                    .Any(depositEntry =>
                        depositEntry.PortfolioId == command.PortfolioId
                        && depositEntry.TenantLedgerEntryId == entry.Id),
                HasSecurityDepositLinkedAllocationCounterpart =
                    attempt.Persistence.Query<TenantLedgerAllocation>().Any(allocation =>
                        allocation.PortfolioId == command.PortfolioId
                        && allocation.TenantAccountId == command.TenantAccountId
                        && ((allocation.DebitEntryId == entry.Id
                                && attempt.Persistence.Query<SecurityDepositEntry>()
                                    .Any(depositEntry =>
                                        depositEntry.PortfolioId == command.PortfolioId
                                        && depositEntry.TenantLedgerEntryId
                                            == allocation.CreditEntryId))
                            || (allocation.CreditEntryId == entry.Id
                                && attempt.Persistence.Query<SecurityDepositEntry>()
                                    .Any(depositEntry =>
                                        depositEntry.PortfolioId == command.PortfolioId
                                        && depositEntry.TenantLedgerEntryId
                                            == allocation.DebitEntryId)))),
                HasReversal = attempt.Persistence.Query<TenantLedgerEntry>().Any(reversal =>
                    reversal.PortfolioId == command.PortfolioId
                    && reversal.TenantAccountId == command.TenantAccountId
                    && reversal.EntryType == TenantLedgerEntryType.Reversal
                    && reversal.ReversesEntryId == entry.Id),
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException("Reversal source provenance must belong to the current portfolio.");
        if (target.IsSecurityDepositLinked
            || target.HasSecurityDepositLinkedAllocationCounterpart)
        {
            return new TenantLedgerMutationResult(true, false, target.Id, 0, target.EntryId,
                TenantLedgerEntryType.Reversal,
                target.Direction == TenantLedgerDirection.Debit
                    ? TenantLedgerDirection.Credit
                    : TenantLedgerDirection.Debit,
                target.Amount, 0m, 0,
                "Security-deposit ledger entries and their allocated counterparts must be " +
                "corrected through the dedicated security-deposit workflow.");
        }
        if (target.HasReversal)
        {
            return new TenantLedgerMutationResult(true, false, target.Id, 0, target.EntryId,
                TenantLedgerEntryType.Reversal,
                target.Direction == TenantLedgerDirection.Debit
                    ? TenantLedgerDirection.Credit
                    : TenantLedgerDirection.Debit,
                target.Amount, 0m, 0, "The ledger entry has already been reversed.");
        }

        var reversalDirection = target.Direction == TenantLedgerDirection.Debit
            ? TenantLedgerDirection.Credit
            : TenantLedgerDirection.Debit;
        var reversal = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Reversal, reversalDirection,
            target.Amount, target.Currency, command.EffectiveOn, null,
            command.Reason, command.BusinessKey, times.WallClockUtc,
            sourceStoredFileId: command.SourceStoredFileId);
        reversal.ReversesEntryId = target.EntryId;
        attempt.Persistence.Add(reversal);
        await attempt.FlushBusinessAsync(ct);

        var reversedAllocations = await attempt.TenantMoney.ReverseEntryAllocationsAsync(
            command.PortfolioId, command.TenantAccountId, target.EntryId,
            $"{command.BusinessKey}:allocation", command.ActorUserId,
            times.WallClockUtc, ct);
        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), reversal.Id,
            "Tenant ledger entry reversed", new
            {
                originalEntryType = target.EntryType,
                reversal.Amount,
                reversal.Direction,
                reversal.EffectiveOn,
                reversal.ReversesEntryId,
                reason = command.Reason.Trim(),
                reversedAllocations.AllocationCount,
                reversedAllocations.AllocatedAmount,
            });
        return new TenantLedgerMutationResult(true, true, target.Id, reversal.Id,
            target.EntryId, reversal.EntryType, reversal.Direction, reversal.Amount,
            reversedAllocations.AllocatedAmount, reversedAllocations.AllocationCount, null);
    }

    public Task AuthorizeReplayAsync(ReverseTenantLedgerEntryCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class RefundTenantPaymentHandler
    : IAtomicCommandHandler<RefundTenantPaymentCommand, TenantPaymentRefundResult>,
      IAtomicReplayAuthorizer<RefundTenantPaymentCommand>
{
    public async Task<TenantPaymentRefundResult> HandleAsync(
        RefundTenantPaymentCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await (
            from account in TenantMoneyCommandSupport.AuthorizedAccounts(
                command, attempt.Persistence, times.WallClockUtc)
            join entry in attempt.Persistence.Query<TenantLedgerEntry>()
                on new { TenantAccountId = account.Id, account.PortfolioId }
                equals new { entry.TenantAccountId, entry.PortfolioId }
            where entry.Id == command.PaymentEntryId
                && entry.EntryType == TenantLedgerEntryType.PaymentReceipt
            select new
            {
                account.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Amount,
                entry.Currency,
                entry.ProviderPaymentAttemptId,
                OriginalAttemptType = entry.ProviderPaymentAttemptId == null
                    ? (TenantPaymentAttemptType?)null
                    : attempt.Persistence.Query<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => (TenantPaymentAttemptType?)payment.AttemptType)
                        .SingleOrDefault(),
                OriginalAttemptState = entry.ProviderPaymentAttemptId == null
                    ? (TenantPaymentAttemptState?)null
                    : attempt.Persistence.Query<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => (TenantPaymentAttemptState?)payment.State)
                        .SingleOrDefault(),
                OriginalAttemptAmount = entry.ProviderPaymentAttemptId == null
                    ? (decimal?)null
                    : attempt.Persistence.Query<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => (decimal?)payment.Amount)
                        .SingleOrDefault(),
                OriginalAttemptCurrency = entry.ProviderPaymentAttemptId == null
                    ? null
                    : attempt.Persistence.Query<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => payment.Currency)
                        .SingleOrDefault(),
                Provider = entry.ProviderPaymentAttemptId == null
                    ? null
                    : attempt.Persistence.Query<TenantPaymentAttempt>()
                        .Where(payment => payment.Id == entry.ProviderPaymentAttemptId.Value
                            && payment.PortfolioId == command.PortfolioId
                            && payment.TenantAccountId == command.TenantAccountId)
                        .Select(payment => payment.Provider)
                        .SingleOrDefault(),
                HasReversal = attempt.Persistence.Query<TenantLedgerEntry>().Any(reversal =>
                    reversal.PortfolioId == command.PortfolioId
                    && reversal.TenantAccountId == command.TenantAccountId
                    && reversal.EntryType == TenantLedgerEntryType.Reversal
                    && reversal.ReversesEntryId == entry.Id),
                ExistingRefundAttemptId = entry.ProviderPaymentAttemptId == null
                    ? (long?)null
                    : attempt.Persistence.Query<TenantPaymentAttempt>()
                        .Where(refund => refund.PortfolioId == command.PortfolioId
                            && refund.TenantAccountId == command.TenantAccountId
                            && refund.AttemptType == TenantPaymentAttemptType.Refund
                            && refund.RefundsPaymentAttemptId == entry.ProviderPaymentAttemptId.Value)
                        .Select(refund => (long?)refund.Id)
                        .SingleOrDefault(),
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException(
                "Payment refund source provenance must belong to the current portfolio.");
        TenantPaymentRefundResult Result(
            bool applied, TenantPaymentRefundOutcome outcome, long? refundAttemptId,
            string? error) =>
            new(true, applied, outcome, target.Id, target.EntryId, null,
                refundAttemptId, target.Amount, 0m, 0, error);
        if (target.HasReversal)
            return Result(false, TenantPaymentRefundOutcome.ExternalCorrectionUnavailable, null,
                "A reversed payment receipt cannot also be refunded.");
        var originalAttemptValid = target.ProviderPaymentAttemptId is not null
            && target.OriginalAttemptType == TenantPaymentAttemptType.Charge
            && target.OriginalAttemptState == TenantPaymentAttemptState.Succeeded
            && target.OriginalAttemptAmount == target.Amount
            && string.Equals(target.OriginalAttemptCurrency, target.Currency,
                StringComparison.Ordinal);
        if (!originalAttemptValid)
            return Result(false, TenantPaymentRefundOutcome.ExternalCorrectionUnavailable, null,
                "The payment receipt has no matching settled Charge attempt to refund.");
        if (target.ExistingRefundAttemptId is long existingRefundAttemptId)
            return Result(false, TenantPaymentRefundOutcome.AlreadyRefunded,
                existingRefundAttemptId,
                "The payment receipt has already been refunded.");

        var providerBacked = !string.Equals(
            target.Provider, "manual", StringComparison.OrdinalIgnoreCase);
        if (providerBacked)
            return Result(false, TenantPaymentRefundOutcome.ExternalCorrectionUnavailable, null,
                "Provider-backed payment refunds are unavailable until a supported provider refund workflow is configured.");
        if (string.IsNullOrWhiteSpace(command.PaymentMethodSummary)
                || string.IsNullOrWhiteSpace(command.ExternalReference))
            throw new ArgumentException(
                "Manual refunds require payment method and external payout provenance.");
        var refundAttempt = new TenantPaymentAttempt
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            Provider = target.Provider!,
            ProviderObjectId = command.ExternalReference!.Trim(),
            RefundsPaymentAttemptId = target.ProviderPaymentAttemptId,
            IdempotencyKey = command.DeliveryIdempotencyKey,
            AttemptType = TenantPaymentAttemptType.Refund,
            State = TenantPaymentAttemptState.Succeeded,
            Amount = target.Amount,
            Currency = target.Currency,
            PaymentMethodSummary = command.PaymentMethodSummary!.Trim(),
            PreparedAtUtc = times.WallClockUtc,
            SubmittedAtUtc = times.WallClockUtc,
            SettledAtUtc = times.WallClockUtc,
            UpdatedAtUtc = times.WallClockUtc,
            NextAttemptAtUtc = null,
            AttemptCount = 1,
            CreatedByUserId = command.ActorUserId,
        };
        attempt.Persistence.Add(refundAttempt);
        await attempt.FlushBusinessAsync(ct);

        var refund = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Refund, TenantLedgerDirection.Debit,
            target.Amount, target.Currency, command.EffectiveOn, null, command.Reason,
            command.BusinessKey, times.WallClockUtc,
            providerAttemptId: refundAttempt.Id,
            sourceStoredFileId: command.SourceStoredFileId);
        attempt.Persistence.Add(refund);
        await attempt.FlushBusinessAsync(ct);
        var compensation = await attempt.TenantMoney.ReverseEntryAllocationsAsync(
            command.PortfolioId, command.TenantAccountId, target.EntryId,
            $"{command.BusinessKey}:allocation", command.ActorUserId,
            times.WallClockUtc, ct);
        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(TenantLedgerEntry), refund.Id,
            "Tenant payment refunded", new
            {
                PaymentReceiptEntryId = target.EntryId,
                RefundPaymentAttemptId = refundAttempt.Id,
                refund.Amount,
                refund.EffectiveOn,
                command.PaymentMethodSummary,
                command.ExternalReference,
                reason = command.Reason.Trim(),
                compensation.AllocationCount,
                compensation.AllocatedAmount,
            });
        return new TenantPaymentRefundResult(true, true,
            TenantPaymentRefundOutcome.Refunded, target.Id, target.EntryId, refund.Id,
            refundAttempt.Id, target.Amount, compensation.AllocatedAmount,
            compensation.AllocationCount, null);
    }

    public Task AuthorizeReplayAsync(RefundTenantPaymentCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeReplayAsync(command, persistence, ct);
}

public sealed class FundSecurityDepositHandler
    : IAtomicCommandHandler<FundSecurityDepositCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<FundSecurityDepositCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        FundSecurityDepositCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(row => new
            {
                row.Id,
                row.Currency,
                OpenDepositCharges = attempt.Persistence.Query<TenantChargeBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.TenantAccountId == command.TenantAccountId
                        && balance.EntryType == nameof(TenantLedgerEntryType.DepositCharge)
                        && balance.OpenAmount > 0m)
                    .Sum(balance => (decimal?)balance.OpenAmount) ?? 0m,
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (target.OpenDepositCharges < command.Amount)
        {
            return TenantMoneyCommandSupport.DepositConflict(
                command,
                "The deposit funding amount exceeds the tenant account's open security-deposit charges.");
        }

        var paymentAttempt = TenantMoneyCommandSupport.ManualAttempt(
            command, target.Currency, command.Amount, command.PaymentMethodSummary,
            command.ExternalReference, null, null, null, times.WallClockUtc);
        attempt.Persistence.Add(paymentAttempt);
        await attempt.FlushBusinessAsync(ct);

        var receipt = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.PaymentReceipt, TenantLedgerDirection.Credit,
            command.Amount, target.Currency, command.EffectiveOn, null,
            command.Description, $"{command.BusinessKey}:tenant-receipt", times.WallClockUtc,
            providerAttemptId: paymentAttempt.Id, sourceStoredFileId: command.SourceStoredFileId);
        attempt.Persistence.Add(receipt);
        await attempt.FlushBusinessAsync(ct);

        var allocation = await TenantMoneyCommandSupport.AllocateOldestAsync(
            command.PortfolioId, command.TenantAccountId, receipt.Id, command.Amount,
            $"{command.BusinessKey}:allocation", command.ActorUserId, times.WallClockUtc,
            attempt, ct, TenantLedgerEntryType.DepositCharge);
        if (allocation.AllocatedAmount != command.Amount)
        {
            throw new InvalidOperationException(
                "Security-deposit funding must allocate its full amount to open deposit charges.");
        }

        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Receipt, SecurityDepositDirection.Increase,
            command.Amount, target.Currency, command.EffectiveOn, command.Description,
            command.BusinessKey, times.WallClockUtc, receipt.Id, command.SourceStoredFileId);
        attempt.Persistence.Add(deposit);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(attempt, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit funded",
            new
            {
                deposit.Amount,
                deposit.EffectiveOn,
                TenantLedgerEntryId = receipt.Id,
                allocation.AllocationCount,
                allocation.AllocatedAmount,
            });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, receipt.Id, deposit.Amount, null);
    }

    public Task AuthorizeReplayAsync(FundSecurityDepositCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

public sealed class DeductSecurityDepositHandler
    : IAtomicCommandHandler<DeductSecurityDepositCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<DeductSecurityDepositCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        DeductSecurityDepositCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(account => new
            {
                account.Id,
                account.Currency,
                Balance = attempt.Persistence.Query<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.SecurityDepositAccountId == account.Id)
                    .Select(balance => (decimal?)balance.HeldBalance)
                    .SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if ((target.Balance ?? 0m) < command.Amount)
            return TenantMoneyCommandSupport.DepositConflict(command, "The deduction exceeds the held deposit balance.");

        var description = string.IsNullOrWhiteSpace(command.Notes)
            ? command.Reason.Trim()
            : $"{command.Reason.Trim()}: {command.Notes.Trim()}";
        var charge = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.ManualCharge, TenantLedgerDirection.Debit,
            command.Amount, target.Currency, command.EffectiveOn, command.EffectiveOn,
            description, $"{command.BusinessKey}:charge", times.WallClockUtc);
        var credit = TenantMoneyCommandSupport.Ledger(
            command, TenantLedgerEntryType.Credit, TenantLedgerDirection.Credit,
            command.Amount, target.Currency, command.EffectiveOn, null,
            $"Security deposit applied: {description}", $"{command.BusinessKey}:credit", times.WallClockUtc);
        attempt.Persistence.Add(charge);
        attempt.Persistence.Add(credit);
        await attempt.FlushBusinessAsync(ct);

        attempt.Persistence.Add(new TenantLedgerAllocation
        {
            PortfolioId = command.PortfolioId,
            TenantAccountId = command.TenantAccountId,
            DebitEntryId = charge.Id,
            CreditEntryId = credit.Id,
            Amount = command.Amount,
            AllocatedAtUtc = times.WallClockUtc,
            BusinessKey = $"{command.BusinessKey}:allocation",
            CreatedByUserId = command.ActorUserId,
        });
        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Deduction, SecurityDepositDirection.Decrease,
            command.Amount, target.Currency, command.EffectiveOn, description,
            command.BusinessKey, times.WallClockUtc, credit.Id, command.SourceStoredFileId);
        attempt.Persistence.Add(deposit);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(attempt, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit deduction posted",
            new { deposit.Amount, command.Reason, ChargeEntryId = charge.Id, CreditEntryId = credit.Id });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, credit.Id, deposit.Amount, null);
    }

    public Task AuthorizeReplayAsync(DeductSecurityDepositCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

public sealed class RefundSecurityDepositHandler
    : IAtomicCommandHandler<RefundSecurityDepositCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<RefundSecurityDepositCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        RefundSecurityDepositCommand command, IAtomicWriteAttempt attempt, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await TenantMoneyCommandSupport.AuthorizedDepositAccounts(command, attempt.Persistence, times.WallClockUtc)
            .Select(account => new
            {
                account.Id,
                account.Currency,
                Balance = attempt.Persistence.Query<SecurityDepositBalanceProjection>()
                    .Where(balance => balance.PortfolioId == command.PortfolioId
                        && balance.SecurityDepositAccountId == account.Id)
                    .Select(balance => (decimal?)balance.HeldBalance)
                    .SingleOrDefault(),
            })
            .SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        var balance = target.Balance ?? 0m;
        var amount = command.Amount ?? balance;
        if (amount <= 0m || amount > balance)
            return TenantMoneyCommandSupport.DepositConflict(command, "The refund must be positive and cannot exceed the held deposit balance.");

        var deposit = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Refund, SecurityDepositDirection.Decrease,
            amount, target.Currency, command.EffectiveOn, command.Description,
            command.BusinessKey, times.WallClockUtc, null, null,
            payoutExternalReference: TenantMoneyCommandSupport.Clean(command.ExternalReference));
        attempt.Persistence.Add(deposit);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(attempt, command, times.WallClockUtc,
            nameof(SecurityDepositEntry), deposit.Id, "Security deposit refund posted",
            new { deposit.Amount, deposit.EffectiveOn, deposit.PayoutExternalReference });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, deposit.Id, null, amount, null);
    }

    public Task AuthorizeReplayAsync(RefundSecurityDepositCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

public sealed class ReverseSecurityDepositEntryHandler
    : IAtomicCommandHandler<ReverseSecurityDepositEntryCommand, SecurityDepositMutationResult>,
      IAtomicReplayAuthorizer<ReverseSecurityDepositEntryCommand>
{
    public async Task<SecurityDepositMutationResult> HandleAsync(
        ReverseSecurityDepositEntryCommand command,
        IAtomicWriteAttempt attempt,
        CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        await attempt.Locking.AcquireAsync(
            AtomicLockResource.TenantAccount, command.TenantAccountId, ct);
        var times = await attempt.Persistence.ReadCommandTimesAsync(command.PortfolioId, ct);
        var target = await (
            from depositAccount in TenantMoneyCommandSupport.AuthorizedDepositAccounts(
                command, attempt.Persistence, times.WallClockUtc)
            join entry in attempt.Persistence.Query<SecurityDepositEntry>()
                on new { SecurityDepositAccountId = depositAccount.Id, depositAccount.PortfolioId }
                equals new { entry.SecurityDepositAccountId, entry.PortfolioId }
            where entry.Id == command.ReversesEntryId
            select new
            {
                depositAccount.Id,
                EntryId = entry.Id,
                entry.EntryType,
                entry.Direction,
                entry.Amount,
                entry.Currency,
                entry.TenantLedgerEntryId,
                HasReversal = attempt.Persistence.Query<SecurityDepositEntry>().Any(reversal =>
                    reversal.PortfolioId == command.PortfolioId
                    && reversal.SecurityDepositAccountId == command.SecurityDepositAccountId
                    && reversal.EntryType == SecurityDepositEntryType.Reversal
                    && reversal.ReversesEntryId == entry.Id),
                LinkedLedgerType = entry.TenantLedgerEntryId == null
                    ? (TenantLedgerEntryType?)null
                    : attempt.Persistence.Query<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => (TenantLedgerEntryType?)ledger.EntryType)
                        .SingleOrDefault(),
                LinkedLedgerDirection = entry.TenantLedgerEntryId == null
                    ? (TenantLedgerDirection?)null
                    : attempt.Persistence.Query<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => (TenantLedgerDirection?)ledger.Direction)
                        .SingleOrDefault(),
                LinkedLedgerAmount = entry.TenantLedgerEntryId == null
                    ? (decimal?)null
                    : attempt.Persistence.Query<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => (decimal?)ledger.Amount)
                        .SingleOrDefault(),
                LinkedLedgerCurrency = entry.TenantLedgerEntryId == null
                    ? null
                    : attempt.Persistence.Query<TenantLedgerEntry>()
                        .Where(ledger => ledger.Id == entry.TenantLedgerEntryId.Value
                            && ledger.PortfolioId == command.PortfolioId
                            && ledger.TenantAccountId == command.TenantAccountId)
                        .Select(ledger => ledger.Currency)
                        .SingleOrDefault(),
                LinkedLedgerHasReversal = entry.TenantLedgerEntryId != null
                    && attempt.Persistence.Query<TenantLedgerEntry>().Any(reversal =>
                        reversal.PortfolioId == command.PortfolioId
                        && reversal.TenantAccountId == command.TenantAccountId
                        && reversal.EntryType == TenantLedgerEntryType.Reversal
                        && reversal.ReversesEntryId == entry.TenantLedgerEntryId.Value),
                SourceAllowed = command.SourceStoredFileId == null
                    || attempt.Persistence.Query<StoredFile>().Any(file =>
                        file.Id == command.SourceStoredFileId.Value
                        && file.PortfolioId == command.PortfolioId
                        && file.DeletedAt == null),
            }).SingleOrDefaultAsync(ct);
        if (target is null) throw TenantMoneyCommandSupport.Unauthorized();
        if (!target.SourceAllowed)
            throw new ArgumentException(
                "Deposit reversal source provenance must belong to the current portfolio.");
        if (target.HasReversal)
            return TenantMoneyCommandSupport.DepositConflict(
                command, "The security-deposit entry has already been reversed.");
        if (target.EntryType is not (SecurityDepositEntryType.Receipt
                or SecurityDepositEntryType.Adjustment))
            return TenantMoneyCommandSupport.DepositConflict(command,
                "Only deposit receipts and standalone adjustments have complete local reversal provenance. " +
                "Deductions, payouts, and transfers require their dedicated external or paired workflow.");

        TenantLedgerEntry? ledgerReversal = null;
        AtomicLedgerAllocationSummary allocationCompensation = new();
        if (target.EntryType == SecurityDepositEntryType.Receipt)
        {
            var validLinkedReceipt = target.TenantLedgerEntryId is not null
                && target.LinkedLedgerType == TenantLedgerEntryType.PaymentReceipt
                && target.LinkedLedgerDirection == TenantLedgerDirection.Credit
                && target.LinkedLedgerAmount == target.Amount
                && string.Equals(target.LinkedLedgerCurrency, target.Currency,
                    StringComparison.Ordinal)
                && !target.LinkedLedgerHasReversal;
            if (!validLinkedReceipt)
                return TenantMoneyCommandSupport.DepositConflict(command,
                    "The deposit receipt's tenant-ledger provenance is missing, mismatched, or already reversed.");

            ledgerReversal = TenantMoneyCommandSupport.Ledger(
                command, TenantLedgerEntryType.Reversal, TenantLedgerDirection.Debit,
                target.Amount, target.Currency, command.EffectiveOn, null, command.Reason,
                $"{command.BusinessKey}:tenant-ledger", times.WallClockUtc,
                sourceStoredFileId: command.SourceStoredFileId);
            ledgerReversal.ReversesEntryId = target.TenantLedgerEntryId;
            attempt.Persistence.Add(ledgerReversal);
            await attempt.FlushBusinessAsync(ct);
            allocationCompensation = await attempt.TenantMoney.ReverseEntryAllocationsAsync(
                command.PortfolioId, command.TenantAccountId,
                target.TenantLedgerEntryId!.Value,
                $"{command.BusinessKey}:allocation", command.ActorUserId,
                times.WallClockUtc, ct);
        }

        var reversalDirection = target.Direction == SecurityDepositDirection.Increase
            ? SecurityDepositDirection.Decrease
            : SecurityDepositDirection.Increase;
        var depositReversal = TenantMoneyCommandSupport.DepositEntry(
            command, SecurityDepositEntryType.Reversal, reversalDirection,
            target.Amount, target.Currency, command.EffectiveOn, command.Reason,
            command.BusinessKey, times.WallClockUtc, ledgerReversal?.Id,
            command.SourceStoredFileId);
        depositReversal.ReversesEntryId = target.EntryId;
        attempt.Persistence.Add(depositReversal);
        await attempt.FlushBusinessAsync(ct);
        TenantMoneyCommandSupport.StageMutation(
            attempt, command, times.WallClockUtc, nameof(SecurityDepositEntry),
            depositReversal.Id, "Security deposit entry reversed", new
            {
                originalEntryType = target.EntryType,
                depositReversal.Amount,
                depositReversal.Direction,
                depositReversal.EffectiveOn,
                depositReversal.ReversesEntryId,
                TenantLedgerReversalId = ledgerReversal?.Id,
                allocationCompensation.AllocationCount,
                allocationCompensation.AllocatedAmount,
                reason = command.Reason.Trim(),
            });
        return new SecurityDepositMutationResult(true, true, command.TenantAccountId,
            command.SecurityDepositAccountId, depositReversal.Id, ledgerReversal?.Id,
            depositReversal.Amount, null);
    }

    public Task AuthorizeReplayAsync(ReverseSecurityDepositEntryCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct) =>
        TenantMoneyCommandSupport.AuthorizeDepositReplayAsync(command, persistence, ct);
}

internal static class TenantMoneyCommandSupport
{
    internal static IQueryable<TenantAccount> AuthorizedAccounts(
        ITenantMoneyCommand command, IAtomicPersistenceSession persistence, DateTime securityNowUtc)
    {
        var assignments = persistence.Query<MembershipRoleAssignment>().Where(assignment =>
            assignment.Status == MembershipRoleAssignmentStatus.Active
            && assignment.SuspendedAtUtc == null && assignment.RevokedAtUtc == null
            && assignment.EffectiveFromUtc <= securityNowUtc
            && (assignment.EffectiveToUtc == null || assignment.EffectiveToUtc > securityNowUtc));
        return persistence.Query<TenantAccount>().Where(account =>
            account.Id == command.TenantAccountId && account.PortfolioId == command.PortfolioId
            && account.ClosedAtUtc == null && account.LeaseManagement != null
            && persistence.Query<AuthSession>().Any(session => session.Id == command.AuthSessionId
                && session.UserId == command.ActorUserId && session.ActiveAccessContextId == command.AccessContextId
                && session.Status == AuthSessionStatus.Active && session.RevokedAtUtc == null
                && session.ExpiresAtUtc > securityNowUtc)
            && persistence.Query<WorkspaceAccessContext>().Any(context => context.Id == command.AccessContextId
                && context.UserId == command.ActorUserId && context.PortfolioId == command.PortfolioId
                && context.AccessRevision == command.ExpectedAccessRevision
                && context.Status == WorkspaceAccessContextStatus.Active
                && context.SuspendedAtUtc == null && context.RevokedAtUtc == null)
            && persistence.Query<WorkspaceMembership>().Any(membership =>
                membership.AccessContextId == command.AccessContextId && membership.PortfolioId == command.PortfolioId
                && membership.Status == WorkspaceMembershipStatus.Active
                && membership.SuspendedAtUtc == null && membership.RevokedAtUtc == null
                && membership.EffectiveFromUtc <= securityNowUtc
                && (membership.EffectiveToUtc == null || membership.EffectiveToUtc > securityNowUtc)
                && assignments.Any(assignment => assignment.WorkspaceMembershipId == membership.Id
                    && assignment.PortfolioId == command.PortfolioId
                    && (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.AllProperties
                        || (assignment.ScopeKind == MembershipRoleAssignmentScopeKind.SelectedProperties
                            && assignment.SelectedProperties.Any(scope => scope.PortfolioId == command.PortfolioId
                                && scope.PropertyId == account.LeaseManagement.PropertyId)))
                    && assignment.RoleProfile!.Capabilities.Any(capability =>
                        capability.CapabilityDefinition!.Key == command.RequiredCapability))));
    }

    internal static IQueryable<SecurityDepositAccount> AuthorizedDepositAccounts(
        ISecurityDepositMoneyCommand command, IAtomicPersistenceSession persistence, DateTime securityNowUtc) =>
        from deposit in persistence.Query<SecurityDepositAccount>()
        join account in AuthorizedAccounts(command, persistence, securityNowUtc)
            on new { deposit.TenantAccountId, deposit.PortfolioId }
            equals new { TenantAccountId = account.Id, account.PortfolioId }
        where deposit.Id == command.SecurityDepositAccountId
        select deposit;

    internal static async Task AuthorizeReplayAsync(ITenantMoneyCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedAccounts(command, persistence, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static async Task AuthorizeDepositReplayAsync(ISecurityDepositMoneyCommand command,
        IAtomicPersistenceSession persistence, CancellationToken ct)
    {
        Validate(command);
        var now = await persistence.ReadDatabaseClockUtcAsync(ct);
        if (!await AuthorizedDepositAccounts(command, persistence, now).AnyAsync(ct)) throw Unauthorized();
    }

    internal static void Validate(ITenantMoneyCommand command)
    {
        if (command.PortfolioId <= 0 || command.TenantAccountId <= 0 || command.ActorUserId <= 0
            || command.AuthSessionId == Guid.Empty || command.AccessContextId <= 0
            || command.ExpectedAccessRevision <= 0 || string.IsNullOrWhiteSpace(command.RequiredCapability)
            || string.IsNullOrWhiteSpace(command.DeliveryIdempotencyKey))
            throw new ArgumentException("Portfolio, tenant account, actor, access, capability, and operation key are required.");
        if (command is ISecurityDepositMoneyCommand deposit && deposit.SecurityDepositAccountId <= 0)
            throw new ArgumentException("Security deposit account is required.");
        decimal? amount = command switch
        {
            RecordTenantReceiptCommand receiptCommand => receiptCommand.Amount,
            PostTenantChargeCommand chargeCommand => chargeCommand.Amount,
            ReverseTenantChargeCommand => null,
            PostTenantCreditCommand creditCommand => creditCommand.Amount,
            PostTenantAdjustmentCommand adjustmentCommand => adjustmentCommand.Amount,
            ReverseTenantLedgerEntryCommand => null,
            RefundTenantPaymentCommand => null,
            FundSecurityDepositCommand fund => fund.Amount,
            DeductSecurityDepositCommand deduction => deduction.Amount,
            RefundSecurityDepositCommand refund => refund.Amount ?? 1m,
            ReverseSecurityDepositEntryCommand => null,
            _ => 0m,
        };
        if (amount is <= 0m) throw new ArgumentException("Amount must be positive.");
        if (command.DeliveryIdempotencyKey.Length > 200)
            throw new ArgumentException("Operation key cannot exceed 200 characters.");
        if ((command is PostTenantChargeCommand or ReverseTenantChargeCommand
                or PostTenantCreditCommand or PostTenantAdjustmentCommand
                or ReverseTenantLedgerEntryCommand)
            && command.RequiredCapability != CapabilityKeys.MoneyChargesManage)
            throw new ArgumentException("Tenant ledger mutations require the charge-management capability.");
        if ((command is RecordTenantReceiptCommand or RefundTenantPaymentCommand)
            && command.RequiredCapability != CapabilityKeys.MoneyPaymentsManage)
            throw new ArgumentException("Payment receipts and refunds require the payment-management capability.");
        if (command is ISecurityDepositMoneyCommand
            && command.RequiredCapability != CapabilityKeys.MoneyDepositsManage)
            throw new ArgumentException("Security-deposit mutations require the deposit-management capability.");
        if (command is RecordTenantReceiptCommand receipt
            && (receipt.EffectiveOn == default || string.IsNullOrWhiteSpace(receipt.Description)
                || receipt.Description.Trim().Length > 500
                || string.IsNullOrWhiteSpace(receipt.PaymentMethodSummary)
                || receipt.PaymentMethodSummary.Trim().Length > 200))
            throw new ArgumentException("Receipt date, description, and payment method are required and must fit their limits.");
        if (command is PostTenantChargeCommand charge
            && (charge.EffectiveOn == default || charge.DueOn == default
                || string.IsNullOrWhiteSpace(charge.Description)
                || charge.Description.Trim().Length > 500
                || charge.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Charge dates, description, and valid source provenance are required.");
        if (command is ReverseTenantChargeCommand reversal
            && (reversal.ReversesEntryId <= 0 || reversal.EffectiveOn == default
                || string.IsNullOrWhiteSpace(reversal.Reason)
                || reversal.Reason.Trim().Length > 500
                || reversal.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Charge entry, reversal date, reason, and valid source provenance are required.");
        if (command is PostTenantCreditCommand credit
            && (credit.EffectiveOn == default || string.IsNullOrWhiteSpace(credit.Description)
                || credit.Description.Trim().Length > 500
                || credit.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Credit date, description, and valid source provenance are required.");
        if (command is PostTenantAdjustmentCommand adjustment
            && (!Enum.IsDefined(adjustment.Direction)
                || adjustment.EffectiveOn == default
                || string.IsNullOrWhiteSpace(adjustment.Description)
                || adjustment.Description.Trim().Length > 500
                || adjustment.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Adjustment direction, date, description, and valid source provenance are required.");
        if (command is ReverseTenantLedgerEntryCommand ledgerReversal
            && (ledgerReversal.ReversesEntryId <= 0 || ledgerReversal.EffectiveOn == default
                || string.IsNullOrWhiteSpace(ledgerReversal.Reason)
                || ledgerReversal.Reason.Trim().Length > 500
                || ledgerReversal.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Ledger entry, reversal date, reason, and valid source provenance are required.");
        if (command is RefundTenantPaymentCommand paymentRefund
            && (paymentRefund.PaymentEntryId <= 0 || paymentRefund.EffectiveOn == default
                || string.IsNullOrWhiteSpace(paymentRefund.Reason)
                || paymentRefund.Reason.Trim().Length > 500
                || paymentRefund.PaymentMethodSummary?.Trim().Length > 200
                || paymentRefund.ExternalReference?.Trim().Length > 200
                || paymentRefund.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Payment entry, refund date, reason, and valid source provenance are required.");
        if (command is ReverseSecurityDepositEntryCommand depositReversal
            && (depositReversal.ReversesEntryId <= 0
                || depositReversal.EffectiveOn == default
                || string.IsNullOrWhiteSpace(depositReversal.Reason)
                || depositReversal.Reason.Trim().Length > 500
                || depositReversal.SourceStoredFileId is <= 0))
            throw new ArgumentException(
                "Deposit entry, reversal date, reason, and valid source provenance are required.");
        if (command is RefundSecurityDepositCommand payout
            && payout.ExternalReference?.Trim().Length > 200)
            throw new ArgumentException("Payout reference cannot exceed 200 characters.");
    }

    internal static TenantPaymentAttempt ManualAttempt(ITenantMoneyCommand command, string currency,
        decimal amount, string method, string? externalReference, string? payerName,
        string? checkNumber, string? bankName, DateTime now) => new()
    {
        PortfolioId = command.PortfolioId,
        TenantAccountId = command.TenantAccountId,
        Provider = "manual",
        ProviderObjectId = string.IsNullOrWhiteSpace(externalReference) ? null : externalReference.Trim(),
        IdempotencyKey = command.DeliveryIdempotencyKey,
        AttemptType = TenantPaymentAttemptType.Charge,
        State = TenantPaymentAttemptState.Succeeded,
        Amount = amount,
        Currency = currency,
        PaymentMethodSummary = method.Trim(),
        PayerName = payerName?.Trim(),
        CheckNumber = checkNumber?.Trim(),
        BankName = bankName?.Trim(),
        PreparedAtUtc = now,
        SubmittedAtUtc = now,
        SettledAtUtc = now,
        UpdatedAtUtc = now,
        AttemptCount = 1,
        CreatedByUserId = command.ActorUserId,
    };

    internal static TenantLedgerEntry Ledger(ITenantMoneyCommand command,
        TenantLedgerEntryType type, TenantLedgerDirection direction, decimal amount,
        string currency, DateOnly effectiveOn, DateOnly? dueOn, string description,
        string businessKey, DateTime now, long? providerAttemptId = null,
        int? sourceStoredFileId = null) => new()
    {
        PortfolioId = command.PortfolioId,
        TenantAccountId = command.TenantAccountId,
        EntryType = type,
        Direction = direction,
        Amount = amount,
        Currency = currency,
        EffectiveOn = effectiveOn,
        DueOn = dueOn,
        PostedAtUtc = now,
        Description = description.Trim(),
        BusinessKey = businessKey,
        ProviderPaymentAttemptId = providerAttemptId,
        SourceStoredFileId = sourceStoredFileId,
        CreatedByUserId = command.ActorUserId,
    };

    internal static SecurityDepositEntry DepositEntry(ISecurityDepositMoneyCommand command,
        SecurityDepositEntryType type, SecurityDepositDirection direction, decimal amount,
        string currency, DateOnly effectiveOn, string description, string businessKey,
        DateTime now, long? tenantLedgerEntryId, int? sourceStoredFileId,
        string? payoutExternalReference = null) => new()
    {
        PortfolioId = command.PortfolioId,
        SecurityDepositAccountId = command.SecurityDepositAccountId,
        EntryType = type,
        Direction = direction,
        Amount = amount,
        Currency = currency,
        EffectiveOn = effectiveOn,
        PostedAtUtc = now,
        BusinessKey = businessKey,
        Description = description.Trim(),
        TenantLedgerEntryId = tenantLedgerEntryId,
        SourceStoredFileId = sourceStoredFileId,
        PayoutExternalReference = payoutExternalReference,
        CreatedByUserId = command.ActorUserId,
    };

    internal static string? Clean(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal static DateTime CommandTimestamp(DateTime requestedUtc, DateTime fallbackUtc)
    {
        var timestamp = requestedUtc == default ? fallbackUtc : requestedUtc;
        return timestamp.Kind switch
        {
            DateTimeKind.Utc => timestamp,
            DateTimeKind.Local => timestamp.ToUniversalTime(),
            _ => DateTime.SpecifyKind(timestamp, DateTimeKind.Utc),
        };
    }

    internal static Task<AtomicLedgerAllocationSummary> AllocateOldestAsync(
        int portfolioId, int accountId, long receiptId, decimal available, string businessKey,
        int actorUserId, DateTime now, IAtomicWriteAttempt attempt, CancellationToken ct,
        TenantLedgerEntryType? onlyType = null)
    {
        return attempt.TenantMoney.AllocateOldestChargesAsync(
            portfolioId, accountId, receiptId, available, businessKey,
            actorUserId, now, onlyType?.ToString(), ct);
    }

    internal static void StageMutation(IAtomicWriteAttempt attempt, ITenantMoneyCommand command,
        DateTime now, string entityType, long entityId, string reason, object values)
    {
        attempt.StageSemanticEvent(new AtomicSemanticAudit(command.PortfolioId, nameof(TenantAccount),
            command.TenantAccountId, AuditLogOperation.Updated, UserId: command.ActorUserId,
            NewValues: JsonSerializer.Serialize(values), ChangeReason: reason), now);
        attempt.StageOutbox(new OutboxMessage
        {
            PortfolioId = command.PortfolioId,
            MessageType = "data-update",
            Payload = JsonSerializer.Serialize(new { entityType, entityId,
                data = new { command.TenantAccountId, mutation = reason } }),
            IdempotencyKey = OutboxIdempotency.Create("tenant-money", command.DeliveryIdempotencyKey),
            CreatedAtUtc = now,
            NextAttemptAtUtc = now,
        });
    }

    internal static SecurityDepositMutationResult DepositConflict(
        ISecurityDepositMoneyCommand command, string error) => new(true, false,
            command.TenantAccountId, command.SecurityDepositAccountId, 0, null, 0, error);

    internal static UnauthorizedAccessException Unauthorized() => new(
        "The tenant account is not authorized in the current access context, capability, and property scope.");
}
