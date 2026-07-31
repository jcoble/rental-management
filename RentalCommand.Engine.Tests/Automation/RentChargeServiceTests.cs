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
            new ApplyScheduledRentChargeBatchResult(3));
        var service = new RentChargeService(
            atomic,
            new FixedTimeProvider(BusinessNowUtc),
            NullLogger<RentChargeService>.Instance);

        var count = await service.GenerateAsync();

        count.Should().Be(3);
        var command = atomic.Command.Should().BeOfType<ApplyScheduledRentChargeBatchCommand>().Subject;
        command.BusinessNowUtc.Should().Be(BusinessNowUtc);
        command.BatchSize.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Generate_RethrowsAtomicFailureSoSimulationCommandCanSurfaceError()
    {
        var failure = new InvalidOperationException("permission denied for table TenantLedgerEntries");
        var service = new RentChargeService(
            new CapturingAtomicUnitOfWork(failure),
            new FixedTimeProvider(BusinessNowUtc),
            NullLogger<RentChargeService>.Instance);

        var act = async () => await service.GenerateAsync();

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*TenantLedgerEntries*");
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
