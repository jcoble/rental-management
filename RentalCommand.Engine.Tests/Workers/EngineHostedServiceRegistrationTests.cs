using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using RentalCommand.Engine;
using RentalCommand.Engine.Workers;

namespace RentalCommand.Engine.Tests.Workers;

public sealed class EngineHostedServiceRegistrationTests
{
    [Fact]
    public void CommandBridgeOnly_RegistersOnlyBridgeAndAdvisoryLockWatcher()
    {
        var services = new ServiceCollection();

        services.AddEngineHostedServices(simulationEnabled: true, commandBridgeOnly: true);

        services
            .Where(descriptor => descriptor.ServiceType == typeof(IHostedService))
            .Select(descriptor => descriptor.ImplementationType)
            .Should()
            .BeEquivalentTo(
                [typeof(SimWorkerCommandWorker), typeof(AdvisoryLockWatcherService)],
                options => options.WithStrictOrdering());
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(SimWorkerRegistry));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IScanProcessingCycleService));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ScanProcessingWorker));
    }

    [Fact]
    public void CommandBridgeOnly_FailsClosedOutsideSimulation()
    {
        var services = new ServiceCollection();

        var act = () => services.AddEngineHostedServices(
            simulationEnabled: false,
            commandBridgeOnly: true);

        act.Should().Throw<InvalidOperationException>();
    }
}
