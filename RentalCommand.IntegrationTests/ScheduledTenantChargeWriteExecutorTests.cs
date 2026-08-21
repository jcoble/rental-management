using FluentAssertions;
using RentalCommand.Core.Accounting;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Operations;
using RentalCommand.Data.Accounting;
using RentalCommand.Data.Payments;

namespace RentalCommand.IntegrationTests;

public sealed class ScheduledTenantChargeWriteExecutorTests
{
    private static readonly Guid SessionId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [Fact]
    public void ConfigurationAndScheduledRent_PreserveFrozenFingerprints()
    {
        AtomicCommandFingerprint.Create(Create()).Should().Be(
            "311f506cf0a52cd84a871d1c08fd5b62a062dc7f650633b3d0909392c9195392");
        AtomicCommandFingerprint.Create(Rent()).Should().Be(
            "fe45b09d4a1c4e4677f38c7402649d553a50cdedfb4d9ef1840226f1b7f92cbf");
    }

    [Fact]
    public void FiveWrites_PreserveIdentitiesResultContractsAndLegacyLocks()
    {
        AssertWrite(Create(), "tenant-account.recurring-charge.create.v1",
            "tenant-account.recurring-charge.configuration.v1",
            WriteLockProtocol.TenantAccount, "TenantAccount");
        AssertWrite(Update(), "tenant-account.recurring-charge.update.v1",
            "tenant-account.recurring-charge.configuration.v1",
            WriteLockProtocol.TenantAccountRecurringCharge,
            "TenantAccount", "RecurringTenantCharge");
        AssertWrite(Deactivate(), "tenant-account.recurring-charge.deactivate.v1",
            "tenant-account.recurring-charge.configuration.v1",
            WriteLockProtocol.TenantAccountRecurringCharge,
            "TenantAccount", "RecurringTenantCharge");
        AssertWrite(Rent(), "scheduled-tenant-charges.rent.apply",
            "scheduled-tenant-charges.rent.apply.v1", null);
        AssertWrite(LateFee(), "scheduled-tenant-charges.late-fee.apply",
            "scheduled-tenant-charges.late-fee.apply.v1", null);
    }

    [Fact]
    public async Task AllLegacyHandlerArmsThrow()
    {
        var create = Create();
        var update = Update();
        var deactivate = Deactivate();
        var rent = Rent();
        var lateFee = LateFee();
        var calls = new Func<Task>[]
        {
            () => new CreateRecurringTenantChargeHandler(null!).HandleAsync(create, null!, default),
            () => new CreateRecurringTenantChargeHandler(null!).AuthorizeReplayAsync(create, null!, default),
            () => new UpdateRecurringTenantChargeHandler(null!).HandleAsync(update, null!, default),
            () => new UpdateRecurringTenantChargeHandler(null!).AuthorizeReplayAsync(update, null!, default),
            () => new DeactivateRecurringTenantChargeHandler(null!).HandleAsync(deactivate, null!, default),
            () => new DeactivateRecurringTenantChargeHandler(null!).AuthorizeReplayAsync(deactivate, null!, default),
            () => new ApplyScheduledRentChargeBatchHandler(null!).HandleAsync(rent, null!, default),
            () => new ApplyScheduledRentChargeBatchHandler(null!).AuthorizeReplayAsync(rent, null!, default),
            () => new ApplyScheduledLateFeeChargeBatchHandler(null!).HandleAsync(lateFee, null!, default),
            () => new ApplyScheduledLateFeeChargeBatchHandler(null!).AuthorizeReplayAsync(lateFee, null!, default),
        };

        foreach (var call in calls)
        {
            await call.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage("Tenant-money writes no longer use the legacy atomic handlers.");
        }
    }

    private static void AssertWrite<TCommand>(
        TCommand command,
        string operation,
        string contract,
        WriteLockProtocol? protocol,
        params string[] lockNamespaces)
        where TCommand : notnull, IAtomicCommandData
    {
        var write = TenantMoneyWriteSupport.Write(
            command,
            (_, _, _) => Task.FromResult(true),
            (_, _, _) => Task.CompletedTask);
        write.OperationName.Should().Be(operation);
        write.ResultContract.Should().Be(contract);
        write.LockPlan.Protocol.Should().Be(protocol);
        write.LockPlan.Locks.Select(item => item.LockNamespace).Should().Equal(lockNamespaces);
    }

    private static CreateRecurringTenantChargeCommand Create() => new(
        1, Actor(), 42, 17, "Frozen recurring rent", 275m, 81,
        new DateOnly(2099, 8, 1), new DateOnly(2100, 7, 31), 1,
        new DateOnly(2099, 8, 1),
        new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc),
        "recurring-charge-create:frozen");

    private static UpdateRecurringTenantChargeCommand Update() => new(
        1, Actor(), 42, 84, "Updated recurring rent", 300m, 82,
        new DateOnly(2099, 9, 1), new DateOnly(2100, 8, 31), 5,
        new DateOnly(2099, 9, 5),
        new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc),
        "recurring-charge-update:frozen");

    private static DeactivateRecurringTenantChargeCommand Deactivate() => new(
        1, Actor(), 42, 84,
        new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc),
        "recurring-charge-deactivate:frozen");

    private static ApplyScheduledRentChargeBatchCommand Rent() => new(
        Guid.Parse("44444444-4444-4444-4444-444444444444"),
        new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc), 200);

    private static ApplyScheduledLateFeeChargeBatchCommand LateFee() => new(
        Guid.Parse("55555555-5555-5555-5555-555555555555"),
        new DateTime(2099, 8, 20, 12, 0, 0, DateTimeKind.Utc), 200,
        "[{\"State\":\"CA\",\"MaxFlat\":75}]");

    private static StaffOperationActor Actor() => new(7, SessionId, 9, 3);
}
