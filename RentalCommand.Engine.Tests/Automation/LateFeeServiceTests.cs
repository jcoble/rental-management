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
        var writes = new CapturingJobStepWriteExecutor(
            new ApplyScheduledLateFeeChargeBatchResult(2));
        var config = new NotificationsConfig
        {
            StateLateFeeCaps = new Dictionary<string, LateFeeCap>
            {
                ["CA"] = new() { MaxFlat = 75m, MaxPercentOfRent = 6m },
            },
        };
        var service = new LateFeeService(
            writes,
            null!,
            new FixedTimeProvider(BusinessNowUtc),
            Options.Create(config),
            NullLogger<LateFeeService>.Instance);

        var count = await service.AssessAsync();

        count.Should().Be(2);
        var command = writes.Command.Should().BeOfType<ApplyScheduledLateFeeChargeBatchCommand>().Subject;
        command.BusinessNowUtc.Should().Be(BusinessNowUtc);
        command.StateLateFeeCapsJson.Should().Contain("CA").And.Contain("75");
        writes.OperationName.Should().Be("scheduled-tenant-charges.late-fee.apply");
        writes.ResultContract.Should().Be("scheduled-tenant-charges.late-fee.apply.v1");
        writes.StepKey.Should().Be(command.RunToken.ToString("N"));
    }

    private sealed class FixedTimeProvider(DateTime utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utcNow);
    }
}
