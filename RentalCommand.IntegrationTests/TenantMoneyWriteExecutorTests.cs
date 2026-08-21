using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Enums;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Payments;

namespace RentalCommand.IntegrationTests;

public sealed class TenantMoneyWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [Fact]
    public void ReceiptAndDepositFund_PreserveFrozenFingerprints()
    {
        AtomicCommandFingerprint.Create(Receipt()).Should().Be(
            "d9c25469fd4b2f2533ddaa3b7d1f0b8967d01911a8ce23433d53f2cb8114b725");
        AtomicCommandFingerprint.Create(Fund()).Should().Be(
            "02742925b331144a13cfda62b8e1b774977f9f33a4c66d715078b40af4b32513");
    }

    [Fact]
    public void ElevenWrites_PreserveIdentitiesResultContractsAndTenantAccountLocks()
    {
        var expected = new[]
        {
            ("tenant-account.receipt.record", "tenant-account.receipt.record.v1"),
            ("tenant-account.charge.post", "tenant-account.charge.mutation.v1"),
            ("tenant-account.charge.reverse", "tenant-account.charge.mutation.v1"),
            ("tenant-account.credit.post", "tenant-account.ledger.mutation.v1"),
            ("tenant-account.adjustment.post", "tenant-account.ledger.mutation.v1"),
            ("tenant-account.ledger.reverse", "tenant-account.ledger.mutation.v1"),
            ("tenant-account.payment.refund", "tenant-account.payment.refund.v1"),
            ("tenant-account.deposit.fund", "tenant-account.deposit.mutation.v1"),
            ("tenant-account.deposit.deduct", "tenant-account.deposit.mutation.v1"),
            ("tenant-account.deposit.refund", "tenant-account.deposit.mutation.v1"),
            ("tenant-account.deposit.reverse", "tenant-account.deposit.mutation.v1"),
        };

        Commands().Zip(expected).Should().AllSatisfy(pair =>
        {
            var write = TenantMoneyWriteSupport.Write(
                pair.First,
                (_, _, _) => Task.FromResult(true),
                (_, _, _) => Task.CompletedTask);
            write.OperationName.Should().Be(pair.Second.Item1);
            write.ResultContract.Should().Be(pair.Second.Item2);
            write.LockPlan.Protocol.Should().Be(WriteLockProtocol.TenantAccount);
            write.LockPlan.Locks.Select(item => item.LockNamespace).Should().Equal("TenantAccount");
            write.LockPlan.Locks.Single().IntegerId.Should().Be(pair.First.TenantAccountId);
        });
    }

    [Fact]
    public async Task AllLegacyHandlerArmsThrow()
    {
        var commands = Commands();
        var calls = new Func<Task>[]
        {
            () => new RecordTenantReceiptHandler(null!).HandleAsync((RecordTenantReceiptCommand)commands[0], null!, default),
            () => new PostTenantChargeHandler(null!).HandleAsync((PostTenantChargeCommand)commands[1], null!, default),
            () => new ReverseTenantChargeHandler(null!).HandleAsync((ReverseTenantChargeCommand)commands[2], null!, default),
            () => new PostTenantCreditHandler(null!).HandleAsync((PostTenantCreditCommand)commands[3], null!, default),
            () => new PostTenantAdjustmentHandler(null!).HandleAsync((PostTenantAdjustmentCommand)commands[4], null!, default),
            () => new ReverseTenantLedgerEntryHandler(null!).HandleAsync((ReverseTenantLedgerEntryCommand)commands[5], null!, default),
            () => new RefundTenantPaymentHandler(null!).HandleAsync((RefundTenantPaymentCommand)commands[6], null!, default),
            () => new FundSecurityDepositHandler(null!).HandleAsync((FundSecurityDepositCommand)commands[7], null!, default),
            () => new DeductSecurityDepositHandler(null!).HandleAsync((DeductSecurityDepositCommand)commands[8], null!, default),
            () => new RefundSecurityDepositHandler(null!).HandleAsync((RefundSecurityDepositCommand)commands[9], null!, default),
            () => new ReverseSecurityDepositEntryHandler(null!).HandleAsync((ReverseSecurityDepositEntryCommand)commands[10], null!, default),
            () => new RecordTenantReceiptHandler(null!).AuthorizeReplayAsync((RecordTenantReceiptCommand)commands[0], null!, default),
            () => new PostTenantChargeHandler(null!).AuthorizeReplayAsync((PostTenantChargeCommand)commands[1], null!, default),
            () => new ReverseTenantChargeHandler(null!).AuthorizeReplayAsync((ReverseTenantChargeCommand)commands[2], null!, default),
            () => new PostTenantCreditHandler(null!).AuthorizeReplayAsync((PostTenantCreditCommand)commands[3], null!, default),
            () => new PostTenantAdjustmentHandler(null!).AuthorizeReplayAsync((PostTenantAdjustmentCommand)commands[4], null!, default),
            () => new ReverseTenantLedgerEntryHandler(null!).AuthorizeReplayAsync((ReverseTenantLedgerEntryCommand)commands[5], null!, default),
            () => new RefundTenantPaymentHandler(null!).AuthorizeReplayAsync((RefundTenantPaymentCommand)commands[6], null!, default),
            () => new FundSecurityDepositHandler(null!).AuthorizeReplayAsync((FundSecurityDepositCommand)commands[7], null!, default),
            () => new DeductSecurityDepositHandler(null!).AuthorizeReplayAsync((DeductSecurityDepositCommand)commands[8], null!, default),
            () => new RefundSecurityDepositHandler(null!).AuthorizeReplayAsync((RefundSecurityDepositCommand)commands[9], null!, default),
            () => new ReverseSecurityDepositEntryHandler(null!).AuthorizeReplayAsync((ReverseSecurityDepositEntryCommand)commands[10], null!, default),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Tenant-money writes no longer use the legacy atomic handlers.");
        }
    }

    private static ITenantMoneyCommand[] Commands() =>
    [
        Receipt(), Charge(), ReverseCharge(), Credit(), Adjustment(), ReverseLedger(),
        RefundPayment(), Fund(), Deduct(), RefundDeposit(), ReverseDeposit(),
    ];

    private static RecordTenantReceiptCommand Receipt() => new(
        1, 42, 125.50m, new DateOnly(2099, 8, 20), "Frozen receipt", "Check",
        "receipt-42", "Frozen payer", "1001", "Frozen bank", 12, 81, 7,
        SessionId, 9, 3, "money.payments.manage", "manual-receipt:frozen",
        "tenant-receipt:1:42:frozen", new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc))
        { AllocateOldestCharges = true };

    private static PostTenantChargeCommand Charge() => new(
        1, 42, 75m, new DateOnly(2099, 8, 20), new DateOnly(2099, 9, 1),
        "Frozen charge", 12, 7, SessionId, 9, 3, "money.charges.manage",
        "tenant-charge:frozen", "tenant-charge:1:42:frozen", 17,
        new DateOnly(2099, 8, 1), new DateOnly(2099, 8, 31));

    private static ReverseTenantChargeCommand ReverseCharge() => new(
        1, 42, 81, new DateOnly(2099, 8, 20), "Frozen correction", 12, 7,
        SessionId, 9, 3, "money.charges.manage", "tenant-charge-reversal:frozen",
        "tenant-charge-reversal:1:42:81:frozen");

    private static PostTenantCreditCommand Credit() => new(
        1, 42, 25m, new DateOnly(2099, 8, 20), "Frozen credit", 12, true, 7,
        SessionId, 9, 3, "money.charges.manage", "tenant-credit:frozen",
        "tenant-credit:1:42:frozen", 81, 17);

    private static PostTenantAdjustmentCommand Adjustment() => new(
        1, 42, TenantLedgerDirection.Debit, 10m, new DateOnly(2099, 8, 20),
        "Frozen adjustment", 12, 7, SessionId, 9, 3, "money.charges.manage",
        "tenant-adjustment:frozen", "tenant-adjustment:1:42:frozen");

    private static ReverseTenantLedgerEntryCommand ReverseLedger() => new(
        1, 42, 82, new DateOnly(2099, 8, 20), "Frozen reversal", 12, 7,
        SessionId, 9, 3, "money.charges.manage", "tenant-ledger-reversal:frozen",
        "tenant-ledger-reversal:1:42:82:frozen");

    private static RefundTenantPaymentCommand RefundPayment() => new(
        1, 42, 83, new DateOnly(2099, 8, 20), "Frozen refund", "Check", "refund-42",
        12, 7, SessionId, 9, 3, "money.payments.manage", "tenant-payment-refund:frozen",
        "tenant-payment-refund:1:42:83:frozen");

    private static FundSecurityDepositCommand Fund() => new(
        1, 42, 18, 100m, new DateOnly(2099, 8, 20), "Frozen deposit", "Check",
        "deposit-42", 12, 7, SessionId, 9, 3, "money.deposits.manage",
        "deposit-fund:frozen", "deposit-fund:1:42:18:frozen");

    private static DeductSecurityDepositCommand Deduct() => new(
        1, 42, 18, 20m, new DateOnly(2099, 8, 20), "Frozen deduction", "Frozen note",
        12, 7, SessionId, 9, 3, "money.deposits.manage", "deposit-deduction:frozen",
        "deposit-deduction:1:42:18:frozen");

    private static RefundSecurityDepositCommand RefundDeposit() => new(
        1, 42, 18, 30m, new DateOnly(2099, 8, 20), "Frozen payout", "payout-42",
        7, SessionId, 9, 3, "money.deposits.manage", "deposit-refund:frozen",
        "deposit-refund:1:42:18:frozen");

    private static ReverseSecurityDepositEntryCommand ReverseDeposit() => new(
        1, 42, 18, 84, new DateOnly(2099, 8, 20), "Frozen deposit reversal", 12,
        7, SessionId, 9, 3, "money.deposits.manage", "deposit-reversal:frozen",
        "deposit-reversal:1:42:18:84:frozen");
}
