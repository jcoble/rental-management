using FluentAssertions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Payments;
using RentalCommand.Data.Payments;

namespace RentalCommand.IntegrationTests;

public sealed class TenantMoneyRecoveryWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [Fact]
    public void HistoricalRent_PreservesFrozenFingerprint()
    {
        AtomicCommandFingerprint.Create(HistoricalRent()).Should().Be(
            "4f41a2c3bb3031e9330a4f9ae48f403677f5ec78bab282154f6ba219c2b03486");
    }

    [Fact]
    public void FourRecoveries_PreserveIdentitiesResultContractsAndLegacyLocks()
    {
        var cases = new (IAtomicCommandData Command, string Operation, string Contract,
            WriteLockProtocol Protocol, string LockNamespace, int LockId)[]
        {
            (OpeningDeposits(), "opening-security-deposits.recover",
                "opening-security-deposits.recover.v1", WriteLockProtocol.Portfolio,
                "Portfolio", 1),
            (HistoricalRent(), "historical-rent-charge.recover",
                "historical-rent-charge.recover.v1", WriteLockProtocol.TenantAccount,
                "TenantAccount", 42),
            (RefundedAllocation(), "refunded-tenant-allocation.recover",
                "refunded-tenant-allocation.recover.v1", WriteLockProtocol.TenantAccount,
                "TenantAccount", 42),
            (LateFees(), "late-fee-charges.recover",
                "late-fee-charges.recover.v1", WriteLockProtocol.Portfolio,
                "Portfolio", 1),
        };

        cases.Should().AllSatisfy(item =>
        {
            var write = TenantMoneyWriteSupport.Write(
                item.Command,
                (_, _, _) => Task.FromResult(true),
                (_, _, _) => Task.CompletedTask);
            write.OperationName.Should().Be(item.Operation);
            write.ResultContract.Should().Be(item.Contract);
            write.LockPlan.Protocol.Should().Be(item.Protocol);
            write.LockPlan.Locks.Select(value => value.LockNamespace)
                .Should().Equal(item.LockNamespace);
            write.LockPlan.Locks.Single().IntegerId.Should().Be(item.LockId);
        });
    }

    [Fact]
    public async Task AllLegacyRecoveryHandlerArmsThrow()
    {
        var opening = OpeningDeposits();
        var historical = HistoricalRent();
        var refunded = RefundedAllocation();
        var lateFees = LateFees();
        var calls = new Func<Task>[]
        {
            () => new RecoverOpeningSecurityDepositsHandler(null!).HandleAsync(opening, null!, default),
            () => new RecoverHistoricalRentChargeHandler(null!).HandleAsync(historical, null!, default),
            () => new RecoverRefundedTenantAllocationHandler(null!).HandleAsync(refunded, null!, default),
            () => new RecoverLateFeeChargesHandler(null!).HandleAsync(lateFees, null!, default),
            () => new RecoverOpeningSecurityDepositsHandler(null!).AuthorizeReplayAsync(opening, null!, default),
            () => new RecoverHistoricalRentChargeHandler(null!).AuthorizeReplayAsync(historical, null!, default),
            () => new RecoverRefundedTenantAllocationHandler(null!).AuthorizeReplayAsync(refunded, null!, default),
            () => new RecoverLateFeeChargesHandler(null!).AuthorizeReplayAsync(lateFees, null!, default),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Tenant-money writes no longer use the legacy atomic handlers.");
        }
    }

    private static RecoverOpeningSecurityDepositsCommand OpeningDeposits() => new(
        1, new DateOnly(2099, 8, 20), 2, 2_500m, "FIN-OPENING-FROZEN", 7,
        SessionId, 9, 3, "money.deposits.manage",
        "opening-security-deposits:1:frozen");

    private static RecoverHistoricalRentChargeCommand HistoricalRent() => new(
        1, 42, 17, 81, 82, 83,
        new DateOnly(2099, 8, 4), new DateOnly(2099, 8, 1),
        new DateOnly(2099, 8, 1), new DateOnly(2099, 8, 4),
        1_377.42m, 1_525m, 1_525m, "FIN-HISTORICAL-FROZEN", 7,
        SessionId, 9, 3, "money.charges.manage",
        "historical-rent:1:42:frozen");

    private static RecoverRefundedTenantAllocationCommand RefundedAllocation() => new(
        1, 42, 83, 81, 82, 84, 1_377.42m, 1_525m,
        "FIN-REFUNDED-FROZEN", 7, SessionId, 9, 3, "money.charges.manage",
        "refunded-allocation-recovery:1:42:83",
        "refunded-allocation-recovery:1:42:frozen");

    private static RecoverLateFeeChargesCommand LateFees() => new(
        1,
        [new RecoverLateFeeChargeRow(42, 81, 75m, 50m, AlreadyReversed: false)],
        1, 1, 1, 75m, 50m, "FIN-LATE-FEE-FROZEN", 7,
        SessionId, 9, 3, "money.charges.manage",
        "late-fee-recovery:1:frozen");
}
