using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Operations;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

internal static class AtomicDomainTestKernel
{
    internal static ServiceProvider CreateForWorkOrders(string connectionString)
    {
        var services = Core(connectionString);
        services.AddAtomicCommandHandler<
            CreateWorkOrderCommand,
            OperationMutationResult,
            CreateWorkOrderHandler>();
        services.AddAtomicCommandHandler<
            UpdateWorkOrderCommand,
            OperationMutationResult,
            UpdateWorkOrderHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForApplications(string connectionString)
    {
        var services = Core(connectionString);
        services.AddAtomicCommandHandler<
            AtomicRentalMutationCommand,
            AtomicRentalMutationResult,
            AtomicRentalMutationHandler>();
        services.AddAtomicCommandHandler<
            AtomicPublicApplicationSubmissionCommand,
            AtomicPublicApplicationSubmissionResult,
            AtomicPublicApplicationSubmissionHandler>();
        services.AddAtomicCommandHandler<
            AtomicWorkspaceCoreMutationCommand,
            AtomicWorkspaceCoreMutationResult,
            AtomicWorkspaceCoreMutationHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForCoreCrud(string connectionString)
    {
        var services = Core(connectionString);
        services.AddAtomicCommandHandler<
            AtomicCoreCrudMutationCommand,
            AtomicCoreCrudMutationResult,
            AtomicCoreCrudMutationHandler>();
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Core(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseSqlite(connectionString)
                .AddInterceptors(SqliteDatabaseClockInterceptor.Instance)
                .UseAtomicPersistenceKernel(provider));
        return services;
    }

}
