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
        var writes = new CapturingJobStepWriteExecutor(
            new ApplyScheduledRentChargeBatchResult(3));
        var service = new RentChargeService(
            writes,
            null!,
            new FixedTimeProvider(BusinessNowUtc),
            NullLogger<RentChargeService>.Instance);

        var count = await service.GenerateAsync();

        count.Should().Be(3);
        var command = writes.Command.Should().BeOfType<ApplyScheduledRentChargeBatchCommand>().Subject;
        command.BusinessNowUtc.Should().Be(BusinessNowUtc);
        command.BatchSize.Should().BeGreaterThan(0);
        writes.OperationName.Should().Be("scheduled-tenant-charges.rent.apply");
        writes.ResultContract.Should().Be("scheduled-tenant-charges.rent.apply.v1");
        writes.StepKey.Should().Be(command.RunToken.ToString("N"));
    }

    [Fact]
    public async Task Generate_RethrowsAtomicFailureSoSimulationCommandCanSurfaceError()
    {
        var failure = new InvalidOperationException("permission denied for table TenantLedgerEntries");
        var service = new RentChargeService(
            new CapturingJobStepWriteExecutor(failure),
            null!,
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
