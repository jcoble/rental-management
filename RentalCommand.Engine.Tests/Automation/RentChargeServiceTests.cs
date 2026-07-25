using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class RentChargeServiceTests
{
    [Fact]
    public async Task Generate_DelegatesToCanonicalAtomicRentBatch()
    {
        var atomic = new CapturingAtomicUnitOfWork(
            new ApplyScheduledTenantChargeBatchResult(3, 0));
        var service = new RentChargeService(atomic, NullLogger<RentChargeService>.Instance);

        var count = await service.GenerateAsync();

        count.Should().Be(3);
        var command = atomic.Command.Should().BeOfType<ApplyScheduledTenantChargeBatchCommand>().Subject;
        command.IncludeRentCharges.Should().BeTrue();
        command.IncludeLateFeeCharges.Should().BeFalse();
        command.BatchSize.Should().BeGreaterThan(0);
    }
}
