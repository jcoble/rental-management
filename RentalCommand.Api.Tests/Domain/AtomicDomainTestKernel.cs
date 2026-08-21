using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Moq;
using RentalCommand.Api.Services.Domain;
using RentalCommand.Api.Writes;
using RentalCommand.Core.Atomic;
using RentalCommand.Core.Automation;
using RentalCommand.Core.Interfaces;
using RentalCommand.Core.Money;
using RentalCommand.Core.Operations;
using RentalCommand.Core.Auth;
using RentalCommand.Core.Payments;
using RentalCommand.Core.Scanning;
using RentalCommand.Core.Screening;
using RentalCommand.Data;
using RentalCommand.Data.Atomic;
using RentalCommand.Data.Auditing;
using RentalCommand.Data.Auth;
using RentalCommand.Data.Operations;
using RentalCommand.Data.Payments;
using RentalCommand.Data.Scanning;
using RentalCommand.Data.Screening;
using RentalCommand.TestCommon;

namespace RentalCommand.Api.Tests.Domain;

internal static class AtomicDomainTestKernel
{
    internal static ServiceProvider CreateForWorkOrders(string connectionString)
    {
        var services = Core(connectionString);
        AddWorkOrderHandlers(services);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAppointments(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = Core(connectionString, timeProvider, interceptors);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAppointmentsPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForWorkOrdersPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        AddWorkOrderHandlers(services);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForVendorDispatchPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddAtomicCommandHandler<
            DispatchWorkOrderToVendorCommand,
            DispatchWorkOrderToVendorResult,
            DispatchWorkOrderToVendorHandler>();
        services.AddAtomicCommandHandler<
            CancelVendorDispatchCommand,
            CancelVendorDispatchResult,
            CancelVendorDispatchHandler>();
        services.AddAtomicCommandHandler<
            RecoverVendorDispatchChronologyCommand,
            RecoverVendorDispatchChronologyResult,
            RecoverVendorDispatchChronologyHandler>();
        return services.BuildServiceProvider();
    }

    private static void AddWorkOrderHandlers(IServiceCollection services)
    {
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
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

    internal static ServiceProvider CreateForCoreCrud(string connectionString, TimeProvider? timeProvider = null)
    {
        var services = Core(connectionString, timeProvider);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<PropertyService>();
        services.AddScoped<TenantService>();
        services.AddAtomicCommandHandler<
            AtomicGuidedTenantSetupCommand,
            AtomicGuidedTenantSetupResult,
            AtomicGuidedTenantSetupHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForRecurringMaintenance(
        string connectionString,
        TimeProvider? timeProvider = null)
    {
        var services = Core(connectionString, timeProvider);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<RecurringMaintenanceTaskService>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForCoreCrudPostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddScoped<PropertyService>();
        services.AddScoped<TenantService>();
        services.AddAtomicCommandHandler<
            AtomicGuidedTenantSetupCommand,
            AtomicGuidedTenantSetupResult,
            AtomicGuidedTenantSetupHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForRentalCrudPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Mock.Of<IDataUpdateService>());
        services.AddSingleton(Mock.Of<IAuditTrailService>());
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        services.AddScoped<UnitService>();
        services.AddAtomicCommandHandler<
            AtomicRentalMutationCommand,
            AtomicRentalMutationResult,
            AtomicRentalMutationHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForMoneyPostgreSql(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null,
        TimeProvider? timeProvider = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScheduledTenantChargesPostgreSql(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null,
        TimeProvider? timeProvider = null)
    {
        var services = CorePostgreSql(connectionString, timeProvider, interceptors);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForInspectionsPostgreSql(
        string connectionString,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = CorePostgreSql(connectionString, interceptors: interceptors);
        services.AddPendingFileUploadStore();
        services.AddAtomicCommandHandler<
            AtomicInspectionMutationCommand,
            AtomicInspectionMutationResult,
            AtomicInspectionMutationHandler>();
        services.AddAtomicCommandHandler<
            DispatchWorkOrderToVendorCommand,
            DispatchWorkOrderToVendorResult,
            DispatchWorkOrderToVendorHandler>();
        services.AddAtomicCommandHandler<
            CompleteVendorDispatchFromInboundCommand,
            CompleteVendorDispatchFromInboundResult,
            CompleteVendorDispatchFromInboundHandler>();
        AddWorkOrderHandlers(services);
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScreeningPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddPendingFileUploadStore();
        services.AddAtomicCommandHandler<
            PrepareAdverseActionNoticeCommand,
            PrepareAdverseActionNoticeResult,
            PrepareAdverseActionNoticeHandler>();
        services.AddAtomicCommandHandler<
            CreateAdverseActionNoticeCommand,
            CreateAdverseActionNoticeResult,
            CreateAdverseActionNoticeHandler>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForAccountBootstrapPostgreSql(NpgsqlConnection connection)
    {
        var services = CorePostgreSql(connection);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForPasswordResetPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    internal static ServiceProvider CreateForScanRetryPostgreSql(string connectionString)
    {
        var services = CorePostgreSql(connectionString);
        services.AddScoped<IRequestWriteExecutor, RequestWriteExecutor>();
        return services.BuildServiceProvider();
    }

    private static ServiceCollection Core(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseSqlite(connectionString)
                .AddInterceptors(SqliteDatabaseClockInterceptor.Instance)
                .UseAtomicPersistenceKernel(provider);
            if (interceptors is not null)
            {
                builder.AddInterceptors(interceptors);
            }
        });
        return services;
    }

    private static ServiceCollection CorePostgreSql(
        string connectionString,
        TimeProvider? timeProvider = null,
        IEnumerable<IInterceptor>? interceptors = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }
        services.AddScoped<ICurrentActor, SystemCurrentActor>();
        services.AddAtomicPersistenceKernel();
        services.AddDbContext<RentalCommandDbContext>((provider, builder) =>
        {
            builder.UseNpgsql(connectionString)
                .UseAtomicPersistenceKernel(provider);
            if (interceptors is not null)
            {
                builder.AddInterceptors(interceptors);
            }
        });
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
