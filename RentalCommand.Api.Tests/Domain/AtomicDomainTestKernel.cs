using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Scanning;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Auth;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Scanning;
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

    internal static ServiceProvider CreateForRentalCrudPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddAtomicCommandHandler<
            AtomicRentalMutationCommand,
            AtomicRentalMutationResult,
            AtomicRentalMutationHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForMoneyPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddAtomicCommandHandler<
            AtomicMoneyMutationCommand,
            AtomicMoneyMutationResult,
            AtomicMoneyMutationHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForInspectionsPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddAtomicCommandHandler<
            AtomicInspectionMutationCommand,
            AtomicInspectionMutationResult,
            AtomicInspectionMutationHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAccountBootstrapPostgreSql(NpgsqlConnection connection)
    {
        var services = CorePostgreSql(connection);
        services.AddAtomicCommandHandler<
            BootstrapAccountCommand,
            BootstrapAccountResult,
            BootstrapAccountHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForPasswordResetPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddAtomicCommandHandler<
            ResetAccountPasswordCommand,
            ResetAccountPasswordResult,
            ResetAccountPasswordHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScanRetryPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddAtomicCommandHandler<
            RetryScanDraftCommand,
            ScanDraftMutationResult,
            RetryScanDraftHandler>();
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

    private static ServiceCollection CorePostgreSql(string connectionString)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider));
        return services;
    }

    private static ServiceCollection CorePostgreSql(NpgsqlConnection connection)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
            builder.UseNpgsql(connection)
                .UseAtomicPersistenceKernel(provider));
        return services;
    }

}
