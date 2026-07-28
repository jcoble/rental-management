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
    private static readonly DateTime BusinessNowUtc =
        new(2027, 01, 22, 09, 15, 00, DateTimeKind.Utc);

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
            new FixedTimeProvider(BusinessNowUtc),
            Options.Create(config),
            NullLogger<LateFeeService>.Instance);

        var count = await service.AssessAsync();

        count.Should().Be(2);
        var command = atomic.Command.Should().BeOfType<ApplyScheduledTenantChargeBatchCommand>().Subject;
        command.IncludeRentCharges.Should().BeFalse();
        command.IncludeLateFeeCharges.Should().BeTrue();
        command.BusinessNowUtc.Should().Be(BusinessNowUtc);
        command.StateLateFeeCapsJson.Should().Contain("CA").And.Contain("75");
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
