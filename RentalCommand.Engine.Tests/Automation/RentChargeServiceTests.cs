using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class RentChargeServiceTests
{
    private static readonly DateTime BusinessNowUtc =
        new(2027, 01, 22, 09, 15, 00, DateTimeKind.Utc);

    [Fact]
    public async Task Generate_DelegatesToCanonicalAtomicRentBatch()
    {
        var atomic = new CapturingAtomicUnitOfWork(
            new ApplyScheduledTenantChargeBatchResult(3, 0));
        var service = new RentChargeService(
            atomic,
            new FixedTimeProvider(BusinessNowUtc),
            NullLogger<RentChargeService>.Instance);

        var count = await service.GenerateAsync();

        count.Should().Be(3);
        var command = atomic.Command.Should().BeOfType<ApplyScheduledTenantChargeBatchCommand>().Subject;
        command.IncludeRentCharges.Should().BeTrue();
        command.IncludeLateFeeCharges.Should().BeFalse();
        command.BusinessNowUtc.Should().Be(BusinessNowUtc);
        command.BatchSize.Should().BeGreaterThan(0);
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
