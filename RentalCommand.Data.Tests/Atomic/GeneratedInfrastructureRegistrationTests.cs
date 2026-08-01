using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Documents;
using RentalCommand.Data.Notifications;

namespace RentalCommand.Data.Tests.Atomic;

public sealed class GeneratedInfrastructureRegistrationTests
{
    [Fact]
    public void AddGeneratedInfrastructureStores_RegistersPurposeSpecificStoresThroughDataScope()
    {
        var services = new ServiceCollection();

        services.AddAtomicPersistenceKernel();
        services.AddPendingFileUploadStore();
        services.AddGeneratedInfrastructureStores();

        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IInternalSetBasedWriteScope));
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IPendingFileUploadStore) &&
            descriptor.ImplementationFactory != null);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ITenantNoticeWorkClaimStore) &&
            descriptor.ImplementationFactory != null);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(ITenantNoticeCandidateStore) &&
            descriptor.ImplementationFactory != null);
        services.Should().ContainSingle(descriptor =>
            descriptor.ServiceType == typeof(IEngineWorkerHeartbeatStore));
    }
}
