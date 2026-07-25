using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Configuration;
using RentalCommand.Engine.Services;

namespace RentalCommand.Engine.Tests.Automation;

public sealed class LateFeeServiceTests
{
    [Fact]
    public async Task Assess_DelegatesToCanonicalAtomicLateFeeBatch_WithCaps()
    {
        var atomic = new CapturingAtomicUnitOfWork(
            new ApplyScheduledTenantChargeBatchResult(0, 2));
        var config = new NotificationsConfig
        {
            StateLateFeeCaps = new Dictionary<string, LateFeeCap>
            {
                ["CA"] = new() { MaxFlat = 75m, MaxPercentOfRent = 6m },
            },
        };
        var service = new LateFeeService(
            atomic,
            Options.Create(config),
            NullLogger<LateFeeService>.Instance);

        var count = await service.AssessAsync();

        count.Should().Be(2);
        var command = atomic.Command.Should().BeOfType<ApplyScheduledTenantChargeBatchCommand>().Subject;
        command.IncludeRentCharges.Should().BeFalse();
        command.IncludeLateFeeCharges.Should().BeTrue();
        command.StateLateFeeCapsJson.Should().Contain("CA").And.Contain("75");
    }
}
