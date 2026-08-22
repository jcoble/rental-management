using Microsoft.EntityFrameworkCore;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Entities;
using RentalCommand.Core.Payments;

namespace RentalCommand.Data.Payments;

/// <summary>
/// Appends one reviewed allocation compensation after proving that the allocation consumed a
/// fully-refunded payment receipt. This is historical recovery, not another allocation route.
/// </summary>
public sealed class RecoverRefundedTenantAllocationRule
{
    private readonly RentalCommandDbContext _db;

    public RecoverRefundedTenantAllocationRule(RentalCommandDbContext db) => _db = db;

    public async Task<RecoverRefundedTenantAllocationResult> ExecuteAsync(
        RecoverRefundedTenantAllocationCommand command,
        IAtomicCommandContext context,
        CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        Validate(command);
        var times = await RentalCommand.Data.AtomicCommandClock.ReadCommandTimesAsync(_db, command.PortfolioId, ct);
        if (!await TenantMoneyCommandSupport
                .AuthorizedAccounts(command, _db, times.WallClockUtc)
                .AnyAsync(ct))
        {
            throw TenantMoneyCommandSupport.Unauthorized();
        }

        var recovery = await TenantMoneyPersistence.RecoverRefundedAllocationAsync(_db, context,
            command.PortfolioId,
            command.TenantAccountId,
            command.ExistingAllocationId,
            command.ExpectedDebitEntryId,
            command.ExpectedCreditEntryId,
            command.ExpectedRefundPaymentAttemptId,
            command.ExpectedAllocationAmount,
            command.ExpectedRefundAmount,
            command.BusinessKey,
            command.ActorUserId,
            times.EffectiveNowUtc,
            ct);
        if (!recovery.IsValid)
            throw new InvalidOperationException(recovery.ValidationError);

        var reference = command.FinancialReference.Trim();
        context.UseDatabaseWallClockForAudit(times.EffectiveNowUtc);
        TenantMoneyCommandSupport.StageMutation(
            context,
            command,
            times.EffectiveNowUtc,
            nameof(TenantLedgerAllocation),
            recovery.ReversalAllocationId,
            "Recovered allocation of a refunded tenant receipt",
            new
            {
                recovery.ReversedAllocationId,
                recovery.ReversalAllocationId,
                recovery.DebitEntryId,
                recovery.CreditEntryId,
                recovery.RefundPaymentAttemptId,
                recovery.ReversedAmount,
                FinancialReference = reference,
            });

        return new RecoverRefundedTenantAllocationResult(
            true,
            command.TenantAccountId,
            recovery.ReversedAllocationId,
            recovery.ReversalAllocationId,
            recovery.DebitEntryId,
            recovery.CreditEntryId,
            recovery.RefundPaymentAttemptId,
            recovery.ReversedAmount,
            reference);
    }

    public async Task AuthorizeAsync(
        RecoverRefundedTenantAllocationCommand command, IAtomicCommandContext context, CancellationToken ct)
    {
        TenantMoneyCommandSupport.Validate(command);
        Validate(command);
        await TenantMoneyCommandSupport.AuthorizeReplayAsync(command, _db, ct);
    }

    private static void Validate(RecoverRefundedTenantAllocationCommand command)
    {
        if (command.ExistingAllocationId <= 0
            || command.ExpectedDebitEntryId <= 0
            || command.ExpectedCreditEntryId <= 0
            || command.ExpectedRefundPaymentAttemptId <= 0
            || command.ExpectedAllocationAmount <= 0m
            || command.ExpectedRefundAmount <= 0m
            || command.ExpectedRefundAmount < command.ExpectedAllocationAmount
            || string.IsNullOrWhiteSpace(command.FinancialReference)
            || command.FinancialReference.Trim().Length > 80
            || string.IsNullOrWhiteSpace(command.BusinessKey)
            || command.BusinessKey.Length > 200)
        {
            throw new ArgumentException(
                "Refunded-allocation recovery requires an exact allocation, debit, receipt, refund context, amounts, reference, actor, access, and operation key.");
        }
    }
}
